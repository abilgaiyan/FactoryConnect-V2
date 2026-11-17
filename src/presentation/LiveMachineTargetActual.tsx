import { useEffect, useState } from "react";

interface Shift { shiftId: string; displayName: string }
interface Target { shiftId: string; targetQuantity: number; quantityUnit: string; revision: number; productionOrder: string | null; partNumber: string | null; operation: string | null }
interface Actual { shiftId: string; goodQuantity: number; rejectedQuantity: number; quantityUnit: string; revision: number; productionOrder: string | null; partNumber: string | null; operation: string | null }
type Row = { shift: Shift; target: Target | undefined; actual: Actual | undefined };
function same(a: string, b: string) { return a.toLowerCase() === b.toLowerCase(); }
function compatible(target: Target, actual: Actual) {
  return same(target.quantityUnit.trim(), actual.quantityUnit.trim()) &&
    ["productionOrder", "partNumber", "operation"].every(key => {
      const k = key as "productionOrder" | "partNumber" | "operation";
      return (target[k] ?? "").trim().toLowerCase() === (actual[k] ?? "").trim().toLowerCase();
    });
}
export default function LiveMachineTargetActual({ machineId, businessDate }: {
  readonly machineId: string; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [rows, setRows] = useState<Row[] | null>(null);
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setRows(null); setError("");
    const options = { credentials: "include" as const, signal: controller.signal };
    const scoped = async <T,>(url: string, field: string): Promise<T[]> => {
      const response = await fetch(url, options);
      if (!response.ok) throw Error(`Reporting source unavailable (HTTP ${response.status})`);
      const data = await response.json();
      if (data.isSample !== false || !Array.isArray(data[field]) ||
          !same(data.machineId, machineId) || data.businessDate !== businessDate)
        throw Error("Reporting source scope or evidence mismatch");
      return data[field] as T[];
    };
    void Promise.all([
      fetch("/api/planning/v2/shifts", options).then(async response => {
        if (!response.ok) throw Error(`Configured shifts unavailable (HTTP ${response.status})`);
        const data = await response.json();
        if (data.isSample !== false || !Array.isArray(data.shifts)) throw Error("Invalid shift configuration");
        return data.shifts as Shift[];
      }),
      scoped<Target>(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/targets?businessDate=${encodeURIComponent(businessDate)}`, "targets"),
      scoped<Actual>(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/production?businessDate=${encodeURIComponent(businessDate)}`, "records")
    ]).then(([shifts, targets, actuals]) => {
      if (controller.signal.aborted) return;
      setRows(shifts.map(shift => ({
        shift, target: targets.find(t => same(t.shiftId, shift.shiftId)),
        actual: actuals.find(a => same(a.shiftId, shift.shiftId))
      })));
    }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, machineId, businessDate, retry]);
  const comparable = rows?.filter(r => r.target && r.actual && compatible(r.target, r.actual)) ?? [];
  const missing = rows?.filter(r => !r.target || !r.actual).length ?? 0;
  const mismatch = rows?.filter(r => r.target && r.actual && !compatible(r.target, r.actual)).length ?? 0;
  return <section aria-label="Shift target versus actual report">
    <h3>Target vs actual · declared production</h3>
    <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}>{open ? "Hide target vs actual" : "Load target vs actual"}</button>
    {open && <>
      <p>Machine {machineId} · business date {businessDate}. Compares latest SQL target revisions with supervisor-declared good and rejected quantities, not machine-verified production.</p>
      {error && <p role="alert">{error}. No sample fallback. <button type="button" onClick={() => setRetry(v => v + 1)}>Retry report</button></p>}
      {!error && !rows && <p role="status">Loading live SQL targets and declared production…</p>}
      {rows && <>
        <p role="status">{comparable.length} comparable shifts · {missing} missing target or actual · {mismatch} incompatible unit or job context.</p>
        {rows.length === 0 ? <p>No configured shifts. Reporting is unavailable.</p> :
          <table><thead><tr><th>Shift</th><th>Target</th><th>Good</th><th>Rejected</th><th>Total declared</th><th>Achievement</th><th>Evidence status</th></tr></thead>
            <tbody>{rows.map(({ shift, target, actual }) => {
              const ok = target && actual && compatible(target, actual);
              const total = actual ? actual.goodQuantity + actual.rejectedQuantity : null;
              const percent = ok && target.targetQuantity > 0 ? (100 * total! / target.targetQuantity) : null;
              const status = !target && !actual ? "Target and actual missing" : !target ? "Target missing" :
                !actual ? "Actual missing" : !ok ? "Unit or job context mismatch" :
                target.targetQuantity === 0 ? "Zero target; achievement undefined" : "Comparable supervisor declaration";
              return <tr key={shift.shiftId}>
                <th scope="row">{shift.displayName}</th>
                <td>{target ? `${target.targetQuantity} ${target.quantityUnit}` : "Not recorded"}</td>
                <td>{actual ? `${actual.goodQuantity} ${actual.quantityUnit}` : "Not recorded"}</td>
                <td>{actual ? `${actual.rejectedQuantity} ${actual.quantityUnit}` : "Not recorded"}</td>
                <td>{actual ? `${total} ${actual.quantityUnit}` : "Not recorded"}</td>
                <td>{percent === null ? "—" : `${percent.toFixed(1)}%`}</td>
                <td>{status}</td>
              </tr>;
            })}</tbody></table>}
        <section aria-label="Shift production achievement charts">
          <h4>Production plan vs declared actual · shift charts</h4>
          <p>Each chart uses its own scale; compare the target and declared production within the same shift only. Blue indicates target, green indicates declared good, and orange indicates declared rejected. Missing or incompatible evidence is not plotted.</p>
          {comparable.filter(r => r.target && r.actual && r.target.targetQuantity > 0).length === 0 &&
            <p>No comparable positive-target shift records are available to chart.</p>}
          {comparable.filter(r => r.target && r.actual && r.target.targetQuantity > 0).map(({ shift, target, actual }) => {
            if (!target || !actual) return null;
            const total = actual.goodQuantity + actual.rejectedQuantity;
            const maximum = Math.max(target.targetQuantity, total);
            const scale = (quantity: number) => maximum > 0 ? 340 * quantity / maximum : 0;
            return <figure key={shift.shiftId}>
              <figcaption><strong>{shift.displayName}</strong> · {target.quantityUnit} · target revision {target.revision}, declared revision {actual.revision}</figcaption>
              <svg viewBox="0 0 510 110" role="img" aria-label={`${shift.displayName}: target ${target.targetQuantity}, declared good ${actual.goodQuantity}, declared rejected ${actual.rejectedQuantity} ${target.quantityUnit}`} style={{ width: "100%", maxWidth: 650, height: "auto" }}>
                <text x="0" y="29" fontSize="14" fill="currentColor">Target</text>
                <rect x="140" y="13" width={scale(target.targetQuantity)} height="24" fill="#3974b9" />
                <text x="145" y="30" fontSize="13" fill="#fff">{target.targetQuantity}</text>
                <text x="0" y="75" fontSize="14" fill="currentColor">Declared</text>
                <rect x="140" y="59" width={scale(actual.goodQuantity)} height="24" fill="#278558" />
                <rect x={140 + scale(actual.goodQuantity)} y="59" width={scale(actual.rejectedQuantity)} height="24" fill="#c27620" />
                <text x="145" y="76" fontSize="13" fill={scale(actual.goodQuantity) > 40 ? "#fff" : "currentColor"}>{total}</text>
              </svg>
              <p>Declared total {total} / target {target.targetQuantity} = {(100 * total / target.targetQuantity).toFixed(1)}% of plan. Good {actual.goodQuantity}; rejected {actual.rejectedQuantity}.</p>
            </figure>;
          })}
        </section>
        <p>Achievement = (declared good + rejected) / recorded target, only when units and production context match and the target is positive. This is plan achievement, not OEE Performance, Quality or OEE.</p>
        <button type="button" onClick={() => setRetry(v => v + 1)}>Refresh report after edits</button>
      </>}
    </>}
  </section>;
}
