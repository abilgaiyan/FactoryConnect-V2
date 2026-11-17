import { useEffect, useState, type FormEvent } from "react";

interface Shift { shiftId: string; displayName: string }
interface Production {
  shiftId: string; productionRevisionId: string; revision: number;
  goodQuantity: number; rejectedQuantity: number; quantityUnit: string;
  productionOrder: string | null; partNumber: string | null; operation: string | null;
  revisionReason: string; recordedAtUtc: string;
}
export default function LiveMachineShiftProduction({ machineId, businessDate }: {
  readonly machineId: string; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [shifts, setShifts] = useState<Shift[] | null>(null);
  const [records, setRecords] = useState<Production[] | null>(null);
  const [shiftId, setShiftId] = useState("");
  const [good, setGood] = useState("");
  const [rejected, setRejected] = useState("");
  const [unit, setUnit] = useState("pieces");
  const [order, setOrder] = useState("");
  const [part, setPart] = useState("");
  const [operation, setOperation] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [saving, setSaving] = useState(false);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setError(""); setRecords(null); setShifts(null);
    const options = { credentials: "include" as const, signal: controller.signal };
    void Promise.all([
      fetch("/api/planning/v2/shifts", options).then(async response => {
        if (!response.ok) throw Error(`Shifts unavailable (HTTP ${response.status})`);
        const data: { shifts: Shift[]; isSample: boolean } = await response.json();
        if (data.isSample || !Array.isArray(data.shifts)) throw Error("Invalid shift response");
        return data.shifts;
      }),
      fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/production?businessDate=${encodeURIComponent(businessDate)}`, options).then(async response => {
        if (!response.ok) throw Error(`Production unavailable (HTTP ${response.status})`);
        const data: { machineId: string; businessDate: string; records: Production[]; isSample: boolean; evidenceKind: string } = await response.json();
        if (data.isSample || data.evidenceKind !== "human-declared-production" ||
          data.machineId.toLowerCase() !== machineId.toLowerCase() || data.businessDate !== businessDate ||
          !Array.isArray(data.records)) throw Error("Production response scope mismatch");
        return data.records;
      })
    ]).then(([loadedShifts, loadedRecords]) => {
      if (controller.signal.aborted) return;
      setShifts(loadedShifts); setRecords(loadedRecords);
    }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, machineId, businessDate, retry]);

  function selectShift(id: string) {
    setShiftId(id); setNotice(""); setError("");
    const existing = records?.find(r => r.shiftId.toLowerCase() === id.toLowerCase());
    setGood(existing ? String(existing.goodQuantity) : "");
    setRejected(existing ? String(existing.rejectedQuantity) : "");
    setUnit(existing?.quantityUnit ?? "pieces");
    setOrder(existing?.productionOrder ?? "");
    setPart(existing?.partNumber ?? "");
    setOperation(existing?.operation ?? "");
    setReason("");
  }
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(""); setNotice("");
    const validQuantity = (value: string) => /^\d+(?:\.\d{1,3})?$/.test(value.trim()) && Number.isFinite(Number(value)) && Number(value) <= 999999999999999;
    if (!shiftId || !validQuantity(good) || !validQuantity(rejected) || !unit.trim() || !reason.trim()) {
      setError("Choose a shift and enter nonnegative good/rejected quantities (up to 3 decimals), unit and revision reason.");
      return;
    }
    setSaving(true);
    try {
      const response = await fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/production`, {
        method: "POST", credentials: "include", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ shiftId, businessDate, goodQuantity: Number(good), rejectedQuantity: Number(rejected),
          quantityUnit: unit.trim(), productionOrder: order.trim() || null, partNumber: part.trim() || null,
          operation: operation.trim() || null, revisionReason: reason.trim() })
      });
      if (!response.ok) {
        const payload: { error?: string } = await response.json().catch(() => ({}));
        throw Error(payload.error ?? `Production save failed (HTTP ${response.status}). Check permissions and migration 028.`);
      }
      const saved: { revision: number } = await response.json();
      setNotice(`Production revision ${saved.revision} saved. Reloading latest values.`);
      setReason(""); setRetry(value => value + 1);
    } catch (e) { setError(String(e)); } finally { setSaving(false); }
  }
  const selected = records?.find(r => r.shiftId.toLowerCase() === shiftId.toLowerCase());
  const shiftName = (id: string) => shifts?.find(s => s.shiftId.toLowerCase() === id.toLowerCase())?.displayName ?? "Unknown shift";
  return <section aria-label="Supervisor production quantities">
    <h3>Actual production</h3>
    <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}>{open ? "Hide production entry" : "Open production entry"}</button>
    {open && <>
      <p>Record good and rejected quantities for {businessDate}. Corrections create a new revision; they never overwrite previous declarations.</p>
      {error && <p role="alert">{error} No sample fallback. <button type="button" onClick={() => setRetry(v => v + 1)}>Retry loading</button></p>}
      {(shifts === null || records === null) && !error && <p role="status">Loading configured shifts and production records…</p>}
      {shifts && records && <form onSubmit={save}>
        <fieldset disabled={saving}><legend>Record shift production</legend>
          <label>Shift <select required value={shiftId} onChange={e => selectShift(e.target.value)}>
            <option value="">Select configured shift</option>
            {shifts.map(s => <option key={s.shiftId} value={s.shiftId}>{s.displayName}</option>)}
          </select></label>
          {shiftId && <p>{selected ? `Existing revision ${selected.revision} loaded. Saving will create a correction.` : "No production record for this shift. Missing is not zero."}</p>}
          <label>Good quantity <input type="number" required min="0" step="0.001" value={good} onChange={e => setGood(e.target.value)}/></label>
          <label>Rejected quantity <input type="number" required min="0" step="0.001" value={rejected} onChange={e => setRejected(e.target.value)}/></label>
          <label>Unit <input required maxLength={50} value={unit} onChange={e => setUnit(e.target.value)}/></label>
          <label>Production order <input maxLength={100} value={order} onChange={e => setOrder(e.target.value)}/></label>
          <label>Part number <input maxLength={100} value={part} onChange={e => setPart(e.target.value)}/></label>
          <label>Operation <input maxLength={100} value={operation} onChange={e => setOperation(e.target.value)}/></label>
          <label>Entry / correction reason <input required maxLength={1000} value={reason} onChange={e => setReason(e.target.value)}/></label>
          <button type="submit" disabled={!shiftId}>{saving ? "Saving…" : selected ? "Save correction revision" : "Save production entry"}</button>
        </fieldset>
      </form>}
      {notice && <p role="status">{notice}</p>}
      {shifts && records && <section aria-label="Latest declared shift production">
        <h4>Latest saved production</h4>
        {records.length === 0 ? <p>No production has been recorded for this machine and date. This does not mean zero production.</p> :
          <table><thead><tr><th>Shift</th><th>Good</th><th>Rejected</th><th>Total declared</th><th>Revision</th><th>Action</th></tr></thead>
            <tbody>{records.map(record => <tr key={record.productionRevisionId}>
              <td>{shiftName(record.shiftId)}</td><td>{record.goodQuantity} {record.quantityUnit}</td>
              <td>{record.rejectedQuantity} {record.quantityUnit}</td>
              <td>{record.goodQuantity + record.rejectedQuantity} {record.quantityUnit}</td>
              <td>{record.revision} · {record.recordedAtUtc}</td>
              <td><button type="button" onClick={() => selectShift(record.shiftId)}>Revise</button></td>
            </tr>)}</tbody></table>}
        {shifts.filter(s => !records.some(r => r.shiftId.toLowerCase() === s.shiftId.toLowerCase())).length > 0 &&
          <p>Missing production: {shifts.filter(s => !records.some(r => r.shiftId.toLowerCase() === s.shiftId.toLowerCase())).map(s => s.displayName).join(", ")}.</p>}
      </section>}
      <p>Production quantities entered by a supervisor. Machine-based OEE is shown separately when available.</p>
    </>}
  </section>;
}
