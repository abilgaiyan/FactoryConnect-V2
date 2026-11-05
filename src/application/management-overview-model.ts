import { validateRangeSelection } from './date-range-reporting.ts';
import { fleetMachines, type FleetMachine, type FleetAttempt } from './fleet-status.ts';
import type { DashboardRuntimeSource } from './runtime-configuration.ts';
import { isProductionDaySelection } from './production-day-reporting.ts';
export interface OverviewSelection { day: string; site: string; line: string; machine: string }
export function overviewSelection(search: string, sources: readonly DashboardRuntimeSource[]): OverviewSelection {
    const query = new URLSearchParams(search);
    for (const key of query.keys()) if (!['day', 'site', 'line', 'machine'].includes(key) || query.getAll(key).length !== 1) throw new Error('Invalid overview selection.');
    const selection = { day: query.get('day') ?? '', site: query.get('site') ?? '', line: query.get('line') ?? '', machine: (query.get('machine') ?? '').toLowerCase() };
    if (selection.day && !isProductionDaySelection(selection.day)) throw new Error('Select a valid production day.');
    if ((selection.site || selection.line || selection.machine) && !selectedMachines(selection, sources).length) throw new Error('Selection does not match a configured machine.');
    if (selection.day) validateRangeSelection(selection.day, selection.day, selectedMachines(selection, sources).reduce((count, machine) => count + machine.sources.length, 0));
    return selection;
}
export function overviewSearch(selection: OverviewSelection): string {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(selection)) if (value) query.set(key, value);
    return query.size ? `?${query}` : '';
}
export function selectedMachines(selection: OverviewSelection, sources: readonly DashboardRuntimeSource[]): FleetMachine[] {
    return fleetMachines(sources).filter(m => m.sources.some(s => (!selection.site || s.siteId === selection.site) && (!selection.line || s.productionLineId === selection.line)) && (!selection.machine || m.machineId === selection.machine));
}
export function usableState(attempt: FleetAttempt | undefined, conflict = false): string | undefined {
    const response = attempt?.request === 'success' ? attempt.current : undefined;
    const evidence = response?.evidence;
    return !conflict && response?.outcome === 'evidence' && response.coverage === 'complete' && evidence?.freshness === 'current' && evidence.usability === 'current' ? evidence.machineState : undefined;
}
export function overviewCounts(machines: readonly FleetMachine[], attempts: Readonly<Record<string, FleetAttempt>>) {
    const operational: Record<string, number> = { running: 0, idle: 0, fault: 0, stopped: 0, offline: 0, unknown: 0, 'No usable current state': 0 };
    const request = { loading: 0, success: 0, failure: 0 };
    const freshness: Record<string, number> = { current: 0, stale: 0, indeterminate: 0, Unavailable: 0 };
    for (const machine of machines) {
        const attempt = attempts[machine.machineId];
        ++request[attempt?.request ?? 'loading'];
        const state = usableState(attempt, machine.conflict);
        const bucket = state && state in operational ? state : 'No usable current state';
        operational[bucket] = (operational[bucket] ?? 0) + 1;
        const fresh = attempt?.request === 'success' ? attempt.current?.evidence?.freshness ?? 'Unavailable' : 'Unavailable';
        freshness[fresh] = (freshness[fresh] ?? 0) + 1;
    }
    return { operational, request, freshness };
}
