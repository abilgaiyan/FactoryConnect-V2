import { useCallback, useMemo, useState } from "react";
import { applyPreviewCommand, head, initialPreviewThreads, overlap, previewActors, previewEvents, previewShifts, summary,
  type ExplanationContent, type PreviewCommand, type PreviewEvidenceState, type PreviewThread } from "../application/supervisor-preview.ts";
import "./supervisor-preview.css";
import ShiftTargetsPreview, { type TemporaryShiftTarget } from "./ShiftTargetsPreview.tsx";
import ProductionDetailsPreview from "./ProductionDetailsPreview.tsx";
import type { ProductionDetailsEntry } from "../application/production-details-preview.ts";

type View = "Shift Targets" | "Production Details" | "Shift start" | "Event explanations" | "Shift summary" | "Reviewer queue";
const views: readonly View[] = ["Shift start", "Event explanations", "Shift summary", "Reviewer queue"];
const format = (value: string) => new Intl.DateTimeFormat("en-IN", { timeZone: "Asia/Kolkata", day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date(value));
const statusLabel = (value: string) => value === "Approved" ? "Explanation accepted" : value;

export default function SupervisorDeskPreview() {
  const [view, setView] = useState<View>("Shift start");
  const [shiftId, setShiftId] = useState("sample-s1");
  const [machine, setMachine] = useState("all");
  const [actorId, setActorId] = useState("sample-operator");
  const [evidence, setEvidence] = useState<PreviewEvidenceState>("resolved");
  const [threads, setThreads] = useState(initialPreviewThreads);
  const [productionEntries, setProductionEntries] = useState<readonly ProductionDetailsEntry[]>([]);
  const [targets, setTargets] = useState<readonly TemporaryShiftTarget[]>([]);
  const [eventId, setEventId] = useState("sample-event-1");
  const [threadId, setThreadId] = useState("");
  const [dirty, setDirty] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const onDirty = useCallback((value: boolean) => setDirty(value), []);
  const shift = previewShifts.find(item => item.id === shiftId)!;
  const actor = previewActors.find(item => item.id === actorId)!;
  const events = useMemo(() => previewEvents.filter(event => (machine === "all" || event.machineId === machine) && overlap(event, shift)), [machine, shift]);
  const counts = summary(events, threads);
  const event = events.find(item => item.id === eventId);
  const eventThreads = threads.filter(thread => thread.eventId === event?.id);
  const selectedThread = eventThreads.find(thread => thread.id === threadId);
  const machines = [...new Map(previewEvents.map(item => [item.machineId, item.machineName])).entries()];
  function selectEvent(id: string) { setEventId(id); setThreadId(""); setError(""); setMessage(""); setView("Event explanations"); }
  function command(action: PreviewCommand): boolean {
    const result = applyPreviewCommand(threads, action, actor, evidence, new Date().toISOString());
    setError(result.error ?? "");
    if (result.error) return false;
    setThreads(result.threads); setDirty(false);
    setMessage("Preview updated in browser memory only. Nothing was sent to the server.");
    return true;
  }
  function create() {
    if (!event) return;
    const id = `preview-thread-${threads.length + 1}`;
    if (command({ kind: "create", id, eventId: event.id })) setThreadId(id);
  }
  return <section className="supervisor-preview" aria-labelledby="supervisor-title">
    <div className="supervisor-heading"><div><p className="supervisor-eyebrow">SUPERVISOR DESK · SCREEN REVIEW</p><h1 id="supervisor-title">Explain the event. Preserve the evidence.</h1></div><span className="supervisor-badge">Sample data only</span></div>
    <p className="supervisor-preview-notice"><strong>Development preview.</strong> All machines, shifts, identities, permissions and results below are illustrative. Changes live only in this page and disappear when you leave or reload. No authentication, server writes, approvals or production data are connected.</p>
    <fieldset className="supervisor-controls" disabled={dirty}><legend>Sample review context</legend>
      <label>Shift occurrence<select value={shiftId} onChange={e => { setShiftId(e.target.value); setEventId(""); setThreadId(""); }}>
        {previewShifts.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
      <label>Machine scope<select value={machine} onChange={e => { setMachine(e.target.value); setEventId(""); setThreadId(""); }}><option value="all">Both sample machines</option>{machines.map(([id, name]) => <option key={id} value={id}>{name}</option>)}</select></label>
      <label>Preview persona<select value={actorId} onChange={e => { setActorId(e.target.value); setError(""); }}>{previewActors.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
      <label>Simulate evidence read<select value={evidence} onChange={e => setEvidence(e.target.value as PreviewEvidenceState)}><option value="resolved">Resolved sample</option><option value="unavailable">Unavailable</option><option value="conflict">Integrity conflict</option></select></label>
    </fieldset>
    <p className="supervisor-scope">Business date <strong>{shift.businessDate}</strong> · {format(shift.start)} – {format(shift.end)} · Asia/Kolkata<br />Sample occurrence: {shift.id}. Machine access does not assign operator responsibility. These are not the commissioned factory calendars.</p>
    <nav className="supervisor-views" aria-label="Supervisor views">{views.map(item => <button type="button" key={item} aria-current={view === item ? "page" : undefined} disabled={dirty} onClick={() => { setView(item); setMessage(""); setError(""); }}>{item}</button>)}<button type="button" disabled={dirty} aria-current={view === "Production Details" ? "page" : undefined} onClick={() => setView("Production Details")}>Production Details</button><button type="button" disabled={dirty} aria-current={view === "Shift Targets" ? "page" : undefined} onClick={() => setView("Shift Targets")}>Shift Targets</button></nav>
    {dirty && <p role="status">Unsaved preview input. Save or discard your preview input before switching event, machine, persona, view or shift.</p>}
    {error && <p role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    {view === "Shift Targets" && <ShiftTargetsPreview key={`${machine}:${actorId}:${shiftId}`} machineId={machine} machineName={machines.find(([id]) => id === machine)?.[1] ?? ""} shift={shift} actorId={actorId} entries={productionEntries} targets={targets} onSave={setTargets} onDirty={onDirty} />}
    {view === "Production Details" && <ProductionDetailsPreview key={`${machine}:${actorId}`} machineId={machine} machineName={machines.find(([id]) => id === machine)?.[1] ?? ""} actorId={actorId} at={shift.start} entries={productionEntries} onSave={setProductionEntries} onDirty={onDirty} />}
    {view === "Shift start" && <>
      <h2>Start with the recorded picture</h2><p>Inspect current activity and closed events. No shift-start declaration or general handover note is created here.</p>
      <div className="supervisor-cards">{machines.filter(([id]) => machine === "all" || machine === id).map(([id, name]) => <article key={id}><h3>{name}</h3><p>Sample access scope</p><p>{previewEvents.find(item => item.machineId === id)?.source}</p><small>Source identity stays visible for both machine types.</small></article>)}</div>
      <h3>Current activity — read only</h3><p>Open activity cannot receive an explanation in this slice.</p>
      {events.filter(item => item.end === null).map(item => <p key={item.id}>{item.machineName}: {item.state} since {format(item.start)} · Open period · Explanation unavailable</p>)}
      {!events.some(item => item.end === null) && <p>No open activity fixture in this selected occurrence. This is not proof of zero activity.</p>}
      <button type="button" onClick={() => setView("Event explanations")}>Inspect closed events</button>
    </>}
    {view === "Event explanations" && <>
      <h2>Recorded events</h2><p>No explanation policy is configured. “No explanation thread” is descriptive, not a user obligation. Running periods do not require routine justification.</p>
      <div className="supervisor-scroll" role="region" aria-label="Sample recorded events" tabIndex={0}><table><caption>Closed original events with the selected shift overlap</caption><thead><tr><th>Machine / source</th><th>Original event</th><th>Shift overlap</th><th>Explanation threads</th><th>Open</th></tr></thead><tbody>
        {events.filter(item => item.end !== null).map(item => { const window = overlap(item, shift)!; const related = threads.filter(thread => thread.eventId === item.id); return <tr key={item.id}>
          <td>{item.machineName}<small>{item.source}</small></td><td>{item.state}<br />{format(item.start)} – {format(item.end!)}<br />{(Date.parse(item.end!) - Date.parse(item.start)) / 60_000} minutes original</td><td>{format(window.start)} – {format(window.end)}<br />{window.minutes} minutes overlap only</td><td>{related.length === 0 ? "No explanation thread" : `${related.length} independent thread(s)`}</td><td><button type="button" disabled={dirty} onClick={() => selectEvent(item.id)}>Inspect event {item.id.slice(-1)}</button></td>
        </tr>; })}
      </tbody></table></div>
      {!events.some(item => item.end !== null) && <p>No closed sample events in this occurrence.</p>}
      {event && <div className="supervisor-detail">
        <h2>Original machine evidence — read only</h2><p><strong>{event.machineName} · {event.state}</strong></p><p>{format(event.start)} – {event.end ? format(event.end) : "Open"} · Original bounds retained</p>
        <p>Evidence read: <strong>{evidence}</strong>. {evidence !== "resolved" && "Showing retained sample evidence, not a fresh resolved result. Creation, submission and acceptance are blocked."}</p>
        <p>Flags / continuity: {event.flags.join(" · ")}</p>
        <details><summary>Exact sample source reference</summary><dl><dt>Authority / kind / reference version</dt><dd>machine-activity-history / closed-activity-period / 1</dd><dt>Machine / stream / processor</dt><dd>{event.machineId}<br />{event.streamKey}<br />{event.processorId}</dd><dt>Original publication revision / output ordinal</dt><dd>{event.publicationRevision} / {event.outputOrdinal}</dd><dt>Position / instance / sequence</dt><dd>{event.position} / {event.instanceId} / {event.sequence}</dd><dt>Original timestamps</dt><dd>{event.start} → {event.end ?? "Open"}</dd></dl></details>
        <h3>Independent explanation threads</h3><div className="supervisor-thread-list">{eventThreads.map(thread => <button type="button" disabled={dirty} key={thread.id} aria-pressed={threadId === thread.id} onClick={() => { setThreadId(thread.id); setError(""); setMessage(""); }}>{thread.id} · {previewActors.find(item => item.id === thread.creator)?.name} · {statusLabel(head(thread).status)}</button>)}</div>
        <button type="button" disabled={dirty || evidence !== "resolved" || event.end === null} onClick={create}>Create independent preview thread</button>
        {selectedThread && <PreviewEditor key={`${selectedThread.id}:${head(selectedThread).revision}:${head(selectedThread).status}`} thread={selectedThread} actorId={actorId} onDirty={onDirty} apply={command} evidence={evidence} />}
      </div>}
    </>}
    {view === "Shift summary" && <>
      <h2>Events and explanations are separate counts</h2><div className="supervisor-cards"><article><h3>Recorded closed events</h3><strong>{counts.closedEvents}</strong><p>{counts.withoutThreads} with no explanation thread</p></article><article><h3>Explanation threads</h3><strong>{counts.threads}</strong><p>{counts.draft} Draft · {counts.submitted} Submitted · {counts.approved} Explanation accepted · {counts.changes} Changes requested</p></article></div>
      <p>Multiple threads may refer to one event. One accepted thread does not approve other threads or establish that the machine evidence is correct.</p><p>This view is not shift closure. Targets, actual-quantity declarations, achievement and OEE recalculation are not connected to this preview.</p>
    </>}
    {view === "Reviewer queue" && <>
      <h2>Review the explanation, not the machine result</h2><p>Preview persona: {actor.name}. Real authenticated, machine-scoped review is not implemented.</p>
      {threads.filter(thread => head(thread).status === "Submitted" && events.some(item => item.id === thread.eventId)).map(thread => <article className="supervisor-review-item" key={thread.id}><h3>{previewEvents.find(item => item.id === thread.eventId)?.machineName} · {thread.id}</h3><p>Submitted annotation revision {head(thread).revision} · Submitted: {head(thread).submittedAt ?? "Not recorded"} · Original event {thread.eventId}</p><p>{head(thread).content.category}: {head(thread).content.note}</p><button type="button" onClick={() => { selectEvent(thread.eventId); setThreadId(thread.id); }}>Inspect evidence and revision</button>{" "}<button type="button" disabled={!actor.reviewer || evidence !== "resolved" || thread.revisions.some(revision => revision.author === actor.id) || head(thread).submitter === actor.id} onClick={() => command({ kind: "approve", threadId: thread.id, expectedRevision: head(thread).revision, note: "Sample review decision only" })}>Simulate explanation acceptance</button><p><small>Changes-requested interaction awaits the unresolved self-review policy and backend contract. No production decision is available.</small></p></article>)}
      {!threads.some(thread => head(thread).status === "Submitted" && events.some(item => item.id === thread.eventId)) && <p>No submitted sample revisions in this scope.</p>}
    </>}
  </section>;
}

function PreviewEditor({ thread, actorId, onDirty, apply, evidence }: { readonly thread: PreviewThread; readonly actorId: string; readonly evidence: PreviewEvidenceState; readonly onDirty: (value: boolean) => void; readonly apply: (command: PreviewCommand) => boolean }) {
  const current = head(thread);
  const [content, setContent] = useState<ExplanationContent>({ ...current.content });
  const editable = thread.creator === actorId;
  function change(key: keyof ExplanationContent, value: string) { setContent(previous => ({ ...previous, [key]: value })); onDirty(true); }
  return <div className="supervisor-editor"><h3>Explanation · annotation revision {current.revision}</h3><p>{statusLabel(current.status)} · Creator: {thread.creator}. Publication revision belongs to the machine event and is a separate value.</p>
    {!editable && <p>Creator-only editing and submission. This preview persona can inspect history but cannot rewrite this thread.</p>}
    <fieldset disabled={!editable}><legend>Describe the event</legend>
      <label>Reason category<select value={content.category} onChange={e => change("category", e.target.value)}>{["Cause not established", "Tool change", "Setup", "Material shortage", "Maintenance", "Inspection", "Investigation", "Other"].map(value => <option key={value}>{value}</option>)}</select></label>
      <label>Observed details<input value={content.detail} onChange={e => change("detail", e.target.value)} maxLength={500} /></label>
      <label>Team involved / follow-up team<select value={content.team} onChange={e => change("team", e.target.value)}>{["Not established", "Production", "Maintenance", "Quality", "Stores"].map(value => <option key={value}>{value}</option>)}</select></label>
      <label>Note (optional) · include action taken here<textarea rows={4} value={content.note} maxLength={2000} onChange={e => change("note", e.target.value)} /></label>
      <p>“Cause not established” and “No action yet” are valid observations. Selecting a team does not establish fault or responsibility.</p>
      <div className="supervisor-actions"><button type="button" onClick={() => apply({ kind: "save", threadId: thread.id, expectedRevision: current.revision, content })}>Save preview revision</button><button type="button" onClick={() => { setContent({ ...current.content }); onDirty(false); }}>Discard unsaved input</button><button type="button" onClick={() => apply({ kind: "save", threadId: thread.id, expectedRevision: "0", content })}>Simulate stale revision</button></div>
    </fieldset>
    <button type="button" disabled={!editable || current.status !== "Draft" || evidence !== "resolved" || JSON.stringify(content) !== JSON.stringify(current.content)} onClick={() => apply({ kind: "submit", threadId: thread.id, expectedRevision: current.revision })}>Simulate submission of saved draft</button>
    <details><summary>Revision history · {thread.revisions.length} revision(s)</summary>{thread.revisions.map(revision => <article key={revision.revision}><h4>Revision {revision.revision} · {statusLabel(revision.status)}</h4><p>Author: {revision.author} · Revision saved (preview): {revision.changedAt}</p><p>{revision.content.category} · {revision.content.detail} · {revision.content.team}</p><p>{revision.content.note}</p>{revision.submitter && <p>Submitted (preview): {revision.submittedAt ?? "Not recorded"} · Submitter: {revision.submitter}</p>}{revision.reviewer && <p>Explanation accepted (preview): {revision.acceptedAt ?? "Not recorded"} · Reviewer: {revision.reviewer} · {revision.decisionNote}</p>}</article>)}</details>
    <p><small>Browser memory is not durable history. Transaction ordering, operation IDs, identity eligibility and permission enforcement require the separately reviewed backend implementation.</small></p>
  </div>;
}
