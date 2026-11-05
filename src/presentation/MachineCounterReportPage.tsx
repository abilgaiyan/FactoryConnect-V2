import { useEffect, useRef, useState, type FormEvent } from 'react';
import { hasCounterComparisonIssue, summarizeCounterDiagnostics } from '../application/counter-diagnostics.ts';
import { activityDateRange } from '../application/machine-activity-report.ts';
import { loadCounterReport, type CounterReport } from '../application/machine-counter-report.ts';
import { formatReportTimestamp } from './date-formatting.ts';
import './machine-activity.css';

export function MachineCounterReportPage({ machineId, timeoutMilliseconds }: { readonly machineId: string; readonly timeoutMilliseconds: number }) {
  const today = () => {
    const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date());
    const part = (kind: string) => parts.find(p => p.type === kind)?.value ?? '';
    return `${part('year')}-${part('month')}-${part('day')}`;
  };
  const [from, setFrom] = useState(today); const [through, setThrough] = useState(today);
  const [source, setSource] = useState(''); const [address, setAddress] = useState('');
  const [signals, setSignals] = useState<CounterReport['availableSignals'] | null>(null);
  const [report, setReport] = useState<CounterReport | null>(null);
  const [request, setRequest] = useState<'idle' | 'loading' | 'success' | 'failure'>('idle'); const [error, setError] = useState('');
  const [issuesOnly, setIssuesOnly] = useState(false);
  const [page, setPage] = useState(1); const [pageSize, setPageSize] = useState(50);
  const active = useRef<AbortController | null>(null);
  const preferenceKey = `factoryconnect:counter-selection:${machineId}`;
  const preferred = useRef<{ source: string; address: string } | null>(null);
  useEffect(() => {
    try {
      const saved = JSON.parse(window.localStorage.getItem(preferenceKey) ?? 'null');
      preferred.current = saved && typeof saved.source === 'string' && typeof saved.address === 'string' ? saved : null;
    } catch { preferred.current = null; }
    void read(true);
    return () => { active.current?.abort(); active.current = null; };
  }, [machineId]);
  function selectSignal(nextSource: string, nextAddress: string) {
    setSource(nextSource); setAddress(nextAddress);
    preferred.current = nextSource && nextAddress ? { source: nextSource, address: nextAddress } : null;
    if (preferred.current) {
      try { window.localStorage.setItem(preferenceKey, JSON.stringify(preferred.current)); } catch { /* Selection works when browser storage is unavailable. */ }
    }
  }
  function changeDates(nextFrom: string, nextThrough: string) {
    active.current?.abort(); active.current = null;
    setFrom(nextFrom); setThrough(nextThrough); setSignals(null);
    setSource(''); setAddress(''); setReport(null); setRequest('idle'); setError('');
  }
  async function read(discover: boolean) {
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    let timer: ReturnType<typeof setTimeout> | undefined; setReport(null); setError('');
    if (discover) { setSignals(null); setSource(''); setAddress(''); }
    try {
      const range = activityDateRange(from, through);
      if (!discover && !signals?.some(s => s.source === source && s.address === address))
        throw new Error('Find signals and select an exact source and address first.');
      setRequest('loading');
      timer = setTimeout(() => controller.abort(), timeoutMilliseconds);
      controller.signal.addEventListener('abort', () => { if (timer !== undefined) clearTimeout(timer); }, { once: true });
      // The existing exact-query API also returns range-wide signal discovery.
      // Probe samples are discarded; the probe is not a selected quantity mapping.
      const selection = { ...range, source: discover ? 'mtconnect' : source, address: discover ? 'part_count' : address };
      const result = await loadCounterReport(machineId, selection, controller.signal);
      if (active.current === controller) {
        if (discover) {
          setSignals(result.availableSignals);
          const selected = result.availableSignals.find(s => s.source === preferred.current?.source && s.address === preferred.current?.address)
            ?? result.availableSignals[0];
          selectSignal(selected?.source ?? '', selected?.address ?? '');
        }
        else { setReport(result); setPage(1); setIssuesOnly(false); }
        setRequest('success');
      }
    } catch (failure) {
      if (active.current === controller) { setRequest('failure'); setError(controller.signal.aborted ? 'Counter request timed out; no report established.' : failure instanceof Error ? failure.message : 'Counter request failed.'); }
    } finally { if (timer !== undefined) clearTimeout(timer); }
  }
  async function submit(event: FormEvent) { event.preventDefault(); await read(false); }
  const sources = [...new Set((signals ?? []).map(s => s.source))];
  const addresses = (signals ?? []).filter(s => s.source === source).map(s => s.address);
  const summary = summarizeCounterDiagnostics(report?.samples ?? []);
  const visibleSamples = (report?.samples ?? []).filter(s => !issuesOnly || hasCounterComparisonIssue(s));
  const count = visibleSamples.length; const pages = Math.max(1, Math.ceil(count / pageSize));
  const visiblePage = Math.min(page, pages); const start = (visiblePage - 1) * pageSize;
  return <section className="activity-report" aria-labelledby="counter-title">
    <h2 id="counter-title">Raw counter inspection</h2>
    <p>Inspect the machine's quantity signal using its exact source and address. Signals are discovered automatically on opening this page. The previous available selection or first discovered signal is selected for convenience; change it before loading if needed. Use Find signals after changing dates. Discovery is not an approved quantity mapping. Numeric and Enumeration signals are listed for discovery; their type or integer text does not establish produced quantity.</p>
    <p>Arithmetic differences are diagnostic only. Continuity, counter meaning and shift attribution are not established; production totals are withheld.</p>
    <form onSubmit={submit} className="activity-filters">
      <label>Counter from<input type="date" required value={from} onChange={e => changeDates(e.currentTarget.value, through)} /></label>
      <label>Counter through (inclusive)<input type="date" required value={through} onChange={e => changeDates(from, e.currentTarget.value)} /></label>
      <button type="button" onClick={() => void read(true)}>Find signals</button>
      <label>Exact source<select required value={source} disabled={sources.length === 0} onChange={e => { const nextSource = e.currentTarget.value; selectSignal(nextSource, (signals ?? []).find(s => s.source === nextSource)?.address ?? ''); }}>
        <option value="">Select source</option>{sources.map(value => <option key={value} value={value}>{value}</option>)}
      </select></label>
      <label>Exact address<select required value={address} disabled={addresses.length === 0} onChange={e => selectSignal(source, e.currentTarget.value)}>
        <option value="">Select address</option>{addresses.map(value => <option key={value} value={value}>{value}</option>)}
      </select></label>
      <button type="submit" disabled={!signals?.some(s => s.source === source && s.address === address)}>Load counter</button>
    </form>
    <p role="status">{request === 'loading' ? 'Loading raw counter signals or rows…' : request === 'success' ? report ? 'Counter read succeeded.' : 'Signal discovery succeeded. Review the source and address, then load the counter.' : ''}</p>
    {error && <p role="alert">{error}</p>}
    {signals !== null && signals.length === 0 && <p>No Numeric or Enumeration signals returned for these dates. This does not establish zero production or an acquisition gap.</p>}
    {report && <>
      <p>Loaded signal: {report.source} / {report.address}. Stream {report.streamKey}; raw position {report.throughPosition}; acquisition revision {report.acquisitionRevision}.</p>
      <p>Selected IST calendar interval: {formatReportTimestamp(report.fromInclusive)} – {formatReportTimestamp(report.toExclusive)} (exclusive end). Read as of: <time dateTime={report.readAsOf}>{formatReportTimestamp(report.readAsOf)}</time>.</p>
      <section aria-labelledby="counter-summary-title">
        <h3 id="counter-summary-title">Diagnostic summary</h3>
        <p>Counts cover all loaded observations. Comparison categories reconcile to the observation count; flag counts overlap. Increases are not admitted produced quantity. A decrease does not establish its cause or a confirmed reset.</p>
        <div className="activity-totals">
          <div><strong>Stored observations</strong><span>{summary.sampleCount}</span></div>
          <div><strong>Increasing comparisons</strong><span>{summary.increases}</span></div>
          <div><strong>Unchanged comparisons</strong><span>{summary.unchanged}</span></div>
          <div><strong>No arithmetic comparison</strong><span>{summary.notComparable}</span></div>
          <div><strong>Rows with diagnostic flags</strong><span>{summary.issueRows}</span></div>
        </div>
        <dl>{summary.flags.map(entry => <div key={entry.flag}><dt>{entry.flag}</dt><dd>{entry.count}</dd></div>)}</dl>
      </section>
      <label><input type="checkbox" checked={issuesOnly} onChange={e => { setIssuesOnly(e.currentTarget.checked); setPage(1); }} />Show only rows with diagnostic flags (includes baseline-only readings)</label>
      <p>Only the table is filtered and paged. Summary counts always cover all {summary.sampleCount} loaded observations.</p>
      {summary.sampleCount === 0 && <p>No stored observations for this exact signal and selection at the pinned boundary. This does not establish zero production or an acquisition gap.</p>}
      {summary.sampleCount > 0 && count === 0 && <p>No rows match this table filter. Loaded observations remain in the summary.</p>}
      <nav className="activity-pagination" aria-label="Counter table pagination">
        <label>Counter rows per page<select value={pageSize} onChange={e => { const size = Number(e.currentTarget.value); if ([25,50,100].includes(size)) { setPageSize(size); setPage(1); } }}><option value={25}>25</option><option value={50}>50</option><option value={100}>100</option></select></label>
        <p role="status">Rows {count === 0 ? 0 : start + 1}–{Math.min(start + pageSize, count)} of {count} · Page {visiblePage} of {pages}</p>
        <button type="button" aria-controls="counter-table" disabled={visiblePage === 1} onClick={() => setPage(visiblePage - 1)}>Previous counter page</button>
        <button type="button" aria-controls="counter-table" disabled={visiblePage === pages} onClick={() => setPage(visiblePage + 1)}>Next counter page</button>
      </nav>
      <div className="activity-table-scroll" role="region" aria-label="Exact raw counter observations" tabIndex={0}>
        <table id="counter-table"><caption>Raw values and diagnostic flags; no admitted quantities</caption><thead><tr><th>Observed at (IST)</th><th>Raw value</th><th>Quality / type</th><th>Arithmetic difference</th><th>Flags</th><th>Provenance</th></tr></thead>
          <tbody>{visibleSamples.slice(start, start + pageSize).map(s => <tr key={s.position}>
            <td><time dateTime={s.observedAtUtc}>{formatReportTimestamp(s.observedAtUtc)}</time></td><td>{s.rawValue ?? 'Missing value'}</td>
            <td>{s.quality} / {s.signalType}</td><td>{s.arithmeticDifference ?? 'Not established'}</td><td><ul>{s.flags.map(f => <li key={f}>{f}</li>)}</ul></td>
            <td><details><summary>Position {s.position}</summary><p>Instance {s.instanceId}; sequence {s.sequence}; predecessor position {s.previousPosition ?? 'None'}.</p><p>Exact UTC: {s.observedAtUtc}</p></details></td>
          </tr>)}</tbody></table>
      </div>
    </>}
    <p>Management's proposed all-good Quality policy means good quantity equals admitted produced quantity and rejected quantity defaults to zero. It is a policy assumption, separate from measured good quantity. This inspection does not apply that policy or publish Quality/OEE.</p>
  </section>;
}

