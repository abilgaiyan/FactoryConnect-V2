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
  const measured = duration - report.unrepresentedTicks;
  const running = report.totals.find(t => t.state === "running")?.durationTicks ?? 0;
  const stopped = report.totals.find(t => t.state === "stopped")?.durationTicks ?? 0;
  return <section aria-label="Measured machine state distribution">
    <h3>Recorded machine state · exact occurrence</h3>
    <dl>
      <dt>Measured historical coverage</dt><dd>{(measured / duration * 100).toFixed(1)}% ({formatActivityDuration(measured)})</dd>
      <dt>Recorded running / occurrence</dt><dd>{(running / duration * 100).toFixed(1)}% ({formatActivityDuration(running)})</dd>
      <dt>Recorded stopped / occurrence</dt><dd>{(stopped / duration * 100).toFixed(1)}% ({formatActivityDuration(stopped)})</dd>
      <dt>Unrepresented / occurrence</dt><dd>{(report.unrepresentedTicks / duration * 100).toFixed(1)}% ({formatActivityDuration(report.unrepresentedTicks)})</dd>
    </dl>
    <p>These are observed proportions of the full occurrence, not OEE availability. Recorded unknown, idle, fault and offline remain separate; planned breaks are not excluded.</p>
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

function ExactActivityTimeline({ report }: { readonly report: ActivityReport }) {
  const start = Date.parse(report.fromInclusive);
  const end = Date.parse(report.toExclusive);
  const span = end - start;
  if (!(span > 0)) return null;
  const colors: Record<string, string> = {
    running: "#16855c", stopped: "#d97706", idle: "#7c8caa",
    fault: "#c24140", offline: "#475569", unknown: "#a3a3a3",
  };
  return <section aria-label="Historical machine activity timeline">
    <h3>Machine state timeline · exact shift</h3>
    <p>Position shows when each closed recorded interval occurred. Blank gaps mean unrepresented coverage, not downtime. This timeline does not exclude planned breaks.</p>
    <div role="img" aria-label={"Historical activity timeline from " + report.fromInclusive + " to " + report.toExclusive}
      style={{ position: "relative", width: "100%", height: "30px", background: "#e5e7eb", borderRadius: "4px" }}>
      {report.intervals.map(interval => {
        const left = (Date.parse(interval.startedAtUtc) - start) / span * 100;
        const width = (Date.parse(interval.endedAtUtc) - Date.parse(interval.startedAtUtc)) / span * 100;
        return <div key={interval.position} title={interval.state + ": " + interval.startedAtUtc + " – " + interval.endedAtUtc}
          style={{ position: "absolute", left: left + "%", width: width + "%", top: 0, bottom: 0, background: colors[interval.state] }} />;
      })}
    </div>
    <p>From <time dateTime={report.fromInclusive}>{report.fromInclusive}</time> to <time dateTime={report.toExclusive}>{report.toExclusive}</time></p>
  </section>;
}

type PlannedBreak = { breakId: string; startOffsetMinute: number; durationMinutes: number; excludeFromPlannedProduction: boolean };
function BreakAdjustedActivity({ report, shiftId }: { readonly report: ActivityReport; readonly shiftId: string }) {
  const [breaks, setBreaks] = useState<PlannedBreak[] | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    setBreaks(null); setError("");
    void fetch("/api/planning/v2/shifts/" + encodeURIComponent(shiftId) + "/breaks",
      { credentials: "same-origin", cache: "no-store", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error("Shift breaks unavailable (HTTP " + response.status + ").");
        const data: unknown = await response.json();
        if (!data || typeof data !== "object") throw new Error("Invalid shift breaks.");
        const value = data as { breaks?: unknown; isSample?: unknown };
        if (value.isSample !== false || !Array.isArray(value.breaks) || !value.breaks.every((b: unknown) => {
          if (!b || typeof b !== "object") return false;
          const v = b as PlannedBreak;
          return typeof v.breakId === "string" && Number.isInteger(v.startOffsetMinute) &&
            v.startOffsetMinute >= 0 && Number.isInteger(v.durationMinutes) && v.durationMinutes > 0 &&
            typeof v.excludeFromPlannedProduction === "boolean";
        })) throw new Error("Unverified shift breaks.");
        if (!controller.signal.aborted) setBreaks(value.breaks as PlannedBreak[]);
      }).catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Shift breaks unavailable."); });
    return () => controller.abort();
  }, [shiftId]);
  if (error) return <p role="alert">Break-adjusted activity unavailable: {error}. No zero-break assumption.</p>;
  if (!breaks) return <p role="status">Loading configured break intervals…</p>;
  const start = Date.parse(report.fromInclusive);
  const end = Date.parse(report.toExclusive);
  const excluded = breaks.filter(b => b.excludeFromPlannedProduction)
    .map(b => ({ start: start + b.startOffsetMinute * 60000, end: start + (b.startOffsetMinute + b.durationMinutes) * 60000 }))
    .sort((a, b) => a.start - b.start);
  if (excluded.some((b, i) => b.start < start || b.end > end || (i > 0 && b.start < excluded[i - 1]!.end)))
    return <p role="alert">Break intervals conflict with this exact occurrence. No adjusted totals calculated.</p>;
  const overlap = (a: number, b: number, c: number, d: number) => Math.max(0, Math.min(b, d) - Math.max(a, c));
  const excludedMs = excluded.reduce((sum, b) => sum + b.end - b.start, 0);
  const plannedMs = end - start - excludedMs;
  if (plannedMs <= 0) return <p role="alert">No positive planned production duration after breaks.</p>;
  const byState = activityStates.map(state => {
    const intervals = report.intervals.filter(i => i.state === state);
    const ms = intervals.reduce((sum, i) => {
      const a = Date.parse(i.startedAtUtc), b = Date.parse(i.endedAtUtc);
      return sum + b - a - excluded.reduce((cut, br) => cut + overlap(a, b, br.start, br.end), 0);
    }, 0);
    return { state, ms };
  });
  const representedMs = byState.reduce((sum, s) => sum + s.ms, 0);
  const missingMs = Math.max(0, plannedMs - representedMs);
  return <section aria-label="Break-adjusted recorded machine activity">
    <h3>Recorded activity outside planned breaks</h3>
    <p>Configured excluded breaks: {excludedMs / 60000} min. Planned production window: {plannedMs / 60000} min. State intervals are clipped against each excluded break.</p>
    <dl>{byState.map(s => <div key={s.state}><dt>{s.state}</dt><dd>{(s.ms / 60000).toFixed(2)} min ({(s.ms / plannedMs * 100).toFixed(1)}%)</dd></div>)}
      <div><dt>Unrepresented outside breaks</dt><dd>{(missingMs / 60000).toFixed(2)} min ({(missingMs / plannedMs * 100).toFixed(1)}%)</dd></div>
    </dl>
    <p>These are recorded durations after break exclusion, not certified OEE availability. Unknown and unrepresented coverage remain separate.</p>
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
      : result ? <><p>Unrepresented coverage: {formatActivityDuration(result.unrepresentedTicks)}. Not proof of downtime or acquisition failure.</p><ExactStateChart report={result} /><ExactActivityTimeline report={result} /><BreakAdjustedActivity report={result} shiftId={occurrence.shift.shiftId} /><MachineActivityEvidence report={result} /></>
      : <p role="status">Loading exact shift activity…</p>}
  </section>;
}
