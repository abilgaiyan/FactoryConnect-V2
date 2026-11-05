import { useState } from "react";
import type { PreviewShift } from "../application/supervisor-preview.ts";
import { productionDetailsAt, type ProductionDetailsEntry } from "../application/production-details-preview.ts";

export interface TemporaryShiftTarget {
  readonly machineId: string; readonly shiftId: string; readonly revision: number;
  readonly quantity: string; readonly unit: string; readonly note: string;
  readonly author: string; readonly recordedAt: string;
  readonly order: string; readonly part: string; readonly operation: string;
}
export default function ShiftTargetsPreview({ machineId, machineName, shift, actorId, entries, targets, onSave, onDirty }: {
  readonly machineId: string; readonly machineName: string; readonly shift: PreviewShift; readonly actorId: string;
  readonly entries: readonly ProductionDetailsEntry[]; readonly targets: readonly TemporaryShiftTarget[];
  readonly onSave: (targets: readonly TemporaryShiftTarget[]) => void; readonly onDirty: (dirty: boolean) => void;
}) {
  const [quantity, setQuantity] = useState("");
  const [unit, setUnit] = useState("pieces");
  const [note, setNote] = useState("");
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const history = targets.filter(target => target.machineId === machineId && target.shiftId === shift.id);
  const details = productionDetailsAt(entries, machineId, shift.start);
  const canEnter = actorId === "sample-reviewer";
  function save() {
    setMessage("");
    if (!canEnter) { setError("Target entry is reserved for the sample supervisor. Real authorization is not connected."); return; }
    if (!/^(?:0|[1-9]\d*)(?:\.\d+)?$/.test(quantity) || !Number.isFinite(Number(quantity))) {
      setError("Enter a nonnegative target quantity, including zero, without signs or exponent notation."); return;
    }
    if (!unit.trim()) { setError("Provide an explicit quantity unit."); return; }
    if (history.length > 0 && !note.trim()) { setError("Explain the target revision in the reason / note field."); return; }
    onSave([...targets, { machineId, shiftId: shift.id, revision: history.length + 1, quantity, unit: unit.trim(), note,
      author: actorId, recordedAt: new Date().toISOString(), order: details?.order ?? "", part: details?.part ?? "", operation: details?.operation ?? "" }]);
    setError(""); setQuantity(""); setNote(""); onDirty(false);
    setMessage("Temporary target revision saved in browser memory only; not approved or applicable.");
  }
  return <div className="supervisor-editor">
    <h2>Shift Targets · temporary planning preview</h2>
    <p>Whole-shift goals only. A revision replaces the intended goal; quantities are never added together. Entries disappear on reload.</p>
    {machineId === "all" ? <p>Select one machine to enter or inspect its shift targets.</p> : <>
      <p><strong>{machineName}</strong> · {shift.name} · Business date {shift.businessDate} · Asia/Kolkata<br />Occurrence {shift.id} · {shift.start} – {shift.end}</p>
      <p>Declared details at shift start — not verified against master data. Job changes within a shift may require separate compatible comparisons.</p>
      <dl><dt>Production order / job number</dt><dd>{details?.order || "Not provided"}</dd><dt>Part number</dt><dd>{details?.part || "Not provided"}</dd><dt>Operation</dt><dd>{details?.operation || "Not provided"}</dd></dl>
      {!canEnter && <p>Target entry is available to the sample supervisor persona only. This is illustrative permission behavior, not authenticated access.</p>}
      <fieldset disabled={!canEnter}><legend>Enter a whole-shift target</legend>
        <label>Target quantity<input inputMode="decimal" value={quantity} maxLength={30} onChange={e => { setQuantity(e.target.value); onDirty(true); }} /></label>
        <label>Quantity unit<input value={unit} maxLength={50} onChange={e => { setUnit(e.target.value); onDirty(true); }} /></label>
        <label>Reason / note<textarea value={note} maxLength={2000} onChange={e => { setNote(e.target.value); onDirty(true); }} /></label>
        <button type="button" onClick={save}>Save temporary target revision</button>
        <button type="button" onClick={() => { setQuantity(""); setUnit("pieces"); setNote(""); setError(""); onDirty(false); }}>Discard target input</button>
      </fieldset>
      {error && <p role="alert">{error}</p>}{message && <p role="status">{message}</p>}
      <h3>Target history · {history.length} revision(s)</h3>
      {!history.length && <p>Target not provided · TargetMissing</p>}
      {history.map((target, index) => <article className="supervisor-detail" key={target.revision}>
        <h4>{index === 0 ? "Original entered target" : "Revised whole-shift goal"} · Revision {target.revision}</h4>
        <p>{target.quantity} {target.unit} · Temporary draft — not approved or applicable</p>
        <p>Author {target.author} · Recorded {target.recordedAt}</p>
        <p>Declared order / part / operation at entry: {target.order || "Not provided"} / {target.part || "Not provided"} / {target.operation || "Not provided"}</p>
        <p>Reason / note: {target.note || "Not provided"}</p>
        {Number(target.quantity) === 0 && <p>Achievement unavailable — zero target · ZeroTarget</p>}
      </article>)}
      <h3>Actual achieved — read only</h3>
      <p>Actual not established · ActualNotEstablished</p>
      <p>Source: not connected · Qualification: not established · Evidence cutoff: not established · Unit compatibility: not established</p>
      <p>Variance: — · Achievement: —</p>
      <p>Comparison basis: none. No approved target revision is selected. Baseline target not established · BaselineNotEstablished. An original entered draft is not a sealed shift-start baseline.</p>
      <p>No actual quantity entry, target approval, applicability selection or baseline capture is implemented. Target achievement does not establish OEE Performance. Saving a target changes no machine evidence, quantity or OEE.</p>
    </>}
  </div>;
}
