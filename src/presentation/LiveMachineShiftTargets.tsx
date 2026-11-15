import { useEffect, useState, type FormEvent } from "react";

interface Target {
  shiftId: string; targetRevisionId: string; revision: number; targetQuantity: number;
  quantityUnit: string; productionOrder: string | null; partNumber: string | null;
  operation: string | null; revisionReason: string; recordedAtUtc: string;
}
export default function LiveMachineShiftTargets({ machineId, businessDate }: {
  readonly machineId: string; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [targets, setTargets] = useState<Target[] | null>(null);
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  const [selectedShift, setSelectedShift] = useState("");
  const [history, setHistory] = useState<Target[] | null>(null);
  const [historyError, setHistoryError] = useState("");
  useEffect(() => {
    if (!open || !selectedShift) { setHistory(null); return; }
    const controller = new AbortController();
    setHistory(null); setHistoryError("");
    const query = new URLSearchParams({ businessDate, shiftId: selectedShift });
    void fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/targets/history?${query}`,
      { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Revision history unavailable (HTTP ${response.status})`);
        const data: { revisions: Target[]; machineId: string; shiftId: string; businessDate: string; isSample: boolean } = await response.json();
        if (data.isSample || data.machineId.toLowerCase() !== machineId.toLowerCase()
            || data.shiftId.toLowerCase() !== selectedShift.toLowerCase()
            || data.businessDate !== businessDate || !Array.isArray(data.revisions))
          throw new Error("Revision history scope mismatch");
        setHistory(data.revisions);
      }).catch(e => { if (!controller.signal.aborted) setHistoryError(String(e)); });
    return () => controller.abort();
  }, [open, selectedShift, machineId, businessDate, retry]);

  const [shifts, setShifts] = useState<{ shiftId: string; displayName: string }[] | null>(null);
  const [writeShift, setWriteShift] = useState("");
  const shiftName = (id: string) => shifts?.find(shift => shift.shiftId.toLowerCase() === id.toLowerCase())?.displayName ?? "Unknown shift";
  const [quantity, setQuantity] = useState("");
  const [unit, setUnit] = useState("pieces");
  const [order, setOrder] = useState("");
  const [part, setPart] = useState("");
  const [operation, setOperation] = useState("");
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState("");
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setShifts(null);
    void fetch("/api/planning/v2/shifts", { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Configured shifts unavailable (HTTP ${response.status})`);
        const data: { shifts: { shiftId: string; displayName: string }[]; isSample: boolean } = await response.json();
        if (data.isSample || !Array.isArray(data.shifts)) throw new Error("Invalid configured shifts response");
        setShifts(data.shifts);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, retry]);
  function prepareRevision(target: Target) {
    setWriteShift(target.shiftId);
    setQuantity(String(target.targetQuantity));
    setUnit(target.quantityUnit);
    setOrder(target.productionOrder ?? "");
    setPart(target.partNumber ?? "");
    setOperation(target.operation ?? "");
    setReason("");
    setNotice("Target fields copied from revision " + target.revision + ". Enter a new reason before saving a new revision.");
  }
  async function saveTarget(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(""); setNotice("");
    const targetQuantity = Number(quantity);
    if (!writeShift || !Number.isFinite(targetQuantity) || targetQuantity < 0
        || !/^\d+(?:\.\d{1,3})?$/.test(quantity.trim())
        || !unit.trim() || !reason.trim()) {
      setError("Choose a configured shift, nonnegative quantity (up to 3 decimals), unit and revision reason.");
      return;
    }
    setSaving(true);
    try {
      const response = await fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/targets`, {
        method: "POST", credentials: "include", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ shiftId: writeShift, businessDate, targetQuantity,
          quantityUnit: unit.trim(), productionOrder: order.trim() || null,
          partNumber: part.trim() || null, operation: operation.trim() || null,
          revisionReason: reason.trim() })
      });
      if (!response.ok) {
        const payload: { error?: string } = await response.json().catch(() => ({}));
        throw new Error(payload.error ?? `Target save failed (HTTP ${response.status}). Check site permissions.`);
      }
      setNotice("New target revision saved to SQL."); setReason(""); setRetry(value => value + 1);
    } catch (e) { setError(String(e)); } finally { setSaving(false); }
  }

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setTargets(null); setError("");
    void fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/targets?businessDate=${encodeURIComponent(businessDate)}`,
      { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Machine shift targets unavailable (HTTP ${response.status})`);
        const data: { targets: Target[]; isSample: boolean; machineId: string; businessDate: string } = await response.json();
        if (data.isSample || data.machineId.toLowerCase() !== machineId.toLowerCase()
            || data.businessDate !== businessDate || !Array.isArray(data.targets))
          throw new Error("Target response does not match selected machine and date");
        setTargets(data.targets);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, machineId, businessDate, retry]);
  return <section aria-label="Live machine shift targets">
    <h3>Machine shift targets · SQL planning</h3>
    <button type="button" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      {open ? "Hide targets" : "Load machine shift targets"}
    </button>

    {open && <form onSubmit={saveTarget}>
      <fieldset disabled={saving}><legend>Record machine shift target revision</legend>
        <p>Machine {machineId} · business date {businessDate}</p>
        <label>Configured shift <select required value={writeShift} onChange={e => setWriteShift(e.target.value)}>
          <option value="">Select shift</option>
          {shifts?.map(shift => <option key={shift.shiftId} value={shift.shiftId}>{shift.displayName}</option>)}
        </select></label>
        <label>Target quantity <input type="number" min="0" step="0.001" required value={quantity} onChange={e => setQuantity(e.target.value)} /></label>
        <label>Unit <input required maxLength={50} value={unit} onChange={e => setUnit(e.target.value)} /></label>
        <label>Production order <input maxLength={100} value={order} onChange={e => setOrder(e.target.value)} /></label>
        <label>Part number <input maxLength={100} value={part} onChange={e => setPart(e.target.value)} /></label>
        <label>Operation <input maxLength={100} value={operation} onChange={e => setOperation(e.target.value)} /></label>
        <label>Revision reason <input required maxLength={1000} value={reason} onChange={e => setReason(e.target.value)} /></label>
        <button type="submit">{saving ? "Saving…" : "Save target revision"}</button>
      </fieldset>
    </form>}
    {notice && <p role="status">{notice}</p>}
    {open && (error ? <><p role="alert">{error}. No sample fallback.</p>
      <button type="button" onClick={() => setRetry(value => value + 1)}>Retry targets</button></>
      : targets === null ? <p role="status">Loading recorded targets…</p>
      : targets.length === 0 ? <p>No targets recorded for this machine and business date. This is not a zero target.</p>
      : <><label>Inspect target revision <select value={selectedShift} onChange={e => setSelectedShift(e.target.value)}><option value="">Select a recorded shift</option>{targets.map(t => <option key={t.shiftId} value={t.shiftId}>{shiftName(t.shiftId)} · revision {t.revision}</option>)}</select></label>
      {selectedShift && (() => { const target = targets.find(t => t.shiftId === selectedShift); return target ? <p>Recorded at {target.recordedAtUtc} · revision {target.revision} · target {target.targetQuantity} {target.quantityUnit}. Revision reason: {target.revisionReason}</p> : null; })()}

      {selectedShift && <section aria-label="Target revision history">
        <h4>Target revision history</h4>
        {historyError ? <p role="alert">{historyError}</p> : history === null
          ? <p role="status">Loading revisions…</p> : history.length === 0
          ? <p>No recorded revisions.</p>
          : <table><thead><tr><th>Revision</th><th>Target</th><th>Reason</th><th>Recorded UTC</th></tr></thead>
            <tbody>{history.map(item => <tr key={item.targetRevisionId}>
              <td>{item.revision}</td><td>{item.targetQuantity} {item.quantityUnit}</td>
              <td>{item.revisionReason}</td><td>{item.recordedAtUtc}</td>
            </tr>)}</tbody></table>}
      </section>}
      <table><thead><tr><th>Shift</th><th>Target</th><th>Job / part / operation</th><th>Revision</th><th>Reason</th><th>Action</th></tr></thead>
        <tbody>{targets.map(target => <tr key={target.targetRevisionId}>
          <td>{shiftName(target.shiftId)}</td><td>{target.targetQuantity} {target.quantityUnit}</td>
          <td>{[target.productionOrder, target.partNumber, target.operation].filter(Boolean).join(" · ") || "Not specified"}</td>
          <td>{target.revision}</td><td>{target.revisionReason}</td><td><button type="button" onClick={() => prepareRevision(target)}>Revise target</button></td>
        </tr>)}</tbody></table></>)}
    <p>Targets are administrative plans, not measured production or OEE.</p>
  </section>;
}
