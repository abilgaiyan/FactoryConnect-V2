import { useEffect, useState } from 'react';

type LegacyMachine = { sourceMachineId: string; deviceKey: string; sourceDatabase: string; v2MachineId: string | null };
type LegacyMetric = { metricKey: string; status: number; metricValue: string | null; unit: string | null; productionBusinessDate: string | null };
const source = 'imported-v1-legacy';
function verified(data: any): boolean {
  return data && data.source === source && data.evidenceKind === 'historical-reporting-only' &&
    data.liveStatus === 'not-established' && data.isSample === false;
}
export default function ManagementImportedHistory({ businessDate }: { readonly businessDate: string }) {
  const [machines, setMachines] = useState<LegacyMachine[] | null>(null);
  const [machineId, setMachineId] = useState('');
  const [metrics, setMetrics] = useState<LegacyMetric[] | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    void fetch('/api/reporting/v2/legacy/machines', { credentials: 'include', cache: 'no-store', signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw Error('Historical machine inventory unavailable (HTTP ' + response.status + ')');
        const data: unknown = await response.json();
        if (!verified(data) || !Array.isArray((data as any).machines) ||
          !(data as any).machines.every((m: any) => typeof m.sourceMachineId === 'string' &&
            typeof m.deviceKey === 'string' && typeof m.sourceDatabase === 'string'))
          throw Error('Historical machine inventory contract mismatch');
        if (!controller.signal.aborted) setMachines((data as any).machines);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, []);
  useEffect(() => { setMetrics(null); setError(''); }, [machineId, businessDate]);
  async function load() {
    if (!machineId || !businessDate || !/^\d{4}-\d{2}-\d{2}$/.test(businessDate)) return;
    setLoading(true); setError(''); setMetrics(null);
    try {
      const url = '/api/reporting/v2/legacy/machines/' + encodeURIComponent(machineId) +
        '/metrics?from=' + encodeURIComponent(businessDate) + '&through=' + encodeURIComponent(businessDate);
      const response = await fetch(url, { credentials: 'include', cache: 'no-store' });
      if (!response.ok) throw Error('Historical metric read unavailable (HTTP ' + response.status + ')');
      const data: unknown = await response.json();
      if (!verified(data) || (data as any).machineId?.toLowerCase() !== machineId.toLowerCase() ||
        !Array.isArray((data as any).metrics)) throw Error('Historical metric evidence contract mismatch');
      setMetrics((data as any).metrics);
    } catch (e) { setError(String(e)); }
    finally { setLoading(false); }
  }
  const machine = machines?.find(m => m.sourceMachineId === machineId);
  const calculated = metrics?.filter(m => m.status === 0 && m.metricValue !== null &&
    Number.isFinite(Number(m.metricValue))) ?? [];
  const unavailable = metrics?.length !== undefined ? metrics.length - calculated.length : 0;
  return <section aria-label="Imported V1 historical management summary">
    <h2>Imported V1 historical reporting</h2>
    <p>Historical SQL projections only. Not live machine status, V2 supervisor production, or qualified V2 OEE. Original V1 shift timing is retained.</p>
    <p><a href="/reports/historical">Open full History report (activity, shifts and detailed metrics)</a></p>
    {machines && machines.length === 0 && <p>No imported V1 machines are registered.</p>}
    {machines && machines.length > 0 && <label>Historical source machine{' '}
      <select value={machineId} onChange={e => setMachineId(e.target.value)}>
        <option value="">Select imported machine</option>
        {machines.map(m => <option key={m.sourceDatabase + ':' + m.sourceMachineId} value={m.sourceMachineId}>{m.deviceKey} ({m.sourceDatabase})</option>)}
      </select>
    </label>}
    {machine && <p>Imported source: {machine.sourceDatabase}. V2 machine mapping: {machine.v2MachineId ?? 'not established'}.</p>}
    {!businessDate && <p>Select a production day above to inspect imported historical projections.</p>}
    {machineId && businessDate && <button type="button" disabled={loading} onClick={() => { void load(); }}>
      {loading ? 'Loading historical metrics…' : 'Load imported metrics for ' + businessDate}
    </button>}
    {error && <p role="alert">{error}. Historical data is not replaced with sample values.</p>}
    {metrics && <div>
      <p>{metrics.length} imported projections · {calculated.length} numerical source results · {unavailable} unavailable/non-numerical results. These counts do not imply complete shift coverage.</p>
      {calculated.length === 0 ? <p>No numerical historical projections for the selected date.</p> :
        <table><thead><tr><th>Historical metric</th><th>Source value</th><th>Source date</th></tr></thead>
          <tbody>{calculated.map((m, i) => <tr key={i}><td>{m.metricKey}</td><td>{m.metricValue} {m.unit}</td><td>{m.productionBusinessDate ?? 'Recorded shift window'}</td></tr>)}</tbody></table>}
      <p>Performance, Quality and OEE must not be inferred from Availability or Utilization.</p>
    </div>}
  </section>;
}
