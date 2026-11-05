import { formatReportTimestamp } from "./date-formatting.ts";
import { useEffect, useMemo, useState } from 'react';
import type { DashboardApplicationRuntime } from '../application/application-runtime.ts';
import { fleetMachines, fleetCounts, FleetRefresh, type FleetAttempt } from '../application/fleet-status.ts';
import type { CurrentMachineStateResponse } from '../api/current-state/current-state-client.ts';
export function FleetStatusPage({ runtime }: {
    runtime: DashboardApplicationRuntime;
}) {
    const machines = useMemo(() => fleetMachines(runtime.configuration.sources), [runtime.configuration.sources]);
    const [attempts, setAttempts] = useState<Record<string, FleetAttempt>>({});
    const controller = useMemo(() => new FleetRefresh(machines, runtime.currentStateClient, setAttempts), [machines, runtime.currentStateClient]);
    useEffect(() => { void controller.refresh(); return () => controller.dispose(); }, [controller]);
    return <section className="fleet-range"><h1>Fleet status</h1><p>{machines.length} configured machines. Each count group reconciles independently; groups overlap.</p>
    <button onClick={() => void controller.refresh()}>Refresh fleet</button>
    <div className="fleet-counts">{Object.entries(fleetCounts(machines, attempts)).map(([group, counts]) => <section key={group}><h2>{group}</h2><ul>{Object.entries(counts).map(([bucket, count]) => <li key={bucket}>{bucket}: {count}</li>)}</ul></section>)}</div>
    <div className="report-table-scroll" role="region" aria-label="Fleet machine rows" tabIndex={0}><table><caption>Reported state and current request outcome</caption><thead><tr><th>Machine</th><th>Configuration</th><th>Request</th><th>Evidence</th></tr></thead><tbody>
      {machines.map(machine => {
            const attempt = attempts[machine.machineId];
            return <tr key={machine.machineId}>
        <td><a href={`/machines/${machine.machineId}`}>{machine.machineId}</a></td>
        <td>{machine.conflict && <strong>Configuration conflict</strong>}<ul>{machine.sources.map(source => <li key={source.processorId}>{source.displayName} — {source.siteId} / {source.productionLineId}; group {source.groupName ?? '(none)'}; order {source.displayOrder}; processor {source.processorId}</li>)}</ul></td>
        <td>{attempt?.request ?? 'loading'}{attempt?.error && <p role="alert">{attempt.error}</p>}</td>
        <td>{attempt?.request === 'success' && attempt.current ? <Evidence value={attempt.current}/> : <p>State unavailable for this attempt.</p>}
        {attempt?.retained && <aside><strong>Retained evidence from a previous successful read</strong><Evidence value={attempt.retained}/></aside>}</td>
      </tr>;
        })}</tbody></table></div></section>;
}
function Evidence({ value }: {
    value: CurrentMachineStateResponse;
}) {
    return <dl><dt>Outcome</dt><dd>{value.outcome}</dd><dt>Coverage</dt><dd>{value.coverage}</dd>{value.evidence ? <>
    <dt>Reported state</dt><dd>{value.evidence.machineState}</dd><dt>Freshness</dt><dd>{value.evidence.freshness}</dd>
    <dt>Usability</dt><dd>{value.evidence.usability}</dd><dt>Read as of</dt><dd><time dateTime={value.evidence.readAsOf}>{formatReportTimestamp(value.evidence.readAsOf)}</time></dd></> : <><dt>Evidence</dt><dd>No evidence</dd></>}</dl>;
}
