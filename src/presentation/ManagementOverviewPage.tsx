import { useMemo } from 'react';
import type { DashboardApplicationRuntime } from '../application/application-runtime.ts';
import { overviewCounts, overviewSearch, overviewSelection, selectedMachines, usableState, type OverviewSelection } from '../application/management-overview-model.ts';
import { useManagementOverview } from '../application/use-management-overview.ts';
import { profileFor } from '../application/machine-capabilities.ts';
import { formatRatioAsPercentage } from '../application/production-day-metric-formatting.ts';
import { formatReportTimestamp } from './date-formatting.ts';
import type { RangeItem } from '../application/date-range-reporting.ts';
import './management-overview.css';
export function ManagementOverviewPage({ runtime, search, navigate }: { runtime: DashboardApplicationRuntime; search: string; navigate: (href: string) => void }) {
    let selection: OverviewSelection;
    try { selection = overviewSelection(search, runtime.configuration.sources); }
    catch (error) { return <section><h1>Management Overview</h1><p role="alert">{error instanceof Error ? error.message : 'Invalid selection'}</p><a href="/overview">Reset overview selection</a></section>; }
    return <Overview key={search} runtime={runtime} selection={selection} navigate={navigate} />;
}
function Overview({ runtime, selection, navigate }: { runtime: DashboardApplicationRuntime; selection: OverviewSelection; navigate: (href: string) => void }) {
    const machines = useMemo(() => selectedMachines(selection, runtime.configuration.sources), [selection, runtime.configuration.sources]);
    const { attempts, reports, refresh } = useManagementOverview(runtime, machines, selection.day);
    const counts = overviewCounts(machines, attempts);
    const context = overviewSearch(selection);
    const change = (next: Partial<OverviewSelection>) => navigate(`/overview${overviewSearch({ ...selection, ...next })}`);
    return <section className="management-overview" aria-labelledby="overview-title">
        <p className="overview-eyebrow">FactoryConnect · Management BI</p><h1 id="overview-title">Management Overview</h1>
        <p>Live evidence and selected production-day results. Independent machine reads do not represent a simultaneous factory snapshot.</p>
        <div className="overview-controls">
            <label>Site <select value={selection.site} onChange={e => change({ site: e.target.value, line: '', machine: '' })}><option value="">All sites</option>{[...new Set(runtime.configuration.sources.map(s => s.siteId))].map(site => <option key={site}>{site}</option>)}</select></label>
            <label>Line <select value={selection.line} onChange={e => change({ line: e.target.value, machine: '' })}><option value="">All lines</option>{[...new Set(runtime.configuration.sources.filter(s => !selection.site || s.siteId === selection.site).map(s => s.productionLineId))].map(line => <option key={line}>{line}</option>)}</select></label>
            <label>Machine <select value={selection.machine} onChange={e => change({ machine: e.target.value })}><option value="">All machines</option>{selectedMachines({ ...selection, machine: '' }, runtime.configuration.sources).map(m => <option key={m.machineId} value={m.machineId}>{m.sources[0]!.displayName}</option>)}</select></label>
            <label>Production day <input type="date" value={selection.day} onChange={e => change({ day: e.target.value })} /></label>
            <button onClick={refresh}>Refresh overview</button>
        </div>
        <p><strong>{machines.length} selected machines</strong> / {new Set(runtime.configuration.sources.map(s => s.machineId.toLowerCase())).size} registered machines. Each count group independently reconciles to the selection.</p>
        <div className="overview-counts">{Object.entries(counts).map(([group, buckets]) => <section key={group} aria-label={group}><h2>{group === 'operational' ? 'Usable current states' : group === 'request' ? 'Live requests' : 'Evidence freshness'}</h2><dl>{Object.entries(buckets).map(([label, count]) => <div key={label}><dt>{label}</dt><dd>{count}</dd></div>)}</dl></section>)}</div>
        <p>Fleet Availability/OEE and coverage percentages withheld pending qualified aggregation and coverage contracts.</p>
        {!selection.day && <p>Select a production day to load per-machine Availability and Utilization. No ongoing-day calendar is assumed.</p>}
        <div className="overview-machines">{machines.map(machine => {
            const source = machine.sources[0]!;
            const attempt = attempts[machine.machineId];
            const evidence = attempt?.request === 'success' ? attempt.current?.evidence : undefined;
            const state = usableState(attempt, machine.conflict);
            const profile = profileFor(runtime.configuration.machineProfiles, machine.machineId);
            const report = reports[machine.machineId];
            const returnContext = new URLSearchParams({ overview: context });
            const reportQuery = new URLSearchParams({ from: selection.day, through: selection.day, machine: machine.machineId, overview: context });
            return <article key={machine.machineId} aria-label={source.displayName}>
                <h2>{source.displayName}</h2><p>{source.siteId} / {source.productionLineId}</p>
                {machine.conflict && <p role="alert">Configuration conflict</p>}
                <p className="overview-state">{state === 'stopped' && profile?.stoppedLabel ? profile.stoppedLabel : state === 'fault' && profile?.faultLabel ? profile.faultLabel : state ?? 'No usable current state'}</p>
                <p>Request: {attempt?.request ?? 'loading'}</p>{attempt?.error && <p role="alert">{attempt.error}</p>}
                {evidence && <p>Reported state: {evidence.machineState}; freshness: {evidence.freshness}; usability: {evidence.usability}. Read as of <time dateTime={evidence.readAsOf}>{formatReportTimestamp(evidence.readAsOf)}</time>.</p>}
                {attempt?.retained && <p>Previous evidence retained; unavailable for this attempt and excluded from current state counts.</p>}
                <details><summary>Machine capabilities</summary>{profile ? <><p>{profile.profileId} / {profile.profileVersion} · {profile.integrationCategoryId}</p><p>Approval: {profile.approvalReference}; effective {profile.effectiveFrom}</p><ul>{profile.capabilities.map(c => <li key={c.key}>{c.key}: {c.support}; {c.qualification}</li>)}</ul></> : <p>Not assessed — no commissioned profile supplied. Optional capabilities are not assumed.</p>}<p>Current metric availability is determined by reporting evidence.</p></details>
                {selection.day && <section aria-label="Selected-day metrics"><h3>Production day {selection.day}</h3>{(!report || report.kind === 'loading') && <p role="status">Loading selected-day results…</p>}{report?.kind === 'failure' && <p role="alert">Reporting request failed: {report.error}</p>}{report?.kind === 'success' && report.rows?.map(row => <div key={row.source.processorId}><p>Processor: {row.source.processorId}</p>{(['availability', 'utilization.elr'] as const).map(metric => <MetricStatusValue key={metric} label={metric === 'availability' ? 'Availability' : 'Utilization'} items={row.items.filter(i => i.metricKey === metric)} />)}</div>)}</section>}
                <nav aria-label={`${source.displayName} drill-downs`}><a href={`/machines/${machine.machineId}?${returnContext}`}>Machine details</a>{selection.day && <> · <a href={`/reports/range?${reportQuery}`}>Supporting historical report</a></>}</nav>
            </article>;
        })}</div>
    </section>;
}
export function MetricStatusValue({ label, items }: { label: string; items: readonly RangeItem[] }) {
    return <div><h4>{label}</h4>{items.length === 0 ? <p>No reporting result</p> : items.map(item => <p key={item.definitionVersion}>{item.status}: {item.status === 'calculated' && item.value !== null ? item.unit === 'ratio' ? `${formatRatioAsPercentage(item.value)}%` : `${item.value} ${item.unit}` : '—'}{item.reasonCode && <> · {item.reasonCode}</>}{item.reasonOperandName && <> · operand {item.reasonOperandName}</>}<small> Definition {item.definitionVersion}; revision {String(item.sourceRevision.position)}; API value {item.value ?? '—'} {item.unit}</small></p>)}</div>;
}
