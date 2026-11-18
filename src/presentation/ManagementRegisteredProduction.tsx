import { useEffect, useState } from 'react';
import ManagementProductionSummary from './ManagementProductionSummary.tsx';
import ManagementProductionTrend from './ManagementProductionTrend.tsx';

type Machine = { machineId: string; displayName: string; siteId: string; isRetired: boolean };
export default function ManagementRegisteredProduction({ bearer, businessDate }: { readonly bearer: string | null; readonly businessDate: string }) {
  const [machines, setMachines] = useState<readonly Machine[] | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    setMachines(null);
    setError('');
    if (!bearer) return;
    const controller = new AbortController();
    void fetch('/api/admin/v2/machines', {
      headers: { Authorization: 'Bearer ' + bearer, Accept: 'application/json' },
      signal: controller.signal,
    }).then(async response => {
      if (!response.ok) throw new Error('Registered machine inventory unavailable (HTTP ' + response.status + ')');
      const data: unknown = await response.json();
      if (typeof data !== 'object' || data === null || !('machines' in data) || !Array.isArray(data.machines) ||
        !data.machines.every((m: unknown) => typeof m === 'object' && m !== null &&
          'machineId' in m && typeof m.machineId === 'string' && 'displayName' in m &&
          typeof m.displayName === 'string' && 'siteId' in m && typeof m.siteId === 'string' &&
          'isRetired' in m && typeof m.isRetired === 'boolean'))
        throw new Error('Registered machine inventory response is invalid');
      if (!controller.signal.aborted) setMachines(data.machines as Machine[]);
    }).catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'Inventory unavailable'); });
    return () => controller.abort();
  }, [bearer]);
  const active = machines?.filter(machine => !machine.isRetired) ?? [];
  return <section aria-label="Registered machine production">
    <h2>Registered machine production</h2>
    <p>SQL-registered machines, independent of live reporting processors. Production quantities are supervisor declarations, not machine-verified OEE.</p>
    {!bearer && <p role="status">Sign in with machine-inventory access to load registered production.</p>}
    {bearer && !machines && !error && <p role="status">Loading registered machine inventory…</p>}
    {error && <p role="alert">{error}. No sample fallback.</p>}
    {machines && <p>{active.length} active registered machines available for production comparison.</p>}
    {active.length > 0 && <>
      <ManagementProductionSummary machines={active} businessDate={businessDate} />
      <ManagementProductionTrend machines={active} businessDate={businessDate} />
    </>}
    {machines && active.length === 0 && <p>No active registered machines.</p>}
  </section>;
}
