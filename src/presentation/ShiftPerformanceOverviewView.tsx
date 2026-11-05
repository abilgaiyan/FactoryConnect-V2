import { useState } from "react";
import { formatReportDate, formatReportTimestamp } from "./date-formatting.ts";
import type {
  PresentedMetric,
  ShiftPerformanceGroup,
  ShiftPerformanceMachine,
  ShiftPerformanceOverview,
  ShiftPerformanceShift,
} from "./shift-performance-model.ts";
import { formatPresentedMetric } from "./shift-performance-view-formatting.ts";

export interface ShiftPerformanceOverviewViewProps {
  readonly overview: ShiftPerformanceOverview;
  readonly showFilters?: boolean;
}

export function ShiftPerformanceOverviewView({ overview, showFilters = true }: ShiftPerformanceOverviewViewProps) {
  const [machineFilter, setMachineFilter] = useState("");
  const [shiftFilter, setShiftFilter] = useState("");
  const machines = overview.groups.flatMap(group => group.machines);
  const shifts = [...new Set(machines.flatMap(machine => machine.shifts.map(shift => shift.shift.shiftId)))];
  const visibleGroups = overview.groups.map(group => ({
    ...group,
    machines: group.machines.filter(machine => !machineFilter || machineKey(machine) === machineFilter)
      .map(machine => ({ ...machine, shifts: machine.shifts.filter(shift => !shiftFilter || shift.shift.shiftId === shiftFilter) })),
  })).filter(group => group.machines.length > 0);
  const selected = visibleGroups.flatMap(group => group.machines.flatMap(machine => machine.shifts.map(shift => ({ machine, shift }))));
  return (
    <section className="equipment-performance" aria-labelledby="shift-performance-title">
      <h2 id="shift-performance-title">Shift performance</h2>
      <p className="report-eyebrow">Equipment performance · Production day: {formatReportDate(overview.productionDay)}</p>
      {showFilters && <div className="report-filters" role="group" aria-label="Equipment performance filters">
        <label>Machine <select value={machineFilter} onChange={event => setMachineFilter(event.target.value)}>
          <option value="">All machines</option>
          {machines.map(machine => <option key={machineKey(machine)} value={machineKey(machine)}>{machine.displayName} · {machine.productionLineId}</option>)}
        </select></label>
        <label>Shift identifier <select aria-describedby="shift-filter-scope" value={shiftFilter} onChange={event => setShiftFilter(event.target.value)}>
          <option value="">All shifts</option>
          {shifts.map(shift => <option key={shift} value={shift}>{shift}</option>)}
        </select></label>
        <button type="button" onClick={() => { setMachineFilter(""); setShiftFilter(""); }}>Reset filters</button>
      </div>}
      {showFilters && <p id="shift-filter-scope" className="report-scope-note">The shift filter includes every occurrence with that identifier. KPI cards appear only when exactly one machine and shift occurrence remains.</p>}
      <p className="report-scope-note">Each result belongs to one machine and exact shift interval. Group totals are not calculated.</p>
      {selected.length === 1 && <p className="report-scope-note">Selected: {selected[0]!.machine.displayName} · {selected[0]!.shift.shift.shiftId} · {formatReportTimestamp(selected[0]!.shift.shift.startsAtUtc)} – {formatReportTimestamp(selected[0]!.shift.shift.endsAtUtc)}</p>}
      {selected.length === 1 && <section className="performance-kpis" aria-label="Selected shift metrics">
        {[selected[0]!.shift.availability, selected[0]!.shift.utilization, selected[0]!.shift.performance, selected[0]!.shift.quality, selected[0]!.shift.oee].map(metric => {
          const text = performanceText(metric, selected[0]!.shift);
          return <article key={metric.metricKey} className="performance-kpi" data-state={text.conflict ? "conflict" : metric.state}>
            <h3>{metricLabel(metric.metricKey)}</h3><p>{text.primary}</p>{text.evidence && <small>{text.evidence}</small>}
          </article>;
        })}
      </section>}
      {machines.length > 0 && selected.length === 0 && (machineFilter || shiftFilter) && <p role="status">No shift occurrences match these filters.</p>}
      {overview.groups.length === 0
        ? <p>No configured machines.</p>
        : visibleGroups.map((group, index) => (
          <ShiftPerformanceGroupView key={`${group.groupName ?? "ungrouped"}-${index}`} group={group} showHeading={overview.groups.length > 1} />
        ))}
    </section>
  );
}

function ShiftPerformanceGroupView({ group, showHeading }: { readonly group: ShiftPerformanceGroup; readonly showHeading: boolean }) {
  return (
    <section aria-label={group.groupName ?? "Ungrouped machines"}>
      {showHeading && <h3>{group.groupName ?? "Ungrouped"}</h3>}
      {group.machines.map(machine => (
        <ShiftPerformanceMachineView
          key={machineKey(machine)}
          machine={machine}
        />
      ))}
    </section>
  );
}

function ShiftPerformanceMachineView({ machine }: { readonly machine: ShiftPerformanceMachine }) {
  return (
    <section aria-label={machine.displayName}>
      <h4>{machine.displayName}</h4>
      <p className="report-scope-note">{machine.siteId} · {machine.productionLineId}</p>
      <details className="performance-evidence"><summary>{machine.displayName} evidence and scope</summary>
        <dl><dt>Machine</dt><dd>{machine.machineId}</dd><dt>Processor</dt><dd>{machine.processorId}</dd></dl>
        {machine.shifts.map(shift => <div key={JSON.stringify(shift.shift)}>
          <strong>{shift.shift.shiftId}</strong>
          <dl><dt>Interval (UTC)</dt><dd>{shift.shift.startsAtUtc} – {shift.shift.endsAtUtc}</dd>
          <dt>Source position</dt><dd>{shift.sourceRevision?.position ?? "Not returned"}</dd>
          <dt>Source stream</dt><dd>{shift.sourceRevision?.streamKey ?? "Not returned"}</dd>
          <dt>Shift assignment</dt><dd>{shift.shift.shiftScheduleAssignmentId}</dd></dl>
        </div>)}
      </details>
      {machine.shifts.length === 0
        ? <p>No authoritative shift occurrences returned.</p>
        : (
          <div className="performance-table-scroll" tabIndex={0} role="region" aria-label={`${machine.displayName} shift results`}><table>
            <thead>
              <tr>
                <th scope="col">Shift</th>
                <th scope="col">Interval (IST)</th>
                <th scope="col">Availability</th>
                <th scope="col">Utilization</th>
                <th scope="col">Performance</th>
                <th scope="col">Quality</th>
                <th scope="col">OEE</th>
              </tr>
            </thead>
            <tbody>
              {machine.shifts.map(shift => (
                <ShiftOccurrenceView
                  key={`${shift.shift.shiftScheduleAssignmentId}\u0000${shift.shift.shiftId}\u0000${shift.shift.startsAtUtc}\u0000${shift.shift.endsAtUtc}`}
                  shift={shift}
                />
              ))}
            </tbody>
          </table></div>
        )}
    </section>
  );
}

function ShiftOccurrenceView({ shift }: { readonly shift: ShiftPerformanceShift }) {
  return (
    <tr>
      <th scope="row">{shift.shift.shiftId}</th>
      <td><time dateTime={shift.shift.startsAtUtc}>{formatReportTimestamp(shift.shift.startsAtUtc)}</time> – <time dateTime={shift.shift.endsAtUtc}>{formatReportTimestamp(shift.shift.endsAtUtc)}</time></td>
      <PresentedMetricValue shift={shift} metric={shift.availability} />
      <PresentedMetricValue shift={shift} metric={shift.utilization} />
      <PresentedMetricValue shift={shift} metric={shift.performance} />
      <PresentedMetricValue shift={shift} metric={shift.quality} />
      <PresentedMetricValue shift={shift} metric={shift.oee} />
    </tr>
  );
}

function PresentedMetricValue({ metric, shift }: { readonly metric: PresentedMetric; readonly shift: ShiftPerformanceShift }) {
  const text = performanceText(metric, shift);
  const missingLabel = metric.state === "missing" ? `${metricLabel(metric.metricKey)} missing` : undefined;

  return (
    <td aria-label={missingLabel} data-state={text.conflict ? "conflict" : metric.state}>
      {metric.state === "unavailable" || metric.state === "insufficient-evidence"
        ? <strong>{text.primary}</strong>
        : text.primary}
      {text.evidence === null ? null : <span> — {text.evidence}</span>}
    </td>
  );
}

function metricLabel(metricKey: PresentedMetric["metricKey"]): string {
  switch (metricKey) {
    case "availability":
      return "Availability";
    case "utilization.elr":
      return "Utilization";
    case "performance":
      return "Performance";
    case "quality":
      return "Quality";
    case "oee":
      return "OEE";
  }
}

function machineKey(machine: ShiftPerformanceMachine): string {
  return JSON.stringify([machine.machineId, machine.processorId]);
}

// Presentation guard only: retain the reported value as evidence, never clamp or recalculate.
function performanceText(metric: PresentedMetric, shift: ShiftPerformanceShift) {
  const availability = shift.availability;
  const invalidAvailability = availability.state === "calculated" && availability.unit.toLowerCase() === "ratio"
    && ratioAboveOne(String(availability.value));
  if (invalidAvailability && (metric.metricKey === "availability" || (metric.metricKey === "oee" && metric.state === "calculated"))) {
    return { primary: "Evidence conflict", evidence: metric.metricKey === "availability"
      ? `Reported ${formatPresentedMetric(metric).primary}; Availability exceeds 100%.`
      : `Reported OEE ${formatPresentedMetric(metric).primary}; Availability exceeds 100%.`, conflict: true };
  }
  return { ...formatPresentedMetric(metric), conflict: false };
}

function ratioAboveOne(value: string): boolean {
  const match = /^(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(value);
  if (!match) return false;
  const digits = `${match[1]}${match[2] ?? ""}`.replace(/^0+/, "");
  if (!digits) return false;
  const decimalIndex = BigInt(digits.length) + BigInt(match[3] ?? "0") - BigInt((match[2] ?? "").length);
  return decimalIndex > 1n || (decimalIndex === 1n && (digits[0]! > "1" || /[1-9]/.test(digits.slice(1))));
}
