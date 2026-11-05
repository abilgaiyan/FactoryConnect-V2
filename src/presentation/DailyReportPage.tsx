import { formatReportDate, formatReportTimestamp } from "./date-formatting.ts";
import type { DailyReportLifecycleState } from "../application/daily-report-lifecycle.ts";
import type {
  DailyReportCell,
  DailyReportMachine,
  DailyReportModel,
  DailyReportShift,
} from "./daily-report-model.ts";
import {
  canPrintDailyReportState,
  visibleDailyReportModel,
} from "./daily-report-page-policy.ts";

const metricLabels = {
  availability: "Availability",
  "utilization.elr": "Utilization",
  performance: "Performance",
  quality: "Quality",
  oee: "OEE",
} as const;

export interface DailyReportPageProps {
  readonly productionDay: string;
  readonly state: DailyReportLifecycleState;
  readonly refresh: () => Promise<void>;
  readonly print?: () => void;
}

export function DailyReportPage({
  productionDay,
  state,
  refresh,
  print = () => window.print(),
}: DailyReportPageProps) {
  const visibleModel = visibleDailyReportModel(state);

  return (
    <div className="daily-report-page">
      <div className="daily-report-controls" aria-label="Daily report actions">
        <button
          type="button"
          disabled={state.kind === "loading" || state.kind === "refreshing"}
          onClick={() => { void refresh(); }}
        >
          Refresh
        </button>
        <button
          type="button"
          disabled={!canPrintDailyReportState(state)}
          onClick={() => print()}
        >
          Print
        </button>
      </div>

      <DailyReportLifecycleMessage state={state} />

      {visibleModel === null
        ? null
        : <DailyReportDocument model={visibleModel} productionDay={productionDay} />}
    </div>
  );
}

function DailyReportLifecycleMessage({ state }: { readonly state: DailyReportLifecycleState }) {
  switch (state.kind) {
    case "idle":
      return null;
    case "loading":
      return <p role="status">Loading Daily Report…</p>;
    case "refreshing":
      return <p role="status">Refreshing Daily Report. The displayed report is from the previous successful retrieval and cannot be printed.</p>;
    case "success":
      return null;
    case "invalid-selection":
      return <p role="alert">The selected production day is not a valid report date.</p>;
    case "roster-prerequisite-failure":
      return <p role="alert">Shift roster coverage is required before this Daily Report can be generated.</p>;
    case "reporting-failure":
      return <p role="alert">Reporting data could not be loaded. Any displayed report is stale and cannot be printed.</p>;
    case "presentation-contract-failure":
      return <p role="alert">Reporting data did not satisfy the Daily Report presentation contract. Any displayed report is stale and cannot be printed.</p>;
    case "invalid-request":
      return <p role="alert">The Daily Report request was rejected. Any displayed report is stale and cannot be printed.</p>;
  }
}

function DailyReportDocument({
  model,
  productionDay,
}: {
  readonly model: DailyReportModel;
  readonly productionDay: string;
}) {
  return (
    <article className="daily-report-document" aria-labelledby="daily-report-document-title">
      <header className="daily-report-document-header">
        <p>FactoryConnect</p>
        <h2 id="daily-report-document-title">Daily Report</h2>
        <p>Production day: <time dateTime={productionDay}>{formatReportDate(productionDay)}</time></p>
      </header>

      {model.groups.length === 0
        ? <p>No configured reporting sources.</p>
        : model.groups.map((group, groupIndex) => (
            <section
              className="daily-report-group"
              key={`${group.groupName ?? "ungrouped"}-${groupIndex}`}
              aria-label={group.groupName ?? "Ungrouped machines"}
            >
              {model.groups.length > 1 && <h3>{group.groupName ?? "Ungrouped"}</h3>}
              {group.machines.map(machine => (
                <DailyReportMachineSection key={`${machine.processorId}:${machine.machineId}`} machine={machine} />
              ))}
            </section>
          ))}
    </article>
  );
}

function DailyReportMachineSection({ machine }: { readonly machine: DailyReportMachine }) {
  return (
    <section className="daily-report-machine" aria-labelledby={`daily-report-machine-${machine.processorId}-${machine.machineId}`}>
      <h4 id={`daily-report-machine-${machine.processorId}-${machine.machineId}`}>{machine.displayName}</h4>
      <p>Production line: {machine.productionLineId}</p>

      <h5>Production day</h5>
      <MetricTable cells={machine.productionDayCells} caption={`${machine.displayName} production-day metrics`} />

      <h5>Shifts</h5>
      {machine.shifts.length === 0
        ? <p>No roster-owned shift occurrences.</p>
        : machine.shifts.map((shift, index) => (
            <DailyReportShiftSection
              key={`${shift.shift.shiftScheduleAssignmentId}:${shift.shift.shiftId}:${shift.shift.startsAtUtc}:${index}`}
              shift={shift}
            />
          ))}
    </section>
  );
}

function DailyReportShiftSection({ shift }: { readonly shift: DailyReportShift }) {
  return (
    <section className="daily-report-shift">
      <h6>{shift.shift.shiftId}</h6>
      <p>
        <time dateTime={shift.shift.startsAtUtc}>{formatReportTimestamp(shift.shift.startsAtUtc)}</time>
        {" – "}
        <time dateTime={shift.shift.endsAtUtc}>{formatReportTimestamp(shift.shift.endsAtUtc)}</time>
      </p>
      <MetricTable cells={shift.cells} caption={`${shift.shift.shiftId} metrics`} />
    </section>
  );
}

function MetricTable({ cells, caption }: { readonly cells: readonly DailyReportCell[]; readonly caption: string }) {
  return (
    <table className="daily-report-metrics">
      <caption>{caption}</caption>
      <thead>
        <tr>
          {cells.map(cell => <th scope="col" key={cell.metricKey}>{metricLabels[cell.metricKey]}</th>)}
        </tr>
      </thead>
      <tbody>
        <tr>
          {cells.map(cell => <td key={cell.metricKey}><DailyReportCellValue cell={cell} /></td>)}
        </tr>
      </tbody>
    </table>
  );
}

function DailyReportCellValue({ cell }: { readonly cell: DailyReportCell }) {
  switch (cell.state) {
    case "calculated":
      return <><strong>Calculated</strong><br />{String(cell.value)} {cell.unit}</>;
    case "unavailable":
      return <><strong>Unavailable</strong>{renderReason(cell.reasonCode, cell.reasonOperandName)}</>;
    case "insufficient-evidence":
      return <><strong>Insufficient evidence</strong>{renderReason(cell.reasonCode, cell.reasonOperandName)}</>;
    case "missing":
      return <strong>Missing</strong>;
  }
}

function renderReason(reasonCode: string | null, reasonOperandName: string | null) {
  if (reasonCode === null && reasonOperandName === null) {
    return null;
  }

  const reason = [reasonCode, reasonOperandName].filter(value => value !== null).join(" · ");
  return <><br /><span>Reason: {reason}</span></>;
}
