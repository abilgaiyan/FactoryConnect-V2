import { useEffect, useMemo, useState } from "react";

interface Machine { sourceMachineId: string; deviceKey: string; sourceDatabase: string }
interface Metric { sourceRowId: number; productionBusinessDate: string | null; shiftId: string | null; shiftStartsAtUtc: string | null; metricKey: string; status: number; metricValue: string | null; unit: string | null; reasonCode: number | null }
interface Period { sourceDatabase: string; sourceMachineId: string; projectionRevision: number; outputOrdinal: number; machineState: number; startedAt: string; endedAt: string }
interface MachineReply { source: string; isSample: boolean; machines: Machine[] }
interface MetricReply { source: string; isSample: boolean; metrics: Metric[] }
interface ActivityReply { source: string; isSample: boolean; periods: Period[] }
interface ShiftAggregate { sourceRowId: number; shiftId: string | null; shiftStartsAtUtc: string | null; shiftEndsAtUtc: string | null; metricInputKey: string; aggregateValue: string | null; unit: string | null; inputCount: number | null }
interface ShiftReply { source: string; isSample: boolean; aggregates: ShiftAggregate[] }
type Daily = { date: string; metric: string; value: number; unit: string };
const expectedSource = "imported-v1-legacy";
const stateNames: Record<number, string> = { 0: "Unknown", 1: "Stopped", 2: "Idle", 3: "Running", 4: "Fault", 5: "Offline" };
const stateLabel = (code: number) => stateNames[code] ?? "Unrecognized";
const historicalTime = (instant: string | null): string => {
  if (!instant) return "Unspecified";
  const parsed = Date.parse(instant);
  if (!Number.isFinite(parsed)) return "Invalid source timestamp";
  return new Intl.DateTimeFormat("en-IN", {
    timeZone: "Asia/Kolkata", day: "2-digit", month: "short", year: "numeric",
    hour: "2-digit", minute: "2-digit", hour12: false,
  }).format(new Date(parsed)) + " IST";
};
const percentMetric = /^(availability|utilization)$/i;
const oeeMetric = /^(availability|utilization|performance|quality|oee)$/i;
const numeric = (value: string | null): number | null => {
  if (value === null || !/^-?(?:\d+(?:\.\d+)?|\.\d+)$/.test(value.trim())) return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
};
function formatMetric(metric: Metric): string {
  // Preserve source status rather than interpreting 0/1/2 as zero percentages.
  if (metric.status !== 0) return `Status ${metric.status}${metric.reasonCode === null ? "" : ` · reason ${metric.reasonCode}`}`;
  const n = numeric(metric.metricValue);
  return n === null ? "No numerical value" : `${n} ${metric.unit ?? ""}`.trim();
}
function dateKey(instant: string): string {
  // Historical factory calendar for Gajra, not UTC midnight.
  return new Date(new Date(instant).getTime() + 330 * 60_000).toISOString().slice(0, 10);
}
export function splitActivityByIndiaDay(periods: readonly Period[], from: string, through: string): { date: string; state: number; seconds: number }[] {
  const begin = Date.parse(from + "T00:00:00+05:30");
  const end = Date.parse(through + "T00:00:00+05:30") + 86_400_000;
  const totals = new Map<string, number>();
  for (const p of periods) {
    let cursor = Math.max(Date.parse(p.startedAt), begin);
    const stop = Math.min(Date.parse(p.endedAt), end);
    if (!Number.isFinite(cursor) || !Number.isFinite(stop)) continue;
    while (cursor < stop) {
      const day = dateKey(new Date(cursor).toISOString());
      const next = Date.parse(day + "T00:00:00+05:30") + 86_400_000;
      const cut = Math.min(stop, next);
      if (cut <= cursor) break;
      const key = day + "|" + p.machineState;
      totals.set(key, (totals.get(key) ?? 0) + (cut - cursor) / 1000);
      cursor = cut;
    }
  }
  return [...totals].map(([key, seconds]) => {
    const [date = "", state = "0"] = key.split("|");
    return { date, state: Number(state), seconds };
  }).sort((a, b) => a.date.localeCompare(b.date) || a.state - b.state);
}
function buildDaily(metrics: readonly Metric[]): Daily[] {
  return metrics.flatMap(metric => {
    if (!percentMetric.test(metric.metricKey) || metric.status !== 0 || !metric.productionBusinessDate) return [];
    const value = numeric(metric.metricValue);
    if (value === null) return [];
    return [{ date: metric.productionBusinessDate, metric: metric.metricKey, value, unit: metric.unit ?? "" }];
  });
}
function Trend({ rows, name }: { rows: readonly Daily[]; name: string }) {
  const selected = rows.filter(row => row.metric.toLowerCase() === name.toLowerCase()).sort((a,b) => a.date.localeCompare(b.date));
  if (!selected.length) return <p>No numerical {name} projection for this date range.</p>;
  const max = Math.max(...selected.map(row => Math.abs(row.value)), 1);
  return <section aria-label={name + " historical trend"}><h3>{name} · historical source values</h3>
    <div style={{ display: "flex", gap: 8, alignItems: "end", overflowX: "auto", minHeight: 130 }}>
      {selected.map((row, i) => <div key={row.date + ":" + i} title={row.date + ": " + row.value + " " + row.unit} style={{ minWidth: 44, textAlign: "center" }}>
        <small>{row.value}</small><div style={{ height: Math.max(3, Math.round(95 * Math.abs(row.value) / max)), background: "#287da8", borderRadius: 3 }} />
        <small>{row.date.slice(5)}</small>
      </div>)}
    </div>
    <p>Values are shown exactly as stored, with source units; not recalculated V2 OEE.</p>
  </section>;
}
function exportShiftMetricsCsv(metrics: readonly Metric[], machine: string): void {
  const escapeCell = (value: string | number | null | undefined): string => {
    const raw = String(value ?? "");
    // Prevent spreadsheet formula injection in source-controlled text values.
    const safe = /^\s*[=+@-]/.test(raw) ? "'" + raw : raw;
    return '"' + safe.replace(/"/g, '""') + '"';
  };
  const columns = ["Machine", "Shift start IST", "Shift ID", "Metric", "Source status", "Value", "Unit", "Reason code", "Source row ID"];
  const rows = metrics.filter(metric => metric.shiftStartsAtUtc !== null && oeeMetric.test(metric.metricKey))
    .map(metric => [machine, historicalTime(metric.shiftStartsAtUtc), metric.shiftId, metric.metricKey,
      metric.status, metric.status === 0 ? metric.metricValue : null, metric.unit, metric.reasonCode, metric.sourceRowId]);
  const csv = [columns, ...rows].map(row => row.map(escapeCell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob(["\uFEFF" + csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = "factoryconnect-history-shifts.csv";
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
function ShiftMetricOverview({ metrics, machine }: { metrics: readonly Metric[]; machine: string }) {
  const rows = metrics.filter(metric => metric.shiftStartsAtUtc !== null && oeeMetric.test(metric.metricKey))
    .sort((a, b) => (a.shiftStartsAtUtc ?? "").localeCompare(b.shiftStartsAtUtc ?? "") || a.metricKey.localeCompare(b.metricKey));
  if (!rows.length) return <p>No source shift-level metric projections in the selected range.</p>;
  const groups = new Map<string, Metric[]>();
  for (const row of rows) {
    const key = JSON.stringify([row.shiftStartsAtUtc, row.shiftId]);
    const group = groups.get(key);
    if (group) group.push(row);
    else groups.set(key, [row]);
  }
  const shifts = [...groups.values()];
  return <section aria-label="Imported V1 shift metric projections">
    <h3>Shift-level Availability and OEE evidence</h3>
    <button type="button" onClick={() => exportShiftMetricsCsv(rows, machine)}>Export shift metrics (CSV)</button>
    <p>{shifts.length} recorded shifts with metric projections. Only original source values are displayed; unavailable Performance, Quality or OEE are not treated as zero.</p>
    <div style={{overflowX:"auto"}}>
      <table><thead><tr><th>Shift start (IST)</th><th>Shift ID</th><th>Availability</th><th>Utilization</th><th>Performance</th><th>Quality</th><th>OEE</th></tr></thead><tbody>
        {shifts.map((group, index) => {
          const first = group[0]!;
          const value = (name: string) => {
            const row = group.find(metric => metric.metricKey.toLowerCase() === name.toLowerCase());
            return row ? formatMetric(row) : "Unavailable";
          };
          return <tr key={(first.shiftStartsAtUtc ?? "") + ":" + index}><td title={first.shiftStartsAtUtc ?? undefined}>{historicalTime(first.shiftStartsAtUtc)}</td><td>{first.shiftId ?? "—"}</td>
            <td>{value("Availability")}</td><td>{value("Utilization")}</td><td>{value("Performance")}</td><td>{value("Quality")}</td><td>{value("OEE")}</td>
          </tr>;
        })}
      </tbody></table>
    </div>
  </section>;
}
function ActivityDailyChart({ rows }: { rows: readonly { date: string; state: number; seconds: number }[] }) {
  const dates = [...new Set(rows.map(row => row.date))].sort();
  const states = [...new Set(rows.map(row => row.state))].sort((a, b) => a - b);
  if (!dates.length) return <p>No completed activity intervals in this range.</p>;
  // Raw interval durations can overlap in source revisions. Do not label the sums
  // as utilization, machine availability or qualified shift production time.
  const palette = ["#287da8", "#d97706", "#64748b", "#7c3aed", "#059669", "#be123c"];
  const max = Math.max(1, ...dates.map(date => rows.filter(row => row.date === date).reduce((sum, row) => sum + row.seconds, 0)));
  return <section aria-label="Imported V1 activity by India calendar day">
    <h3>Historical activity by day</h3>
    <p>Hours by verified V1 machine state, split at India midnight. Raw source intervals may overlap; this is not a qualified OEE or shift-duration calculation.</p>
    <div style={{display:"flex",gap:14,flexWrap:"wrap",marginBottom:12}}>
      {states.map((state,i) => <span key={state} style={{display:"inline-flex",alignItems:"center",gap:6}}><span aria-hidden="true" style={{display:"inline-block",width:12,height:12,background:palette[i % palette.length]}} />{stateLabel(state)} ({state})</span>)}
    </div>
    <div style={{display:"flex",gap:10,alignItems:"end",overflowX:"auto",minHeight:180,padding:"8px 0"}}>
      {dates.map(date => {
        const day = rows.filter(row => row.date === date);
        const total = day.reduce((sum,row) => sum + row.seconds, 0);
        return <div key={date} style={{flex:"1 0 55px",maxWidth:110,textAlign:"center"}} title={date + ": " + (total / 3600).toFixed(2) + " raw interval hours"}>
          <small>{(total / 3600).toFixed(1)}h</small>
          <div style={{height:125,display:"flex",flexDirection:"column-reverse",justifyContent:"start",background:"#f1f5f9",borderRadius:3,overflow:"hidden"}}>
            {states.map((state,i) => {
              const seconds = day.find(row => row.state === state)?.seconds ?? 0;
              return seconds > 0 ? <div key={state} title={date + " · " + stateLabel(state) + " (" + state + "): " + (seconds / 3600).toFixed(2) + "h"} style={{height:(seconds / max * 125) + "px",background:palette[i % palette.length]}} /> : null;
            })}
          </div>
          <small>{date.slice(5)}</small>
        </div>;
      })}
    </div>
  </section>;
}
function HistoricalScheduleReconciliation({ shifts }: { shifts: readonly ShiftAggregate[] }) {
  // Compare only source-recorded shift windows. This is an audit of clock
  // discrepancies, not a reassignment of historical production evidence.
  const expected = new Set(["07:30|16:00|510", "16:00|00:00|480", "00:00|07:30|450"]);
  const clock = (instant: string): string => new Intl.DateTimeFormat("en-GB", {
    timeZone: "Asia/Kolkata", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  }).format(new Date(instant));
  const windows = new Map<string, { shift: string; start: string; end: string; actual: string; matches: boolean }>();
  for (const item of shifts) {
    if (!item.shiftStartsAtUtc || !item.shiftEndsAtUtc) continue;
    const startMs = Date.parse(item.shiftStartsAtUtc);
    const endMs = Date.parse(item.shiftEndsAtUtc);
    if (!Number.isFinite(startMs) || !Number.isFinite(endMs) || endMs <= startMs) continue;
    const key = JSON.stringify([item.shiftId, startMs, endMs]);
    const startClock = clock(item.shiftStartsAtUtc);
    const endClock = clock(item.shiftEndsAtUtc);
    const minutes = (endMs - startMs) / 60000;
    windows.set(key, {
      shift: item.shiftId ?? "Unspecified", start: item.shiftStartsAtUtc,
      end: item.shiftEndsAtUtc, actual: startClock + "–" + endClock,
      matches: expected.has(startClock + "|" + endClock + "|" + minutes),
    });
  }
  const rows = [...windows.values()].sort((a,b) => a.start.localeCompare(b.start));
  if (!rows.length) return null;
  const differing = rows.filter(row => !row.matches);
  const exportDiscrepancies = () => {
    const safe = (value: string) => {
      const protectedValue = /^\s*[=+@-]/.test(value) ? "'" + value : value;
      return '"' + protectedValue.replace(/"/g, '""') + '"';
    };
    const header = ["Original start UTC", "Original end UTC", "Original start IST", "Source shift ID", "Recorded IST clock", "Reconciliation", "Break assessment", "OEE qualification"];
    const data = differing.map(row => [
      row.start, row.end, historicalTime(row.start), row.shift, row.actual,
      "Differs from approved V2 shift timing", "Not assessed", "Not established",
    ]);
    const csv = [header, ...data].map(row => row.map(safe).join(",")).join("\r\n");
    const url = URL.createObjectURL(new Blob(["\uFEFF" + csv], {type:"text/csv;charset=utf-8"}));
    const link = document.createElement("a");
    link.href = url;
    link.download = "factoryconnect-historical-shift-discrepancies.csv";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  };
  return <section aria-label="Historical V1 shift schedule reconciliation">
    <h3>Historical shift timing reconciliation</h3>
    <p>Approved V2 factory shifts (IST): 07:30–16:00, 16:00–00:00, 00:00–07:30. This compares recorded V1 shift windows with approved clock times; it does not change source evidence or recalculate OEE. Break exclusions are not assessed.</p>
    <p role="status">{rows.length} recorded shift windows checked · {differing.length} differ from approved timing.</p>
    {differing.length > 0 && <button type="button" onClick={exportDiscrepancies}>Export timing discrepancies (CSV)</button>}
    {differing.length > 0 && <details><summary>Review {differing.length} historical timing discrepancies</summary>
      <div style={{overflowX:"auto"}}><table><thead><tr><th>Original start (IST)</th><th>Source shift ID</th><th>Recorded clock (IST)</th><th>Review</th></tr></thead>
      <tbody>{differing.map(row => <tr key={row.start + row.shift}><td>{historicalTime(row.start)}</td><td>{row.shift}</td><td>{row.actual}</td><td>Differs from approved V2 shift timing; retain original V1 evidence</td></tr>)}</tbody></table></div>
    </details>}
  </section>;
}
function ShiftActivityBreakdown({ periods, shifts }: { periods: readonly Period[]; shifts: readonly ShiftAggregate[] }) {
  const [reviewOnly, setReviewOnly] = useState(false);
  // Shift boundaries are taken only from imported source facts, never from the
  // current site schedule. Clip each raw period to the recorded shift interval.
  const windows = new Map<string, { id: string; start: string; end: string }>();
  for (const shift of shifts) {
    if (!shift.shiftStartsAtUtc || !shift.shiftEndsAtUtc) continue;
    const id = shift.shiftId ?? "Unspecified";
    const key = JSON.stringify([id, shift.shiftStartsAtUtc, shift.shiftEndsAtUtc]);
    windows.set(key, { id, start: shift.shiftStartsAtUtc, end: shift.shiftEndsAtUtc });
  }
  const rows = [...windows.values()].sort((a,b) => a.start.localeCompare(b.start)).map(window => {
    const start = Date.parse(window.start);
    const end = Date.parse(window.end);
    const totals = new Map<number, number>();
    const intervals: { a: number; b: number }[] = [];
    if (Number.isFinite(start) && Number.isFinite(end) && end > start) {
      for (const period of periods) {
        const a = Math.max(start, Date.parse(period.startedAt));
        const b = Math.min(end, Date.parse(period.endedAt));
        if (Number.isFinite(a) && Number.isFinite(b) && b > a) {
          intervals.push({ a, b });
          totals.set(period.machineState, (totals.get(period.machineState) ?? 0) + (b - a) / 3600000);
        }
      }
    }
    // Calculate distinct time coverage separately from raw state-hour sums.
    // Overlapping intervals can conceal gaps if only totals are compared.
    intervals.sort((left, right) => left.a - right.a || left.b - right.b);
    let coveredMs = 0;
    let mergedEnd = Number.NEGATIVE_INFINITY;
    let hasOverlap = false;
    for (const interval of intervals) {
      if (interval.a < mergedEnd) hasOverlap = true;
      coveredMs += Math.max(0, interval.b - Math.max(interval.a, mergedEnd));
      mergedEnd = Math.max(mergedEnd, interval.b);
    }
    const duration = (end - start) / 3600000;
    const recorded = [...totals.values()].reduce((sum, hours) => sum + hours, 0);
    const covered = coveredMs / 3600000;
    return { ...window, totals, duration, recorded, covered, overlap: hasOverlap, incomplete: covered < duration - 0.01 };
  });
  if (!rows.length) return null;
  const flagged = rows.filter(row => row.overlap || row.incomplete);
  const visibleRows = reviewOnly ? flagged : rows;
  const codes = [...new Set(rows.flatMap(row => [...row.totals.keys()]))].sort((a,b) => a-b);
  const exportEvidence = () => {
    const headers = ["Shift start IST", "Source shift ID", "Window hours", "Distinct covered hours", "Raw interval hours", "Overlapping evidence", "Missing coverage", ...codes.map(code => stateLabel(code) + " (" + code + ") raw hours")];
    const csvCell = (value: string | number | boolean): string => {
      const raw = String(value);
      const safe = /^\\s*[=+@-]/.test(raw) ? "'" + raw : raw;
      return '"' + safe.replace(/"/g, '""') + '"';
    };
    const data = visibleRows.map(row => [
      historicalTime(row.start), row.id, row.duration.toFixed(2), row.covered.toFixed(2), row.recorded.toFixed(2),
      row.overlap ? "Yes" : "No", row.incomplete ? "Yes" : "No",
      ...codes.map(code => (row.totals.get(code) ?? 0).toFixed(2))
    ]);
    const csv = [headers, ...data].map(values => values.map(csvCell).join(",")).join("\r\n");
    const url = URL.createObjectURL(new Blob(["\uFEFF" + csv], {type: "text/csv;charset=utf-8"}));
    const link = document.createElement("a");
    link.href = url;
    link.download = reviewOnly ? "factoryconnect-shifts-needing-review.csv" : "factoryconnect-shift-activity-evidence.csv";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  };

  return <section aria-label="Source-window historical shift activity">
    <h3>Machine activity by recorded shift</h3>
    <p>Raw machine-state intervals clipped to original V1 shift start/end boundaries. Breaks are not excluded. Distinct covered hours are calculated from the union of intervals, so overlap cannot hide missing time. Raw state hours can still overcount; neither measure is qualified production or OEE duration.</p>
    <p role="status">{rows.length} recorded shift windows · {flagged.length} require evidence review ({rows.filter(row => row.overlap).length} with overlap, {rows.filter(row => row.incomplete).length} with missing coverage). These counts can overlap.</p>
    <label style={{display:"inline-flex",gap:6,alignItems:"center",marginBottom:12}}><input type="checkbox" checked={reviewOnly} onChange={event => setReviewOnly(event.target.checked)} />Show only shifts needing evidence review</label>
    {reviewOnly && visibleRows.length === 0 && <p>No shifts have overlap or missing-coverage warnings in the loaded data.</p>}
    <button type="button" disabled={visibleRows.length === 0} onClick={exportEvidence}>Export displayed shift evidence (CSV)</button>
    <div style={{display:"flex",gap:14,flexWrap:"wrap",marginBottom:10}} aria-label="Shift activity state legend">
      {codes.map((code,i) => <span key={code} style={{display:"inline-flex",alignItems:"center",gap:5}}><span aria-hidden="true" style={{display:"inline-block",width:12,height:12,background:["#287da8","#d97706","#64748b","#7c3aed","#059669","#be123c"][i % 6]}} />{stateLabel(code)} ({code})</span>)}
    </div>
    <div style={{display:"flex",gap:12,alignItems:"end",overflowX:"auto",padding:"8px 0 16px"}} aria-label="Historical shift state hours chart">
      {visibleRows.map((row,index) => <div key={row.start + "|" + row.id} style={{flex:"0 0 78px",textAlign:"center"}} title={historicalTime(row.start) + " · " + row.id + " · " + row.recorded.toFixed(2) + " raw interval hours"}>
        <small>{row.recorded.toFixed(1)}h</small>
        <div style={{height:110,display:"flex",flexDirection:"column-reverse",background:"#f1f5f9",overflow:"hidden",borderRadius:3}}>
          {codes.map((code,i) => <div key={code} title={stateLabel(code) + ": " + (row.totals.get(code) ?? 0).toFixed(2) + "h"} style={{height:Math.max(0,Math.min(110,110 * (row.totals.get(code) ?? 0) / Math.max(row.duration,row.recorded,0.01))) + "px",background:["#287da8","#d97706","#64748b","#7c3aed","#059669","#be123c"][i % 6]}} />)}
        </div>
        <small>{historicalTime(row.start).slice(0,6)} #{index+1}</small>
        {row.overlap && <small title="Raw intervals overlap">Overlap</small>}{row.incomplete && <small title="Missing interval coverage">Gaps</small>}
      </div>)}
    </div>
    <div style={{overflowX:"auto"}}><table><thead><tr><th>Shift start (IST)</th><th>Shift</th><th>Window hours</th><th>Distinct covered hours</th>{codes.map(code => <th key={code}>{stateLabel(code)} ({code}) hours</th>)}<th>Coverage check</th></tr></thead>
      <tbody>{visibleRows.map(row => <tr key={row.start + row.id}><td title={row.start}>{historicalTime(row.start)}</td><td>{row.id}</td><td>{row.duration.toFixed(2)}</td><td>{row.covered.toFixed(2)}</td>
        {codes.map(code => <td key={code}>{(row.totals.get(code) ?? 0).toFixed(2)}</td>)}
        <td>{row.overlap && row.incomplete ? "Overlap and gaps — review" : row.overlap ? "Overlapping evidence — review" : row.incomplete ? "Incomplete interval coverage" : "Raw intervals cover window"}</td>
      </tr>)}</tbody></table></div>
  </section>;
}
function HistoricalShiftSummary({ rows }: { rows: readonly ShiftAggregate[] }) {
  if (!rows.length) return null;
  const shifts = new Map<string, { shiftId: string; started: string; inputs: ShiftAggregate[] }>();
  for (const row of rows) {
    const started = row.shiftStartsAtUtc ?? "Unknown shift start";
    const key = JSON.stringify([row.shiftId, started]);
    const group = shifts.get(key);
    if (group) group.inputs.push(row);
    else shifts.set(key, { shiftId: row.shiftId ?? "Unspecified shift", started, inputs: [row] });
  }
  const sorted = [...shifts.values()].sort((a,b) => a.started.localeCompare(b.started));
  return <section aria-label="Historical V1 shift evidence overview">
    <h3>Recorded shift inputs</h3>
    <p>{sorted.length} imported shifts. Values are shown individually as recorded in V1; different inputs and units are never added together.</p>
    <div style={{overflowX:"auto"}}>
      <table><thead><tr><th>Shift start (IST)</th><th>Shift</th><th>Recorded input values</th></tr></thead>
        <tbody>{sorted.map(shift => <tr key={shift.started + "|" + shift.shiftId}>
          <td title={shift.started}>{historicalTime(shift.started)}</td><td>{shift.shiftId}</td>
          <td>{shift.inputs.map(input => <div key={input.sourceRowId}><strong>{input.metricInputKey}</strong>: {input.aggregateValue === null ? "Unavailable" : input.aggregateValue + (input.unit ? " " + input.unit : "")}</div>)}</td>
        </tr>)}</tbody>
      </table>
    </div>
  </section>;
}
// Query smaller date windows when the API refuses more than 2,000 raw periods.
// Windows overlap at their boundaries because long intervals may span multiple days.
async function readActivity(base: string, from: string, through: string): Promise<Period[]> {
  const start = Date.parse(from + "T00:00:00Z");
  const end = Date.parse(through + "T00:00:00Z");
  const read = async (a: number, b: number): Promise<Period[]> => {
    const first = new Date(a).toISOString().slice(0, 10);
    const last = new Date(b).toISOString().slice(0, 10);
    const response = await fetch(base + "/activity?from=" + first + "&through=" + last, { credentials: "include", cache: "no-store" });
    if (response.status === 413 && a < b) {
      const middle = a + Math.floor((b - a) / (2 * 86_400_000)) * 86_400_000;
      const left = await read(a, middle);
      const right = await read(middle + 86_400_000, b);
      return [...left, ...right];
    }
    if (!response.ok) throw Error("Historical activity HTTP " + response.status + " (" + first + " to " + last + ")");
    const result = await response.json() as ActivityReply;
    if (result.source !== expectedSource || result.isSample || !Array.isArray(result.periods)) throw Error("Unexpected historical activity response");
    return result.periods;
  };
  const records = await read(start, end);
  const unique = new Map<string, Period>();
  for (const p of records) {
    const key = JSON.stringify([p.sourceDatabase, p.sourceMachineId, p.projectionRevision, p.outputOrdinal, p.startedAt, p.endedAt, p.machineState]);
    unique.set(key, p);
  }
  return [...unique.values()];
}
export default function HistoricalReportingPage() {
  const [machines, setMachines] = useState<Machine[]>([]);
  const [machineId, setMachineId] = useState("");
  const [from, setFrom] = useState("2026-10-22");
  const [through, setThrough] = useState("2026-10-31");
  const [metrics, setMetrics] = useState<Metric[]>([]);
  const [periods, setPeriods] = useState<Period[]>([]);
  const [shiftAggregates, setShiftAggregates] = useState<ShiftAggregate[]>([]);
  const [shiftError, setShiftError] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    void fetch("/api/reporting/v2/legacy/machines", { credentials: "include", signal: controller.signal, cache: "no-store" })
      .then(async response => { if (!response.ok) throw Error("Machine list HTTP " + response.status); return response.json() as Promise<MachineReply>; })
      .then(data => { if (data.source !== expectedSource || data.isSample || !Array.isArray(data.machines)) throw Error("Unexpected historical machine response"); setMachines(data.machines); })
      .catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, []);
  const days = (Date.parse(through + "T00:00:00Z") - Date.parse(from + "T00:00:00Z")) / 86_400_000;
  const valid = machineId && Number.isFinite(days) && days >= 0 && days < 31;
  async function load() {
    if (!valid) return;
    setLoading(true); setLoaded(false); setError(""); setMetrics([]); setPeriods([]); setShiftAggregates([]); setShiftError("");
    try {
      const base = `/api/reporting/v2/legacy/machines/${encodeURIComponent(machineId)}`;
      const qs = `?from=${encodeURIComponent(from)}&through=${encodeURIComponent(through)}`;
      // Independent read failures must not hide successfully loaded historical metrics or activity.
      const results = await Promise.allSettled([
        fetch(base + "/metrics" + qs, { credentials: "include", cache: "no-store" }).then(async response => {
          if (!response.ok) throw Error("Historical metrics HTTP " + response.status);
          const result = await response.json() as MetricReply;
          if (result.source !== expectedSource || result.isSample || !Array.isArray(result.metrics)) throw Error("Unexpected historical metrics response");
          return result.metrics;
        }),
        readActivity(base, from, through),
        fetch(base + "/shift-aggregates" + qs, { credentials: "include", cache: "no-store" }).then(async response => {
          if (!response.ok) throw Error("Historical shift aggregates HTTP " + response.status);
          const result = await response.json() as ShiftReply;
          if (result.source !== expectedSource || result.isSample || !Array.isArray(result.aggregates)) throw Error("Unexpected historical shift response");
          return result.aggregates;
        }),
      ]);
      const [metricResult, activityResult, shiftResult] = results;
      if (metricResult.status === "fulfilled") setMetrics(metricResult.value);
      if (activityResult.status === "fulfilled") setPeriods(activityResult.value);
      if (shiftResult.status === "fulfilled") setShiftAggregates(shiftResult.value);
      else setShiftError(String(shiftResult.reason));
      const failed = [metricResult, activityResult].filter(result => result.status === "rejected")
        .map(result => String((result as PromiseRejectedResult).reason));
      setError(failed.join("; "));
      setLoaded(results.some(result => result.status === "fulfilled"));
    } catch (e) { setError(String(e)); }
    finally { setLoading(false); }
  }
  const daily = useMemo(() => buildDaily(metrics), [metrics]);
  const activity = useMemo(() => splitActivityByIndiaDay(periods, from, through), [periods, from, through]);
  const activityDates = [...new Set(activity.map(row => row.date))];
  return <section aria-label="Imported Gajra historical reporting">
    <h1>Historical machine reporting</h1>
    <p><strong>Imported V1 history · not live V2 status.</strong> Availability and Utilization are historical projections. Performance, Quality and OEE remain unavailable unless the source has a valid numerical result.</p>
    {error && <p role="alert">{error}</p>}
    <div><label>Historical machine <select value={machineId} onChange={e => { setMachineId(e.target.value); setLoaded(false); }}><option value="">Select machine</option>{machines.map(m => <option key={m.sourceDatabase + m.sourceMachineId} value={m.sourceMachineId}>{m.deviceKey}</option>)}</select></label>{" "}
      <label>From <input type="date" value={from} onChange={e => { setFrom(e.target.value); setLoaded(false); }} /></label>{" "}
      <label>Through <input type="date" value={through} onChange={e => { setThrough(e.target.value); setLoaded(false); }} /></label>{" "}
      <button type="button" disabled={!valid || loading} onClick={() => { void load(); }}>{loading ? "Loading…" : "Load historical report"}</button>
    </div>
    {!valid && machineId && <p role="alert">Select an inclusive range of at most 31 days.</p>}
    {loaded && <><p>Imported source: {expectedSource}. {metrics.length} metric projections; {periods.length} raw activity periods.</p>
      <Trend rows={daily} name="Availability" /><Trend rows={daily} name="Utilization" />
      <ActivityDailyChart rows={activity} />
      <ShiftMetricOverview metrics={metrics} machine={machines.find(m => m.sourceMachineId === machineId)?.deviceKey ?? machineId} />
      <HistoricalShiftSummary rows={shiftAggregates} />
      <HistoricalScheduleReconciliation shifts={shiftAggregates} />
      <ShiftActivityBreakdown periods={periods} shifts={shiftAggregates} />
      <div style={{display:"flex", flexWrap:"wrap", gap:12, margin:"20px 0"}} aria-label="Historical report coverage">
        {[["Historical projections",metrics.length],["Valid daily A/U points",daily.length],["Imported shift inputs",shiftAggregates.length],["Completed activity intervals",periods.length]].map(([label,value]) => <div key={label} style={{flex:"1 1 165px",padding:16,border:"1px solid #cbd5e1",borderRadius:8}}><div style={{fontSize:24,fontWeight:700}}>{value}</div><div>{label}</div></div>)}
      </div>
      <p>Performance, Quality and OEE: unavailable where V1 did not record valid source values. These counts are evidence coverage, not production KPIs.</p>
      <details><summary><strong>Historical metric results ({metrics.filter(m => oeeMetric.test(m.metricKey)).length} rows)</strong> · expand source details</summary>
      <table><thead><tr><th>Date</th><th>Shift</th><th>Metric</th><th>Value or source status</th></tr></thead>
        <tbody>{metrics.filter(m => oeeMetric.test(m.metricKey)).map(m => <tr key={m.sourceRowId}><td>{m.productionBusinessDate ?? "—"}</td><td>{m.shiftId ?? m.shiftStartsAtUtc ?? "Daily"}</td><td>{m.metricKey}</td><td>{formatMetric(m)}</td></tr>)}</tbody>
      </table>
      </details>
      {shiftError && <p role="alert">Shift input records unavailable: {shiftError}. Other historical sections remain usable.</p>}
      <details><summary><strong>Historical shift inputs ({shiftAggregates.length} rows)</strong> · expand source details</summary>
      <p>These are the original V1 shift aggregate values, not recalculated targets or OEE. Historical shift definitions and break exclusions remain as recorded in V1.</p>
      <table><thead><tr><th>Shift</th><th>Shift start UTC</th><th>Input</th><th>Source value</th><th>Input count</th></tr></thead><tbody>
        {shiftAggregates.map(row => <tr key={row.sourceRowId}><td>{row.shiftId ?? "Unspecified"}</td><td title={row.shiftStartsAtUtc ?? undefined}>{historicalTime(row.shiftStartsAtUtc)}</td><td>{row.metricInputKey}</td><td>{row.aggregateValue === null ? "Unavailable" : row.aggregateValue + " " + (row.unit ?? "")}</td><td>{row.inputCount ?? "—"}</td></tr>)}
      </tbody></table>
      {!shiftAggregates.length && !shiftError && <p>No historical shift aggregates in the selected range.</p>}
      </details>
      <details><summary><strong>Activity duration by India calendar day ({activity.length} rows)</strong> · expand source details</summary>
      <p>Completed source intervals split at local midnight. Machine-state names follow the V1 MachineState enum; numeric codes are preserved. These are not shift-adjusted production durations.</p>
      <table><thead><tr><th>Day</th><th>V1 machine state</th><th>Hours</th></tr></thead><tbody>
        {activity.map(row => <tr key={row.date + ":" + row.state}><td>{row.date}</td><td>{stateLabel(row.state)} ({row.state})</td><td>{(row.seconds / 3600).toFixed(2)}</td></tr>)}
      </tbody></table>
      {!activityDates.length && <p>No completed activity intervals in this range.</p>}
      </details>
    </>}
  </section>;
}
