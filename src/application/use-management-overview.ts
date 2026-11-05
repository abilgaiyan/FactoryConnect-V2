import { useEffect, useMemo, useState } from 'react';
import type { DashboardApplicationRuntime } from './application-runtime.ts';
import { FleetRefresh, type FleetAttempt, type FleetMachine } from './fleet-status.ts';
import { loadRange, type RangeRow } from './date-range-reporting.ts';
export type OverviewReport = { kind: 'loading' | 'success' | 'failure'; rows?: RangeRow[]; error?: string };
export function useManagementOverview(runtime: DashboardApplicationRuntime, machines: readonly FleetMachine[], day: string) {
    const [attempts, setAttempts] = useState<Record<string, FleetAttempt>>({});
    const [reports, setReports] = useState<Record<string, OverviewReport>>({});
    const [refreshKey, setRefreshKey] = useState(0);
    const fleet = useMemo(() => new FleetRefresh(machines, runtime.currentStateClient, setAttempts), [machines, runtime.currentStateClient]);
    useEffect(() => { void fleet.refresh(); return () => fleet.dispose(); }, [fleet, refreshKey]);
    useEffect(() => {
        const controller = new AbortController();
        setReports({});
        if (!day) return () => controller.abort();
        const pending = [...machines];
        // Independent per-machine results, with at most four active reporting loads.
        async function worker() {
            while (pending.length && !controller.signal.aborted) {
                const machine = pending.shift()!;
                setReports(old => ({ ...old, [machine.machineId]: { kind: 'loading' } }));
                let result: OverviewReport;
                try {
                    if (machine.conflict) throw new Error('Configuration conflict; reporting selection withheld.');
                    const rows = await loadRange(day, day, machine.sources, runtime.reportingClient, controller.signal);
                    result = { kind: 'success', rows };
                } catch (error) {
                    result = { kind: 'failure', error: error instanceof Error ? error.message : 'Reporting request failed' };
                }
                if (!controller.signal.aborted) setReports(old => ({ ...old, [machine.machineId]: result }));
            }
        }
        void Promise.all(Array.from({ length: Math.min(4, pending.length) }, worker));
        return () => controller.abort();
    }, [machines, day, runtime.reportingClient, refreshKey]);
    return { attempts, reports, refresh: () => setRefreshKey(key => key + 1) };
}
