import { useEffect, useState } from "react";

interface Machine { machineId: string; displayName: string }
interface Quantity { shiftId: string; quantityUnit: string; productionOrder: string | null; partNumber: string | null; operation: string | null }
interface Target extends Quantity { targetQuantity: number }
interface Actual extends Quantity { goodQuantity: number; rejectedQuantity: number }
interface Day { date: string; target: number; good: number; rejected: number; comparable: number; missing: number; incompatible: number; unit: string | null; errors: number }
const same = (a: string, b: string) => a.trim().toLowerCase() === b.trim().toLowerCase();
function matching(a: Quantity, b: Quantity) {
  return same(a.quantityUnit, b.quantityUnit) &&
    (["productionOrder", "partNumber", "operation"] as const).every(k => same(a[k] ?? "", b[k] ?? ""));
}
function sevenDates(date: string) {
  const end = new Date(date + "T00:00:00Z");
  if (!Number.isFinite(end.getTime()) || end.toISOString().slice(0, 10) !== date) return [];
  return Array.from({ length: 7 }, (_, i) => {
    const d = new Date(end);
    d.setUTCDate(d.getUTCDate() - 6 + i);
    return d.toISOString().slice(0, 10);
  });
}
// Quote every field and prevent spreadsheet applications interpreting untrusted labels as formulas.
function csvCell(value: string | number | null) {
  const raw = String(value ?? "");
  const safe = /^\s*[=+@-]/.test(raw) ? "'" + raw : raw;
  return '"' + safe.replace(/"/g, '""') + '"';
}
function exportTrendCsv(days: readonly Day[], businessDate: string, selectedMachines: number) {
  const header = ["Business date", "Selected machines", "Target", "Good", "Rejected", "Unit",
    "Achievement percent", "Comparable shifts", "Missing shifts", "Incompatible shifts",
    "Failed machine reads", "Evidence status"];
  const rows = days.map(d => {
    const complete = d.comparable > 0 && d.missing === 0 && d.incompatible === 0 && d.errors === 0;
    return [d.date, selectedMachines, complete ? d.target : null, complete ? d.good : null,
      complete ? d.rejected : null, complete ? d.unit : null,
      complete && d.target > 0 ? (100 * (d.good + d.rejected) / d.target).toFixed(1) : null,
      d.comparable, d.missing, d.incompatible, d.errors,
      complete ? "Complete declared production" : "Incomplete evidence - quantities withheld"];
  });
  const csv = [header, ...rows].map(row => row.map(csvCell).join(",")).join("\r\n") + "\r\n";
  const url = URL.createObjectURL(new Blob(["\uFEFF", csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `factoryconnect-management-trend-${businessDate}.csv`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
export default function ManagementProductionTrend({ machines, businessDate }: {
  readonly machines: readonly Machine[]; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [days, setDays] = useState<Day[] | null>(null);
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!open || !businessDate || machines.length === 0 || machines.length > 10) return;
    const dates = sevenDates(businessDate);
    if (dates.length !== 7) { setError("Invalid business date"); return; }
    const controller = new AbortController();
    setDays(null); setError("");
    const options = { credentials: "include" as const, signal: controller.signal };
    async function read<T>(url: string, field: string, machineId?: string, date?: string): Promise<T[]> {
      const response = await fetch(url, options);
      if (!response.ok) throw Error(`Reporting unavailable (HTTP ${response.status})`);
      const data = await response.json();
      if (data.isSample !== false || !Array.isArray(data[field]) ||
        (machineId && (!same(data.machineId, machineId) || data.businessDate !== date)))
        throw Error("Reporting evidence or scope mismatch");
      return data[field] as T[];
    }
    void (async () => {
      const shifts = await read<{ shiftId: string }>("/api/planning/v2/shifts", "shifts");
      const results: Day[] = [];
      // Deliberately bounded to ten machines and seven dates; no invented fleet OEE.
      for (const date of dates) {
        let target = 0, good = 0, rejected = 0, comparable = 0, missing = 0, incompatible = 0, errors = 0;
        let unit: string | null = null;
        for (const machine of machines) {
          if (controller.signal.aborted) return;
          try {
            const base = `/api/planning/v2/machines/${encodeURIComponent(machine.machineId)}`;
            const q = `?businessDate=${encodeURIComponent(date)}`;
            const [targets, actuals] = await Promise.all([
              read<Target>(base + "/targets" + q, "targets", machine.machineId, date),
              read<Actual>(base + "/production" + q, "records", machine.machineId, date)
            ]);
            for (const shift of shifts) {
              const t = targets.find(v => same(v.shiftId, shift.shiftId));
              const a = actuals.find(v => same(v.shiftId, shift.shiftId));
              if (!t || !a) { missing++; continue; }
              if (!matching(t, a) || (unit !== null && !same(unit, t.quantityUnit))) { incompatible++; continue; }
              unit = t.quantityUnit;
              target += t.targetQuantity; good += a.goodQuantity; rejected += a.rejectedQuantity; comparable++;
            }
          } catch { errors++; }
        }
        results.push({ date, target, good, rejected, comparable, missing, incompatible, unit, errors });
      }
      if (!controller.signal.aborted) setDays(results);
    })().catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, businessDate, machines, retry]);
  const complete = (d: Day) => d.comparable > 0 && d.missing === 0 && d.incompatible === 0 && d.errors === 0;
  const completeDays = days?.filter(complete) ?? [];
  const units = [...new Set(completeDays.map(d => d.unit?.toLowerCase()))];
  // Each unit has its own consistent seven-day scale; different units are never visually normalized together.
  const maximumForUnit = (unit: string | null) => Math.max(1, ...completeDays
    .filter(d => d.unit?.trim().toLowerCase() === unit?.trim().toLowerCase())
    .map(d => Math.max(d.target, d.good + d.rejected)));
  return <section aria-label="Management seven-day production trend">
    <h2>Seven-day management production trend</h2>
    <p>SQL targets versus supervisor-declared good and rejected quantities. All seven dates end on the selected production day. Not qualified OEE.</p>
    {machines.length === 0 ? <p>No selected machines. Choose a configured reporting source.</p> :
      machines.length > 10 ? <p>Select a line or up to ten machines to load a bounded trend report.</p> :
      !businessDate ? <p>Select a production day to load trends.</p> :
      <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}>{open ? "Hide seven-day management trend" : "Load seven-day management trend"}</button>}
    {open && businessDate && machines.length > 0 && machines.length <= 10 && <>
      {error && <p role="alert">{error}. No sample fallback. <button type="button" onClick={() => setRetry(v => v + 1)}>Retry</button></p>}
      {!error && !days && <p role="status">Loading seven days of selected-machine SQL records…</p>}
      {days && <>
        <p>{completeDays.length} of 7 dates have complete comparable evidence for all selected machines and configured shifts. Incomplete dates are excluded from the graph.</p>
        <table><thead><tr><th>Date</th><th>Target</th><th>Good</th><th>Rejected</th><th>Achievement</th><th>Coverage</th></tr></thead>
          <tbody>{days.map(d => <tr key={d.date}><th scope="row">{d.date}</th>
            <td>{complete(d) ? `${d.target} ${d.unit}` : "Withheld"}</td>
            <td>{complete(d) ? d.good : "—"}</td><td>{complete(d) ? d.rejected : "—"}</td>
            <td>{complete(d) && d.target > 0 ? `${(100 * (d.good + d.rejected) / d.target).toFixed(1)}%` : "—"}</td>
            <td>{d.comparable} comparable · {d.missing} missing · {d.incompatible} incompatible · {d.errors} failed machines</td>
          </tr>)}</tbody>
        </table>
        <button type="button" onClick={() => exportTrendCsv(days, businessDate, machines.length)}>Export seven-day production CSV</button>
        <p>CSV exports all seven dates with coverage; incomplete dates have blank quantities, not zeros. This is supervisor-declared production, not qualified OEE.</p>
        {units.length > 1 && <p role="alert">Different quantity units occur across dates. Charts share a scale only within the same unit; do not compare bar lengths across units.</p>}
        {completeDays.length === 0 && <p>No complete dates available to chart.</p>}
        {completeDays.map(d => { const maximum = maximumForUnit(d.unit); return <figure key={d.date}>
          <figcaption>{d.date} · {d.unit} · {machines.length} machines</figcaption>
          <svg viewBox="0 0 450 56" role="img" aria-label={`${d.date}: target ${d.target}, good ${d.good}, rejected ${d.rejected} ${d.unit}`} style={{ width: "100%", maxWidth: 550, height: "auto" }}>
            <rect x="0" y="3" width={400 * d.target / maximum} height="19" fill="#3974b9"/>
            <rect x="0" y="30" width={400 * d.good / maximum} height="19" fill="#278558"/>
            <rect x={400 * d.good / maximum} y="30" width={400 * d.rejected / maximum} height="19" fill="#c27620"/>
          </svg>
          <p>Target {d.target}; declared good {d.good}; rejected {d.rejected}</p>
        </figure>; })}
        <p>Blue = target, green = good, orange = rejected. A consistent scale is used across dates with the same unit. Missing evidence is never counted as zero. No fleet OEE or Quality inferred.</p>
        <button type="button" onClick={() => setRetry(v => v + 1)}>Refresh seven-day management trend</button>
      </>}
    </>}
  </section>;
}
