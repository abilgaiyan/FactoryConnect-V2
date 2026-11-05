import type { DashboardRuntimeSource } from './runtime-configuration.ts';
import type { CurrentMachineStateResponse, CurrentStateClient } from '../api/current-state/current-state-client.ts';
export interface FleetMachine {
    machineId: string;
    sources: readonly DashboardRuntimeSource[];
    conflict: boolean;
}
export interface FleetAttempt {
    request: 'loading' | 'success' | 'failure';
    current?: CurrentMachineStateResponse;
    retained?: CurrentMachineStateResponse;
    error?: string;
}
export function fleetMachines(sources: readonly DashboardRuntimeSource[]): FleetMachine[] {
    const groups = new Map<string, DashboardRuntimeSource[]>();
    for (const source of sources) {
        const id = source.machineId.toLowerCase();
        groups.set(id, [...(groups.get(id) ?? []), source]);
    }
    return [...groups].map(([machineId, values]) => ({ machineId, sources: values,
        conflict: new Set(values.map(s => JSON.stringify([s.siteId, s.productionLineId, s.displayName, s.groupName, s.displayOrder]))).size > 1 }));
}
export function fleetCounts(machines: readonly FleetMachine[], attempts: Readonly<Record<string, FleetAttempt>>) {
    const groups: Record<string, Record<string, number>> = {
        state: { running: 0, idle: 0, stopped: 0, unknown: 0, fault: 0, 'No evidence': 0, 'State unavailable': 0 },
        freshness: { current: 0, stale: 0, indeterminate: 0, Unavailable: 0 },
        usability: { current: 0, stale: 0, behind: 0, indeterminate: 0, Unavailable: 0 },
        request: { loading: 0, success: 0, failure: 0 },
    };
    for (const machine of machines) {
        const attempt = attempts[machine.machineId];
        const evidence = attempt?.request === 'success' ? attempt.current?.evidence : undefined;
        const buckets = { state: evidence?.machineState ?? (attempt?.request === 'success' ? 'No evidence' : 'State unavailable'),
            freshness: evidence?.freshness ?? 'Unavailable', usability: evidence?.usability ?? 'Unavailable', request: attempt?.request ?? 'loading' };
        for (const [group, bucket] of Object.entries(buckets)) {
            const counts = groups[group]!;
            counts[bucket] = (counts[bucket] ?? 0) + 1;
        }
    }
    return groups;
}
// One generation per refresh; retained values never count as current-attempt evidence.
export class FleetRefresh {
    private generation = 0;
    private active: AbortController | undefined;
    private attempts: Record<string, FleetAttempt> = {};
    private machines: readonly FleetMachine[];
    private client: CurrentStateClient;
    private publish: (value: Record<string, FleetAttempt>) => void;
    constructor(machines: readonly FleetMachine[], client: CurrentStateClient, publish: (value: Record<string, FleetAttempt>) => void) {
        this.machines = machines;
        this.client = client;
        this.publish = publish;
    }
    dispose() { ++this.generation; this.active?.abort(); }
    async refresh() {
        this.active?.abort();
        const controller = new AbortController();
        this.active = controller;
        const generation = ++this.generation;
        this.attempts = Object.fromEntries(this.machines.map(m => {
            const old = this.attempts[m.machineId];
            const retained = old?.current ?? old?.retained;
            return [m.machineId, { request: 'loading', ...(retained ? { retained } : {}) }];
        }));
        this.publish({ ...this.attempts });
        const pending = [...this.machines];
        const worker = async () => {
            while (pending.length && generation === this.generation && !controller.signal.aborted) {
                const machine = pending.shift()!;
                let result: FleetAttempt;
                const retained = this.attempts[machine.machineId]?.retained;
                try {
                    const current = await this.client.read(machine.machineId, { signal: controller.signal });
                    if (current.machineId.toLowerCase() !== machine.machineId)
                        throw new Error('Response machine identity mismatch');
                    result = { request: 'success', current };
                }
                catch (error) {
                    result = { request: 'failure', error: error instanceof Error ? error.message : 'Request failed', ...(retained ? { retained } : {}) };
                }
                if (generation !== this.generation || controller.signal.aborted)
                    return;
                this.attempts = { ...this.attempts, [machine.machineId]: result };
                this.publish(this.attempts);
            }
        };
        await Promise.all(Array.from({ length: Math.min(4, pending.length) }, worker));
    }
}
