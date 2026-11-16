import { useEffect, useMemo, useState } from "react";

interface Machine { sourceMachineId: string; deviceKey: string; sourceDatabase: string }
interface Metric { sourceRowId: number; productionBusinessDate: string | null; shiftId: string | null; shiftStartsAtUtc: string | null; metricKey: string; status: number; metricValue: string | null; unit: string | null; reasonCode: number | null }
interface Period { sourceDatabase: string; sourceMachineId: string; projectionRevision: number; outputOrdinal: number; machineState: number; startedAt: string; endedAt: string }
interface MachineReply { source: string; isSample: boolean; machines: Machine[] }
interface MetricReply { source: string; isSample: boolean; metrics: Metric[] }
interface ActivityReply { source: string; isSample: boolean; periods: Period[] }
type Daily = { date: string; metric: string; value: number; unit: string };
const expectedSource = "imported-v1-legacy";
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
    setLoading(true); setLoaded(false); setError(""); setMetrics([]); setPeriods([]);
    try {
      const base = `/api/reporting/v2/legacy/machines/${encodeURIComponent(machineId)}`;
      const qs = `?from=${encodeURIComponent(from)}&through=${encodeURIComponent(through)}`;
      const [metricResponse, activityPeriods] = await Promise.all([
        fetch(base + "/metrics" + qs, { credentials: "include", cache: "no-store" }),
        readActivity(base, from, through),
      ]);
      if (!metricResponse.ok) throw Error("Historical metrics HTTP " + metricResponse.status);
      const m = await metricResponse.json() as MetricReply;
      if (m.source !== expectedSource || m.isSample || !Array.isArray(m.metrics)) throw Error("Unexpected historical metrics response");
      setMetrics(m.metrics); setPeriods(activityPeriods); setLoaded(true);
    } catch (e) { setError(String(e)); }
    finally { setLoading(false); }
  }
  const daily = useMemo(() => buildDaily(metrics), [metrics]);
  const activity = useMemo(() => splitActivityByIndiaDay(periods, from, through), [periods, from, through]);
  const activityDates = [...new Set(activity.map(row => row.date))];
  return <section aria-label="Imported Gajra historical reporting">
    <h1>Historical Gajra reporting</h1>
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
      <h2>Historical metric results</h2>
      <table><thead><tr><th>Date</th><th>Shift</th><th>Metric</th><th>Value or source status</th></tr></thead>
        <tbody>{metrics.filter(m => oeeMetric.test(m.metricKey)).map(m => <tr key={m.sourceRowId}><td>{m.productionBusinessDate ?? "—"}</td><td>{m.shiftId ?? m.shiftStartsAtUtc ?? "Daily"}</td><td>{m.metricKey}</td><td>{formatMetric(m)}</td></tr>)}</tbody>
      </table>
      <h2>Activity duration by India calendar day</h2>
      <p>Completed source intervals split at local midnight. State codes are preserved until V1 state definitions are verified. These are not shift-adjusted production durations.</p>
      <table><thead><tr><th>Day</th><th>V1 state code</th><th>Hours</th></tr></thead><tbody>
        {activity.map(row => <tr key={row.date + ":" + row.state}><td>{row.date}</td><td>{row.state}</td><td>{(row.seconds / 3600).toFixed(2)}</td></tr>)}
      </tbody></table>
      {!activityDates.length && <p>No completed activity intervals in this range.</p>}
    </>}
  </section>;
}
