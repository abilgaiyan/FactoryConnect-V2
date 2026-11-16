import { useEffect, useMemo, useState, type FormEvent } from "react";
import type { DashboardApplicationRuntime } from "../application/application-runtime.ts";
import { isProductionDayIdentity } from "../application/production-day-shift-reporting.ts";
import { useShiftPerformanceOverview } from "../application/use-shift-performance-overview.ts";
import { activityStates, formatActivityDuration, loadActivityReport, type ActivityReport } from "../application/machine-activity-report.ts";
import { ShiftPerformancePageStateView } from "./ShiftPerformancePageStateView.tsx";
import { ShiftPerformanceOverviewView } from "./ShiftPerformanceOverviewView.tsx";
import type { ShiftPerformanceMachine, ShiftPerformanceOverview, ShiftPerformanceShift } from "./shift-performance-model.ts";
import LiveStopReasonPicker from "./LiveStopReasonPicker.tsx";
import LiveMachineShiftTargets from "./LiveMachineShiftTargets.tsx";
import { MachineActivityEvidence } from "./MachineActivityEvidence.tsx";

export function occurrenceKey(machine: ShiftPerformanceMachine, occurrence: ShiftPerformanceShift): string {
  return JSON.stringify([machine.machineId, machine.processorId, machine.siteId, machine.productionLineId,
    occurrence.shift.shiftScheduleAssignmentId, occurrence.shift.shiftId, occurrence.shift.startsAtUtc, occurrence.shift.endsAtUtc]);
}

export function SupervisorReadOnly({ runtime }: { readonly runtime: DashboardApplicationRuntime }) {
  const [sourceIndex, setSourceIndex] = useState("");
  const [day, setDay] = useState("");
  const [error, setError] = useState("");
  const [request, setRequest] = useState<{ sourceIndex: number; day: string; generation: number } | null>(null);
  const [generation, setGeneration] = useState(0);
  function submit(event: FormEvent) {
    event.preventDefault(); setRequest(null);
    if (sourceIndex === "" || !runtime.configuration.sources[Number(sourceIndex)] || !isProductionDayIdentity(day)) {
      setError("Select one configured machine / processor and a valid production business date."); return;
    }
    setError(""); setGeneration(value => value + 1);
    setRequest({ sourceIndex: Number(sourceIndex), day, generation: generation + 1 });
  }
  if (!runtime.configuration.sources.length) return <section aria-label="Machine reporting availability">
    <h2>Machine reporting · not configured</h2>
    <p>Registered machines are available under Production targets, but this API host has not published any operational-metrics reporting sources. Shift occurrence and OEE reporting cannot be loaded until a real reporting processor is connected.</p>
    <p>No sample machines or calculated metrics have been substituted.</p>
  </section>;
  return <>
    <form onSubmit={submit} className="supervisor-controls">
      <label>Machine / reporting processor<select value={sourceIndex} onChange={event => { setSourceIndex(event.target.value); setRequest(null); setError(""); }}>
        <option value="">Select a configured source</option>
        {runtime.configuration.sources.map((source, index) => <option key={index} value={index}>{source.displayName} · {source.siteId} · {source.productionLineId} · {source.processorId}</option>)}
      </select></label>
      <label>Production business date<input type="date" value={day} onChange={event => { setDay(event.target.value); setRequest(null); setError(""); }} /></label>
      <button type="submit">Load shift occurrences</button>
    </form>
    {error && <p role="alert">{error}</p>}
    {request && <ShiftRead key={`${request.sourceIndex}:${request.day}:${request.generation}`} runtime={runtime} sourceIndex={request.sourceIndex} day={request.day} />}
  </>;
}

function ShiftRead({ runtime, sourceIndex, day }: { readonly runtime: DashboardApplicationRuntime; readonly sourceIndex: number; readonly day: string }) {
  const scopedRuntime = useMemo(() => ({ ...runtime, configuration: { ...runtime.configuration, sources: [runtime.configuration.sources[sourceIndex]!] } }), [runtime, sourceIndex]);
  const { state } = useShiftPerformanceOverview(day, scopedRuntime);
  return state.kind === "success" && !state.isRefreshing
    ? <OccurrenceRead key={day} overview={state.overview} runtime={scopedRuntime} />
    : <ShiftPerformancePageStateView state={state} />;
}

function OccurrenceRead({ overview, runtime }: { readonly overview: ShiftPerformanceOverview; readonly runtime: DashboardApplicationRuntime }) {
  const [selectedKey, setSelectedKey] = useState("");
  const occurrences = overview.groups.flatMap(group => group.machines.flatMap(machine => machine.shifts.map(occurrence => ({ machine, occurrence }))));
  const selected = occurrences.find(({ machine, occurrence }) => occurrenceKey(machine, occurrence) === selectedKey);
  return <>
    <label>Exact shift occurrence<select value={selectedKey} onChange={event => setSelectedKey(event.target.value)}>
      <option value="">Select an exact occurrence</option>
      {occurrences.map(({ machine, occurrence }) => <option key={occurrenceKey(machine, occurrence)} value={occurrenceKey(machine, occurrence)}>
        {occurrence.shift.shiftId} · {occurrence.shift.shiftScheduleAssignmentId} · {occurrence.shift.startsAtUtc} – {occurrence.shift.endsAtUtc}
      </option>)}
    </select></label>
    {!occurrences.length && <p>No authoritative shift occurrences returned. This does not establish zero production.</p>}
    {selected && <>
      <p>Production business date {overview.productionDay} · Site {selected.machine.siteId} · Line {selected.machine.productionLineId}</p>
      <p>Machine {selected.machine.machineId} · Reporting processor {selected.machine.processorId}<br />Assignment {selected.occurrence.shift.shiftScheduleAssignmentId} · Shift {selected.occurrence.shift.shiftId}<br />Exact UTC {selected.occurrence.shift.startsAtUtc} – {selected.occurrence.shift.endsAtUtc}</p>
      <p>Metric evidence read time: not returned by this presentation contract. Reporting source revision: <code>{selected.occurrence.sourceRevision === null ? "Not returned" : JSON.stringify(selected.occurrence.sourceRevision)}</code></p>
      <LiveMachineShiftTargets key={`targets:${selectedKey}`} machineId={selected.machine.machineId} businessDate={overview.productionDay} />
      <ShiftPerformanceOverviewView key={`metrics:${selectedKey}`} showFilters={false} overview={{ productionDay: overview.productionDay, groups: [{ groupName: null, machines: [{ ...selected.machine, shifts: [selected.occurrence] }] }] }} />
      <p>Metrics and activity use the same machine and UTC occurrence scope, but are independent reads and revision domains. No common atomic snapshot or metric/activity convergence is claimed.</p>
      <LiveStopReasonPicker key={`reasons:${selectedKey}`} machineId={selected.machine.machineId} />
      <ExactActivity key={`activity:${selectedKey}`} machine={selected.machine} occurrence={selected.occurrence} timeoutMilliseconds={runtime.configuration.requestTimeoutMilliseconds} />
    </>}
  </>;
}

function ExactStateChart({ report }: { readonly report: ActivityReport }) {
  const duration = report.totals.reduce((sum, entry) => sum + entry.durationTicks, report.unrepresentedTicks);
  if (duration <= 0) return <p>No measurable duration returned for this occurrence.</p>;
  const colors: Record<string, string> = {
    running: "#16855c", stopped: "#d97706", idle: "#7c8caa",
    fault: "#c24140", offline: "#475569", unknown: "#a3a3a3", unrepresented: "#e5e7eb",
  };
  const portions = [...activityStates.map(state => ({
    state, ticks: report.totals.find(t => t.state === state)?.durationTicks ?? 0,
  })), { state: "unrepresented", ticks: report.unrepresentedTicks }];
  return <section aria-label="Measured machine state distribution">
    <h3>Recorded machine state · exact occurrence</h3>
    <p>This chart uses validated historical activity durations. Unrepresented coverage is not treated as running, stopped or recorded Unknown. Planned break exclusions and OEE are not applied.</p>
    <div role="img" aria-label={portions.map(p => p.state + " " + (p.ticks / 10000000).toFixed(1) + " seconds").join(", ")}
      style={{ display: "flex", height: "26px", width: "100%", overflow: "hidden", borderRadius: "4px" }}>
      {portions.filter(p => p.ticks > 0).map(p =>
        <div key={p.state} title={p.state + ": " + formatActivityDuration(p.ticks)}
          style={{ width: (p.ticks / duration * 100) + "%", backgroundColor: colors[p.state] }} />)}
    </div>
    <ul>{portions.map(p => <li key={p.state}>{p.state}: {formatActivityDuration(p.ticks)} ({(p.ticks / duration * 100).toFixed(1)}%)</li>)}</ul>
  </section>;
}

function ExactActivity({ machine, occurrence, timeoutMilliseconds }: { readonly machine: ShiftPerformanceMachine; readonly occurrence: ShiftPerformanceShift; readonly timeoutMilliseconds: number }) {
  const [result, setResult] = useState<ActivityReport | null>(null);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let current = true;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMilliseconds);
    setResult(null); setError("");
    void loadActivityReport(machine.machineId, { fromInclusive: occurrence.shift.startsAtUtc, toExclusive: occurrence.shift.endsAtUtc }, controller.signal).then(report => {
      if (current && !controller.signal.aborted) setResult(report);
    }, () => {
      if (current) setError(controller.signal.aborted ? "Activity request timed out; no activity evidence established." : "Activity read unavailable or invalid; no activity evidence established.");
    }).finally(() => clearTimeout(timeout));
    return () => { current = false; controller.abort(); clearTimeout(timeout); };
  }, [machine.machineId, occurrence.shift.startsAtUtc, occurrence.shift.endsAtUtc, timeoutMilliseconds, attempt]);
  return <section className="activity-report" aria-label="Exact shift activity">
    <h2>Recorded activity · read only</h2>
    <p>Closed stored periods only. Displayed bounds may be clipped to this occurrence; original source bounds remain visible. Open periods, missing coverage and recorded Unknown are different conditions.</p>
    <p>Durable explanation actions are unavailable. The API does not return the complete publication revision/output ordinal reference. Positions are inspection provenance, not a replacement exact-event key.</p>
    {error ? <><p role="alert">{error} No sample fallback.</p><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry activity</button></>
      : result ? <><p>Unrepresented coverage: {formatActivityDuration(result.unrepresentedTicks)}. Not proof of downtime or acquisition failure.</p><ExactStateChart report={result} /><MachineActivityEvidence report={result} /></>
      : <p role="status">Loading exact shift activity…</p>}
  </section>;
}
