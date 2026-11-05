import { overviewSelection, overviewSearch } from '../application/management-overview-model.ts';
import type { DashboardRuntimeSource } from '../application/runtime-configuration.ts';
export function overviewReturn(search: string, sources: readonly DashboardRuntimeSource[]): string | undefined {
    const context = new URLSearchParams(search).get('overview');
    if (context === null) return undefined;
    try { return `/overview${overviewSearch(overviewSelection(context, sources))}`; }
    catch { return undefined; }
}
