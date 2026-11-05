import { formatRatioAsPercentage } from '../application/production-day-metric-formatting.ts';
import { formatReportDate } from "./date-formatting.ts";
import { useEffect, useRef, useState } from 'react';
import type { DashboardApplicationRuntime } from '../application/application-runtime.ts';
import { loadRange, trendSegments, reportingIdentity, plottedValue, rangeMetrics, maximumRangeDays, maximumSourceDays, type RangeRow } from '../application/date-range-reporting.ts';
export function DateRangePage({ runtime }: {
    runtime: DashboardApplicationRuntime;
}) {
    // A local civil date is a selection default, not production-calendar authority.
    const [defaultDate] = useState(() => {
        const now = new Date();
        return `${String(now.getFullYear()).padStart(4, '0')}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    });
    const initialQuery = new URLSearchParams(window.location.search);
    const [from, setFrom] = useState(initialQuery.get('from') ?? defaultDate);
    const [through, setThrough] = useState(initialQuery.get('through') ?? defaultDate);
    const [machines, setMachines] = useState<string[]>(initialQuery.has('machine') ? [initialQuery.get('machine')!.toLowerCase()] : [...new Set(runtime.configuration.sources.map(s => s.machineId.toLowerCase()))]);
    const [state, setState] = useState<{
        kind: 'idle' | 'loading' | 'success' | 'failure';
        rows?: RangeRow[];
        error?: string;
    }>({ kind: 'idle' });
    const active = useRef<AbortController | undefined>(undefined);
    const generation = useRef(0);
    useEffect(() => () => { ++generation.current; active.current?.abort(); }, []);
    function invalidate() { ++generation.current; active.current?.abort(); setState({ kind: 'idle' }); }
    async function submit() {
        active.current?.abort();
        const controller = new AbortController();
        active.current = controller;
        const own = ++generation.current;
        setState({ kind: 'loading' });
        try {
            const sources = runtime.configuration.sources.filter(s => machines.includes(s.machineId.toLowerCase()));
            if (!sources.length)
                throw new Error('Select at least one machine.');
            const rows = await loadRange(from, through, sources, runtime.reportingClient, controller.signal);
            if (own === generation.current && !controller.signal.aborted)
                setState({ kind: 'success', rows });
        }
        catch (error) {
            if (own === generation.current && !controller.signal.aborted)
                setState({ kind: 'failure', error: error instanceof Error ? error.message : 'Request failed' });
        }
    }
    const rows = state.rows ?? [];
    const series = new Map<string, RangeRow[]>();
    for (const row of rows) {
        const key = JSON.stringify([row.source.machineId, row.source.processorId]);
        const values = series.get(key) ?? [];
        values.push(row);
        series.set(key, values);
    }
    return <section className="fleet-range"><h1>Date-range results and trends</h1><p>Ongoing day: Undetermined — calendar provenance unavailable. Range ratios withheld — operand evidence unavailable.</p>
    <p>Selection limit: {maximumRangeDays} inclusive days and {maximumSourceDays} reporting-source/day combinations. Each configured processor counts as a reporting source.</p>
    <form onSubmit={e => { e.preventDefault(); void submit(); }}><label>From <input type="date" required value={from} onChange={e => { invalidate(); setFrom(e.target.value); }}/></label>
    <label>Through (inclusive) <input type="date" required value={through} onChange={e => { invalidate(); setThrough(e.target.value); }}/></label>
    <fieldset><legend>Machines</legend>{[...new Set(runtime.configuration.sources.map(s => s.machineId.toLowerCase()))].map(id => <label key={id}><input type="checkbox" checked={machines.includes(id)} onChange={e => { invalidate(); setMachines(old => e.target.checked ? [...old, id] : old.filter(x => x !== id)); }}/>{id}</label>)}</fieldset>
    <button type="submit">Load range</button></form>
    {state.kind === 'loading' && <p role="status">Loading all selected reporting pages…</p>}{state.kind === 'failure' && <p role="alert">Request failed: {state.error}. Presence or absence has not been established for this selection.</p>}
    {[...series].map(([key, values]) => <section key={key}><h2>{values[0]!.source.displayName} — processor {values[0]!.source.processorId}</h2><p>Machine {values[0]!.source.machineId}; site {values[0]!.source.siteId}</p>
      {[...new Set([...rangeMetrics.map(name => JSON.stringify([name, '1.0', 'ratio'])), ...values.flatMap(row => row.items.map(i => JSON.stringify([i.metricKey, i.definitionVersion, i.unit])))])].map(metric => {
                const [name, version, unit] = JSON.parse(metric) as string[];
                const points = values.map(row => { const item = row.items.find(i => i.metricKey === name && i.definitionVersion === version && i.unit === unit); return plottedValue(item); });
                const real = points.filter((p): p is number => p !== null);
                const min = real.reduce((a, b) => Math.min(a, b), Infinity);
                const max = real.reduce((a, b) => Math.max(a, b), -Infinity);
                const scale = max - min || 1;
                const x = (i: number) => 30 + i * 540 / Math.max(1, values.length - 1);
                const y = (v: number) => 150 - (v - min) * 120 / scale;
                return <figure key={metric}><figcaption>{name} {version} ({unit}) — breaks indicate unavailable values. Coordinates are approximate; dates and exact details appear below.</figcaption><svg style={{ maxWidth: '60rem', width: '100%' }} role="img" aria-label={`${name} trend with gaps; exact values in table`} viewBox="0 0 600 210">
          <text x="30" y="200">{formatReportDate(values[0]!.day)}</text><text x="570" y="200" textAnchor="end">{formatReportDate(values[values.length - 1]!.day)}</text>
          <text x="0" y="15">{real.length ? (unit === 'ratio' ? `${formatRatioAsPercentage(max)}%` : max) : 'No calculated values'}</text><text x="0" y="170">{real.length ? (unit === 'ratio' ? `${formatRatioAsPercentage(min)}%` : min) : ''}</text>
          {trendSegments(points).map((segment, n) => <g key={n}><polyline fill="none" stroke="currentColor" points={segment.map(p => `${x(p.x)},${y(p.y)}`).join(' ')}/>{segment.map(p => <circle key={p.x} cx={x(p.x)} cy={y(p.y)} r="3"><title>{formatReportDate(values[p.x]!.day)}: {values[p.x]!.items.find(i => i.metricKey === name && i.definitionVersion === version && i.unit === unit)?.value} {unit}</title></circle>)}</g>)}
          </svg></figure>;
            })}
      <div className="report-table-scroll" role="region" aria-label="Daily reporting results" tabIndex={0}><table><caption>Exact daily reporting results</caption><thead><tr><th>Day</th><th>Metric / definition</th><th>Status / value / unit</th><th>Evidence reasons</th><th>Source revision</th></tr></thead><tbody>{values.flatMap(row => row.items.length ? row.items.map(item => <tr key={reportingIdentity(item)}><td>{formatReportDate(row.day)}</td><td>{item.metricKey} / {item.definitionVersion}</td><td>{item.status}: {item.status === 'calculated' && item.value !== null && item.unit === 'ratio' ? `${formatRatioAsPercentage(item.value)}%` : `${item.value ?? '—'} ${item.unit}`}<br/><small>API value: {item.value ?? '—'} {item.unit}</small></td><td>{item.reasonCode ?? '—'}; operand {item.reasonOperandName ?? '—'}</td><td>{item.sourceRevision.processorId} / {item.sourceRevision.machineId} / {item.sourceRevision.streamKey} / {String(item.sourceRevision.position)}</td></tr>).concat(rangeMetrics.filter(name => !row.items.some(item => item.metricKey === name && item.definitionVersion === '1.0')).map(name => <tr key={`${row.day}-${name}-missing`}><td>{formatReportDate(row.day)}</td><td>{name} / 1.0</td><td colSpan={3}>No reporting result</td></tr>)) : [<tr key={row.day}><td>{formatReportDate(row.day)}</td><td colSpan={4}>No reporting result</td></tr>])}</tbody></table></div>
    </section>)}</section>;
}
