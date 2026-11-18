import { useEffect, useMemo, useState } from 'react';
import ManagementProductionSummary from './ManagementProductionSummary.tsx';
import ManagementProductionTrend from './ManagementProductionTrend.tsx';

type Machine = { machineId: string; displayName: string; siteId: string; isRetired: boolean };
export default function ManagementRegisteredProduction({ businessDate }: { readonly businessDate: string }) {
  const [machines, setMachines] = useState<readonly Machine[] | null>(null);
  const [error, setError] = useState('');
  const [selectedSite, setSelectedSite] = useState('');
  const [selectedMachine, setSelectedMachine] = useState('');
  const [inventoryRevision, setInventoryRevision] = useState(0);
  useEffect(() => {
    setMachines(null);
    setError('');
    const controller = new AbortController();
    void fetch('/api/reporting/v2/machines', {
      credentials: 'include',
      headers: { Accept: 'application/json' },
      signal: controller.signal,
    }).then(async response => {
      if (response.status === 401) throw new Error('Your session is not authorized for registered-machine inventory. Sign in again');
      if (response.status === 403) throw new Error('The site-scoped reporting machine list is not available for this session');
      if (!response.ok) throw new Error('Registered machine inventory unavailable (HTTP ' + response.status + ')');
      const data: unknown = await response.json();
      if (typeof data !== 'object' || data === null || !('siteId' in data) || typeof data.siteId !== 'string' ||
        !('source' in data) || data.source !== 'sql-master' || !('isSample' in data) || data.isSample !== false ||
        !('machines' in data) || !Array.isArray(data.machines) ||
        !data.machines.every((m: unknown) => typeof m === 'object' && m !== null &&
          'machineId' in m && typeof m.machineId === 'string' && 'displayName' in m &&
          typeof m.displayName === 'string'))
        throw new Error('Registered machine inventory response is invalid');
      if (!controller.signal.aborted) setMachines((data.machines as { machineId: string; displayName: string }[]).map(machine => ({ ...machine, siteId: data.siteId as string, isRetired: false })));
    }).catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'Inventory unavailable'); });
    return () => controller.abort();
  }, [inventoryRevision]);
  // Keep the selected-machine array stable while SQL reports load. Both child reports
  // depend on its identity to cancel stale requests and avoid repeated fetches.
  const active = useMemo(() => machines?.filter(machine => !machine.isRetired) ?? [], [machines]);
  const sites = useMemo(() => [...new Set(active.map(machine => machine.siteId))].sort(), [active]);
  const staleSite = selectedSite !== '' && !sites.includes(selectedSite);
  const siteMachines = useMemo(() => active.filter(machine => !selectedSite || machine.siteId === selectedSite), [active, selectedSite]);
  const staleMachine = selectedMachine !== '' && !siteMachines.some(machine => machine.machineId === selectedMachine);
  const staleSelection = staleSite || staleMachine;
  // Never silently expand a machine-specific report to all machines.
  const selected = useMemo(() => staleSelection ? [] : siteMachines.filter(machine => !selectedMachine || machine.machineId === selectedMachine), [staleSelection, siteMachines, selectedMachine]);
  return <section aria-label="Registered machine production">
    <h2>Registered machine production</h2>
    <p>Active SQL-registered machines for the authenticated site, independent of live reporting processors. Production quantities are supervisor declarations, not machine-verified OEE.</p>
    {!machines && !error && <p role="status">Loading registered machine inventory…</p>}
    {error && <p role="alert">{error}. No sample fallback.</p>}
    {<button type="button" onClick={() => setInventoryRevision(value => value + 1)}>Refresh registered machines</button>}
    {machines && <p>{active.length} active registered machines available for production comparison.</p>}
    {active.length > 0 && <div className="overview-controls">
      <label>Production site <select value={selectedSite} onChange={event => { setSelectedSite(event.target.value); setSelectedMachine(''); }}><option value="">All registered sites</option>{sites.map(site => <option key={site} value={site}>{site}</option>)}</select></label>
      <label>Production machine <select value={selectedMachine} onChange={event => setSelectedMachine(event.target.value)}><option value="">All registered machines</option>{siteMachines.map(machine => <option key={machine.machineId} value={machine.machineId}>{machine.displayName}</option>)}</select></label>
      {staleSelection && <p role="alert">The selected site or machine is no longer active. Reports are withheld until you choose a new selection.</p>}
      {staleSelection && <button type="button" onClick={() => { setSelectedSite(''); setSelectedMachine(''); }}>Clear unavailable selection</button>}
      <p>{selected.length} selected registered machines for declared production.</p>
    </div>}
    {selected.length > 0 && <>
      <ManagementProductionSummary machines={selected} businessDate={businessDate} />
      <ManagementProductionTrend machines={selected} businessDate={businessDate} />
    </>}
    {machines && active.length === 0 && <p>No active registered machines.</p>}
  </section>;
}
