import { useEffect, useState } from "react";

type Machine = { machineId: string; displayName: string };
type Quantity = { shiftId: string; quantityUnit: string; productionOrder: string | null; partNumber: string | null; operation: string | null };
type Target = Quantity & { targetQuantity: number };
type Actual = Quantity & { goodQuantity: number; rejectedQuantity: number };
type Summary = { machine: Machine; comparable: number; missing: number; incompatible: number; target: number; good: number; rejected: number; unit: string | null; error: string | null };
function same(a: string, b: string) { return a.trim().toLowerCase() === b.trim().toLowerCase(); }
function compatible(a: Quantity, b: Quantity) {
  return same(a.quantityUnit, b.quantityUnit) &&
    (["productionOrder", "partNumber", "operation"] as const).every(k => same(a[k] ?? "", b[k] ?? ""));
}
// Protect exported spreadsheet cells from formula execution and preserve CSV quoting.
function csvCell(value: string | number | null) {
  const raw = String(value ?? "");
  const safe = /^[\\s]*[=+@-]/.test(raw) ? "'" + raw : raw;
  return '"' + safe.replace(/"/g, '""') + '"';
}
function exportCsv(rows: readonly Summary[], date: string) {
  const header = ["Business date", "Machine ID", "Machine", "Target", "Good", "Rejected", "Unit", "Achievement percent", "Comparable shifts", "Missing shifts", "Incompatible shifts", "Evidence status"];
  const records = rows.map(r => {
    const present = r.comparable > 0 && !r.error;
    const status = r.error ? "Reporting unavailable" : r.missing || r.incompatible ? "Partial evidence" : present ? "Comparable declarations" : "No comparable evidence";
    return [date, r.machine.machineId, r.machine.displayName, present ? r.target : null,
      present ? r.good : null, present ? r.rejected : null, present ? r.unit : null,
      present && r.target > 0 ? (100 * (r.good + r.rejected) / r.target).toFixed(1) : null,
      r.comparable, r.missing, r.incompatible, status];
  });
  const csv = [header, ...records].map(row => row.map(csvCell).join(",")).join("\r\n") + "\r\n";
  const url = URL.createObjectURL(new Blob(["\uFEFF", csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `factoryconnect-production-${date}.csv`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
export default function ManagementProductionSummary({ machines, businessDate }: {
  readonly machines: readonly Machine[]; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [rows, setRows] = useState<Summary[] | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    if (!open || !businessDate) return;
    const controller = new AbortController();
    setRows(null); setError("");
    const options = { credentials: "include" as const, signal: controller.signal };
    async function read<T>(url: string, key: string, machineId?: string): Promise<T[]> {
      const response = await fetch(url, options);
      if (!response.ok) throw Error(`SQL reporting unavailable (HTTP ${response.status})`);
      const data = await response.json();
      if (data.isSample !== false || !Array.isArray(data[key]) ||
        (machineId && (!same(data.machineId, machineId) || data.businessDate !== businessDate)))
        throw Error("SQL reporting scope or evidence mismatch");
      return data[key] as T[];
    }
    void (async () => {
      const shifts = await read<{ shiftId: string }>("/api/planning/v2/shifts", "shifts");
      const results: Summary[] = [];
      // Sequential per-machine requests keep reporting bounded and preserve partial failures.
      for (const machine of machines) {
        if (controller.signal.aborted) return;
        try {
          const base = `/api/planning/v2/machines/${encodeURIComponent(machine.machineId)}`;
          const query = `?businessDate=${encodeURIComponent(businessDate)}`;
          const [targets, actuals] = await Promise.all([
            read<Target>(base + "/targets" + query, "targets", machine.machineId),
            read<Actual>(base + "/production" + query, "records", machine.machineId)
          ]);
          let comparable = 0, missing = 0, incompatible = 0, target = 0, good = 0, rejected = 0;
          let unit: string | null = null;
          for (const shift of shifts) {
            const t = targets.find(v => same(v.shiftId, shift.shiftId));
            const a = actuals.find(v => same(v.shiftId, shift.shiftId));
            if (!t || !a) { missing++; continue; }
            if (!compatible(t, a) || (unit !== null && !same(unit, t.quantityUnit))) { incompatible++; continue; }
            unit = t.quantityUnit;
            comparable++; target += t.targetQuantity; good += a.goodQuantity; rejected += a.rejectedQuantity;
          }
          results.push({ machine, comparable, missing, incompatible, target, good, rejected, unit, error: null });
        } catch (e) {
          results.push({ machine, comparable: 0, missing: 0, incompatible: 0, target: 0, good: 0, rejected: 0, unit: null, error: String(e) });
        }
      }
      if (!controller.signal.aborted) setRows(results);
    })().catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, machines, businessDate, refresh]);
  const valid = rows?.filter(r => r.comparable > 0 && !r.error) ?? [];
  const units = [...new Set(valid.map(r => r.unit?.trim().toLowerCase()))];
  const aggregate = units.length === 1 && valid.length === machines.length && valid.every(r => r.missing === 0 && r.incompatible === 0);
  return <section aria-label="Management declared production summary">
    <h2>Production plan vs actual · management</h2>
    <p>Selected-day SQL planning and supervisor declarations for the selected machines. This is not a qualified OEE or machine-verified production report.</p>
    {!businessDate ? <p>Select a production day to load declared production.</p> :
      <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}>{open ? "Hide production summary" : "Load production summary"}</button>}
    {open && businessDate && <>
      {error && <p role="alert">{error}. No sample fallback. <button type="button" onClick={() => setRefresh(v => v + 1)}>Retry</button></p>}
      {!error && rows === null && <p role="status">Loading SQL production records for {machines.length} selected machines…</p>}
      {rows && <>
        <p>{valid.length} of {machines.length} machines have comparable shift records. Totals are withheld unless all selected machines and shifts have compatible evidence and one common unit.</p>
        <table><thead><tr><th>Machine</th><th>Target</th><th>Good</th><th>Rejected</th><th>Plan achievement</th><th>Coverage</th></tr></thead>
          <tbody>{rows.map(r => <tr key={r.machine.machineId}><th scope="row">{r.machine.displayName}</th>
            <td>{r.comparable ? `${r.target} ${r.unit}` : "—"}</td>
            <td>{r.comparable ? r.good : "—"}</td><td>{r.comparable ? r.rejected : "—"}</td>
            <td>{r.comparable && r.target > 0 ? `${(100 * (r.good + r.rejected) / r.target).toFixed(1)}%` : "—"}</td>
            <td>{r.error ? r.error : `${r.comparable} comparable · ${r.missing} missing · ${r.incompatible} incompatible`}</td>
          </tr>)}</tbody>
        </table>
        {aggregate ? <p><strong>Complete selected-machine total:</strong> Target {valid.reduce((n, r) => n + r.target, 0)} {units[0]}; good {valid.reduce((n, r) => n + r.good, 0)}; rejected {valid.reduce((n, r) => n + r.rejected, 0)}.</p> :
          <p>Factory-wide totals withheld: missing, incompatible or mixed-unit evidence must not be silently combined.</p>}
        <button type="button" onClick={() => exportCsv(rows, businessDate)}>Export selected-machine production CSV</button>
        <p>CSV includes reporting coverage and explicitly leaves unavailable quantities blank. Exported values are supervisor declarations, not qualified OEE.</p>
        <h3>Machine production comparisons</h3>
        {valid.map(r => {
          const maximum = Math.max(1, r.target, r.good + r.rejected);
          return <figure key={r.machine.machineId}>
            <figcaption>{r.machine.displayName} · {r.unit} · {r.comparable} comparable shifts</figcaption>
            <svg viewBox="0 0 440 56" role="img" aria-label={`${r.machine.displayName}: target ${r.target}, good ${r.good}, rejected ${r.rejected}`} style={{ width: "100%", maxWidth: 540, height: "auto" }}>
              <rect x="0" y="3" width={400 * r.target / maximum} height="19" fill="#3974b9"/>
              <rect x="0" y="30" width={400 * r.good / maximum} height="19" fill="#278558"/>
              <rect x={400 * r.good / maximum} y="30" width={400 * r.rejected / maximum} height="19" fill="#c27620"/>
            </svg>
            <p>Target {r.target}; declared good {r.good}; rejected {r.rejected}</p>
          </figure>;
        })}
        <p>Blue = target; green = good; orange = rejected. Each machine uses its own scale. No fleet OEE inferred.</p>
        <button type="button" onClick={() => setRefresh(v => v + 1)}>Refresh management production</button>
      </>}
    </>}
  </section>;
}
