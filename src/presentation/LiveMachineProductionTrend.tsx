import { useEffect, useState } from "react";

type Shift = { shiftId: string; displayName: string };
type Quantity = { shiftId: string; quantityUnit: string; productionOrder: string | null; partNumber: string | null; operation: string | null };
type Target = Quantity & { targetQuantity: number };
type Actual = Quantity & { goodQuantity: number; rejectedQuantity: number };
type Day = { date: string; comparable: number; missing: number; mismatch: number; good: number; rejected: number; target: number; unit: string | null };
function same(a: string, b: string) { return a.toLowerCase() === b.toLowerCase(); }
function compatible(a: Quantity, b: Quantity) {
  return same(a.quantityUnit.trim(), b.quantityUnit.trim()) &&
    (["productionOrder", "partNumber", "operation"] as const).every(k =>
      same((a[k] ?? "").trim(), (b[k] ?? "").trim()));
}
function datesEndingOn(date: string) {
  const last = new Date(date + "T00:00:00Z");
  if (Number.isNaN(last.getTime()) || last.toISOString().slice(0, 10) !== date) return [];
  return Array.from({ length: 7 }, (_, i) => {
    const day = new Date(last);
    day.setUTCDate(last.getUTCDate() - 6 + i);
    return day.toISOString().slice(0, 10);
  });
}
export default function LiveMachineProductionTrend({ machineId, businessDate }: {
  readonly machineId: string; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [days, setDays] = useState<Day[] | null>(null);
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setDays(null); setError("");
    const dates = datesEndingOn(businessDate);
    if (dates.length !== 7) { setError("Invalid business date"); return; }
    const options = { credentials: "include" as const, signal: controller.signal };
    async function read<T>(url: string, key: string, date?: string): Promise<T[]> {
      const response = await fetch(url, options);
      if (!response.ok) throw Error(`SQL reporting source unavailable (HTTP ${response.status})`);
      const data = await response.json();
      if (data.isSample !== false || !Array.isArray(data[key]) ||
          (date && (!same(data.machineId, machineId) || data.businessDate !== date)))
        throw Error("SQL reporting source or scope mismatch");
      return data[key] as T[];
    }
    void (async () => {
      const shifts = await read<Shift>("/api/planning/v2/shifts", "shifts");
      const results: Day[] = [];
      // Bounded seven-day range: two existing SQL API reads per day, no cross-unit totals.
      for (const date of dates) {
        const base = `/api/planning/v2/machines/${encodeURIComponent(machineId)}`;
        const query = `?businessDate=${encodeURIComponent(date)}`;
        const [targets, actuals] = await Promise.all([
          read<Target>(base + "/targets" + query, "targets", date),
          read<Actual>(base + "/production" + query, "records", date)
        ]);
        let comparable = 0, missing = 0, mismatch = 0, good = 0, rejected = 0, target = 0;
        let unit: string | null = null;
        for (const shift of shifts) {
          const t = targets.find(v => same(v.shiftId, shift.shiftId));
          const a = actuals.find(v => same(v.shiftId, shift.shiftId));
          if (!t || !a) { missing++; continue; }
          if (!compatible(t, a) || (unit !== null && !same(unit, t.quantityUnit))) { mismatch++; continue; }
          unit = t.quantityUnit;
          comparable++; good += a.goodQuantity; rejected += a.rejectedQuantity; target += t.targetQuantity;
        }
        results.push({ date, comparable, missing, mismatch, good, rejected, target, unit });
      }
      if (!controller.signal.aborted) setDays(results);
    })().catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, machineId, businessDate, retry]);
  const units = [...new Set((days ?? []).filter(d => d.comparable > 0).map(d => d.unit?.trim().toLowerCase()))];
  return <section aria-label="Seven-day declared production trend">
    <h3>Seven-day production trend · SQL declarations</h3>
    <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}>{open ? "Hide seven-day trend" : "Load seven-day trend"}</button>
    {open && <>
      <p>Seven business dates ending {businessDate}, for the selected machine. Latest recorded revisions only. Not qualified OEE.</p>
      {error && <p role="alert">{error}. No sample fallback. <button type="button" onClick={() => setRetry(v => v + 1)}>Retry trend</button></p>}
      {!error && !days && <p role="status">Reading seven days of SQL planning and declared production…</p>}
      {days && <>
        {units.length > 1 && <p role="alert">Different quantity units occur across days. Each date uses its own chart scale; do not compare bar lengths between dates.</p>}
        <table><thead><tr><th>Business date</th><th>Target</th><th>Good</th><th>Rejected</th><th>Plan achievement</th><th>Coverage</th></tr></thead>
          <tbody>{days.map(d => <tr key={d.date}><th scope="row">{d.date}</th>
            <td>{d.comparable ? `${d.target} ${d.unit}` : "—"}</td>
            <td>{d.comparable ? d.good : "—"}</td><td>{d.comparable ? d.rejected : "—"}</td>
            <td>{d.comparable && d.target > 0 ? `${(100 * (d.good + d.rejected) / d.target).toFixed(1)}%` : "—"}</td>
            <td>{d.comparable} comparable · {d.missing} missing · {d.mismatch} incompatible</td>
          </tr>)}</tbody></table>
        <h4>Daily declared production against recorded targets</h4>
        {days.filter(d => d.comparable > 0).length === 0 && <p>No comparable production evidence to chart.</p>}
        {days.filter(d => d.comparable > 0).map(d => <figure key={d.date}>
          <figcaption>{d.date} · {d.unit} · {d.comparable} comparable shifts</figcaption>
          <svg viewBox="0 0 450 58" role="img" aria-label={`${d.date}: target ${d.target}, good ${d.good}, rejected ${d.rejected} ${d.unit}`} style={{ width: "100%", maxWidth: 550, height: "auto" }}>
            <rect x="0" y="4" width={400 * d.target / Math.max(1, d.target, d.good + d.rejected)} height="20" fill="#3974b9"/>
            <rect x="0" y="31" width={400 * d.good / Math.max(1, d.target, d.good + d.rejected)} height="20" fill="#278558"/>
            <rect x={400 * d.good / max} y="31" width={400 * d.rejected / Math.max(1, d.target, d.good + d.rejected)} height="20" fill="#c27620"/>
          </svg>
          <p>Target {d.target}; declared good {d.good}; rejected {d.rejected}</p>
        </figure>)}
        <p>Blue = target, green = good, orange = rejected. Each date uses its own scale. Daily totals include only matching unit and job context; missing is not zero. Trends are supervisor-declared production, not OEE.</p>
        <button type="button" onClick={() => setRetry(v => v + 1)}>Refresh seven-day trend</button>
      </>}
    </>}
  </section>;
}
