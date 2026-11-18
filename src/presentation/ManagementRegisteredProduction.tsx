import { useEffect, useState } from 'react';
import ManagementProductionSummary from './ManagementProductionSummary.tsx';
import ManagementProductionTrend from './ManagementProductionTrend.tsx';

type Machine = { machineId: string; displayName: string; siteId: string; isRetired: boolean };
export default function ManagementRegisteredProduction({ bearer, businessDate }: { readonly bearer: string | null; readonly businessDate: string }) {
  const [machines, setMachines] = useState<readonly Machine[] | null>(null);
  const [error, setError] = useState('');
  const [selectedSite, setSelectedSite] = useState('');
  const [selectedMachine, setSelectedMachine] = useState('');
  const [inventoryRevision, setInventoryRevision] = useState(0);
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
  }, [bearer, inventoryRevision]);
  const active = machines?.filter(machine => !machine.isRetired) ?? [];
  const sites = [...new Set(active.map(machine => machine.siteId))].sort();
  const effectiveSite = sites.includes(selectedSite) ? selectedSite : '';
  const siteMachines = active.filter(machine => !effectiveSite || machine.siteId === effectiveSite);
  const effectiveMachine = siteMachines.some(machine => machine.machineId === selectedMachine) ? selectedMachine : '';
  const selected = siteMachines.filter(machine => !effectiveMachine || machine.machineId === effectiveMachine);
  return <section aria-label="Registered machine production">
    <h2>Registered machine production</h2>
    <p>SQL-registered machines, independent of live reporting processors. Production quantities are supervisor declarations, not machine-verified OEE.</p>
    {!bearer && <p role="status">Sign in with machine-inventory access to load registered production.</p>}
    {bearer && !machines && !error && <p role="status">Loading registered machine inventory…</p>}
    {error && <p role="alert">{error}. No sample fallback.</p>}
    {bearer && <button type="button" onClick={() => setInventoryRevision(value => value + 1)}>Refresh registered machines</button>}
    {machines && <p>{active.length} active registered machines available for production comparison.</p>}
    {active.length > 0 && <div className="overview-controls">
      <label>Production site <select value={effectiveSite} onChange={event => { setSelectedSite(event.target.value); setSelectedMachine(''); }}><option value="">All registered sites</option>{sites.map(site => <option key={site} value={site}>{site}</option>)}</select></label>
      <label>Production machine <select value={effectiveMachine} onChange={event => setSelectedMachine(event.target.value)}><option value="">All registered machines</option>{siteMachines.map(machine => <option key={machine.machineId} value={machine.machineId}>{machine.displayName}</option>)}</select></label>
      <p>{selected.length} selected registered machines for declared production.</p>
    </div>}
    {selected.length > 0 && <>
      <ManagementProductionSummary machines={selected} businessDate={businessDate} />
      <ManagementProductionTrend machines={selected} businessDate={businessDate} />
    </>}
    {machines && active.length === 0 && <p>No active registered machines.</p>}
  </section>;
}
