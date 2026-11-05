import { useState } from "react";
import { addProductionDetails, emptyProductionDetails, productionDetailsAt,
  type ProductionDetailsEntry, type ProductionDetailsInput } from "../application/production-details-preview.ts";

export default function ProductionDetailsPreview({ machineId, machineName, actorId, at, entries, onSave, onDirty }: {
  readonly machineId: string; readonly machineName: string; readonly actorId: string; readonly at: string;
  readonly entries: readonly ProductionDetailsEntry[];
  readonly onSave: (entries: readonly ProductionDetailsEntry[]) => void;
  readonly onDirty: (dirty: boolean) => void;
}) {
  const [input, setInput] = useState<ProductionDetailsInput>({ ...emptyProductionDetails });
  const [acknowledge, setAcknowledge] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  const selected = machineId !== "all";
  const current = productionDetailsAt(entries, machineId, at);
  const history = entries.filter(entry => entry.machineId === machineId);
  function change(key: keyof ProductionDetailsInput, value: string) {
    setInput(previous => ({ ...previous, [key]: value })); setAcknowledge(false);
    setSaved(false); onDirty(true);
  }
  function discard() { setInput({ ...emptyProductionDetails }); setAcknowledge(false); setError(""); onDirty(false); }
  function save() {
    const result = addProductionDetails(entries, input, selected ? machineId : "", actorId, new Date().toISOString(), acknowledge);
    setError(result.error ?? "");
    if (result.error) return;
    onSave(result.entries); discard(); setSaved(true);
  }
  const display = (value: string | undefined) => value || "Not provided";
  return <section className="supervisor-editor" aria-labelledby="production-details-title">
    <h2 id="production-details-title">Production Details</h2>
    <p><strong>User-declared production details — not verified against master data.</strong></p>
    <p>Temporary entries disappear on reload or leaving this page. Both sample personas may enter details; real authentication and machine permissions are not connected.</p>
    {!selected && <p>Select one machine in Machine scope to enter or inspect production details.</p>}
    {selected && <>
      <article className="supervisor-detail"><h3>Declared context at selected shift start</h3>
        <p>Comparison instant: {at}. Details continue across shifts until the next declared job change.</p>
        <dl><dt>Production order / job number</dt><dd>{display(current?.order)}</dd><dt>Part number</dt><dd>{display(current?.part)}</dd><dt>Operation number / name</dt><dd>{display(current?.operation)}</dd></dl>
        {!current && <p>No declared context established at this instant.</p>}
      </article>
      <fieldset><legend>New production details declaration</legend>
        <label>Machine<input readOnly value={machineName} /></label>
        <label>Production order / job number<input maxLength={120} value={input.order} onChange={e => change("order", e.target.value)} /></label>
        <label>Part number<input maxLength={120} value={input.part} onChange={e => change("part", e.target.value)} /></label>
        <label>Operation number / name<input maxLength={200} value={input.operation} onChange={e => change("operation", e.target.value)} /></label>
        <label>Effective from · date and time<input type="datetime-local" value={input.localStart} onChange={e => change("localStart", e.target.value)} /></label>
        <label>Timezone · fixed UTC offset<input placeholder="+05:30" maxLength={6} value={input.offset} onChange={e => change("offset", e.target.value)} /></label>
        <p>+05:30 is India Standard Time. The offset is explicit and independent of your browser timezone. Order, part and operation may be left blank and display as “Not provided.”</p>
        <label>Production note (optional)<textarea rows={3} maxLength={2000} value={input.note} onChange={e => change("note", e.target.value)} /></label>
      </fieldset>
      <label className="production-backdate"><input type="checkbox" checked={acknowledge} onChange={e => setAcknowledge(e.target.checked)} />I acknowledge a past declared start; historical evidence and attribution remain unchanged.</label>
      <p>Each new entry explicitly ends the preceding declared context at its effective start. Equal or earlier starts are blocked in this preview; no overlap or historical correction is silently applied.</p>
      <div className="supervisor-actions"><button type="button" onClick={save}>Save temporary production details</button><button type="button" onClick={discard}>Discard production input</button></div>
      {error && <p role="alert">{error}</p>}{saved && <p role="status">Production details saved in browser memory only.</p>}
      <h3>Declaration history · {history.length} entries</h3>
      {history.length === 0 && <p>No temporary entries for this machine.</p>}
      {history.map((entry, index) => <article className="supervisor-detail" key={entry.id}>
        <h4>{entry.id} · User-declared</h4><dl>
          <dt>Order / part / operation</dt><dd>{display(entry.order)} / {display(entry.part)} / {display(entry.operation)}</dd>
          <dt>Author / recorded time (preview browser clock)</dt><dd>{entry.author} / {entry.recordedAt}</dd>
          <dt>Declared effective time / timezone</dt><dd>{entry.localStart} UTC{entry.offset} · {entry.effectiveAt}</dd>
          <dt>Declared context ends</dt><dd>{history[index + 1]?.effectiveAt ?? "Until next declared job change"}</dd>
          <dt>Note</dt><dd>{display(entry.note)}</dd>
        </dl>
      </article>)}
    </>}
    <p>Context only: no assignment approval, qualified counter quantity, target change or OEE recalculation. Earlier entries and their original recorded details are retained.</p>
  </section>;
}
