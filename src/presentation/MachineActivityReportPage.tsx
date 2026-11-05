import { useEffect, useRef, useState, type FormEvent } from 'react';
import { activityDateRange, loadActivityReport, type ActivityReport } from '../application/machine-activity-report.ts';
import { MachineActivityEvidence } from './MachineActivityEvidence.tsx';
import './machine-activity.css';

export function MachineActivityReportPage({ machineId, timeoutMilliseconds }: { readonly machineId: string; readonly timeoutMilliseconds: number }) {
  const today = () => {
    const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date());
    const part = (kind: string) => parts.find(p => p.type === kind)?.value ?? '';
    return `${part('year')}-${part('month')}-${part('day')}`;
  };
  const [from, setFrom] = useState(today); const [through, setThrough] = useState(today);
  const [report, setReport] = useState<ActivityReport | null>(null);
  const [request, setRequest] = useState<'idle' | 'loading' | 'success' | 'failure'>('idle');
  const [error, setError] = useState('');
  const [readVersion, setReadVersion] = useState(0);
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => { active.current?.abort(); active.current = null; }, []);
  async function submit(event: FormEvent) {
    event.preventDefault(); active.current?.abort();
    const controller = new AbortController(); active.current = controller;
    let timer: ReturnType<typeof setTimeout> | undefined;
    setReport(null); setError('');
    try {
      const range = activityDateRange(from, through); setRequest('loading');
      timer = setTimeout(() => controller.abort(), timeoutMilliseconds);
      const result = await loadActivityReport(machineId, range, controller.signal);
      if (active.current === controller) { setReport(result); setReadVersion(value => value + 1); setRequest('success'); }
    } catch (failure) {
      if (active.current === controller) {
        setRequest('failure'); setError(controller.signal.aborted ? 'Activity request timed out; no report established.' : failure instanceof Error ? failure.message : 'Activity request failed.');
      }
    } finally { if (timer !== undefined) clearTimeout(timer); }
  }
  return <section aria-labelledby="activity-title" className="activity-report">
    <h2 id="activity-title">Machine activity history</h2>
    <p>Dates select IST calendar days, not production days. Closed stored intervals only; historical coverage is not certified. No stored interval does not establish a stopped machine or an acquisition gap.</p>
    <form onSubmit={submit} className="activity-filters">
      <label>From<input type="date" required value={from} onChange={e => setFrom(e.currentTarget.value)} /></label>
      <label>Through (inclusive)<input type="date" required value={through} onChange={e => setThrough(e.currentTarget.value)} /></label>
      <button type="submit">Load activity</button><span>Up to 31 days</span>
    </form>
    <p role="status">{request === 'loading' ? 'Loading activity…' : request === 'success' ? 'Activity read succeeded.' : ''}</p>
    {error && <p role="alert">{error}</p>}
    {report && <MachineActivityEvidence key={readVersion} report={report} />}
  </section>;
}
