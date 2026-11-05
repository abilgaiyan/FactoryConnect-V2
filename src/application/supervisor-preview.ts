// Screen-review fixtures and local interactions only. Not an authorization or persistence service.
export type PreviewStatus = "Draft" | "Submitted" | "Approved" | "Changes requested";
export type PreviewEvidenceState = "resolved" | "unavailable" | "conflict";
export interface PreviewActor { readonly id: string; readonly name: string; readonly reviewer: boolean }
export interface PreviewEvent {
  readonly id: string; readonly machineId: string; readonly machineName: string; readonly source: string;
  readonly streamKey: string; readonly processorId: string; readonly publicationRevision: string;
  readonly outputOrdinal: number; readonly position: string; readonly instanceId: string; readonly sequence: string;
  readonly state: string; readonly start: string; readonly end: string | null; readonly flags: readonly string[];
}
export interface PreviewShift { readonly id: string; readonly name: string; readonly businessDate: string; readonly start: string; readonly end: string }
export interface ExplanationContent { readonly category: string; readonly detail: string; readonly team: string; readonly note: string }
export interface PreviewRevision {
  readonly revision: string; readonly author: string; readonly changedAt: string;
  readonly content: ExplanationContent; readonly status: PreviewStatus;
  readonly submitter?: string; readonly submittedAt?: string;
  readonly reviewer?: string; readonly acceptedAt?: string; readonly decisionNote?: string;
}
export interface PreviewThread { readonly id: string; readonly eventId: string; readonly creator: string; readonly revisions: readonly PreviewRevision[] }
export type PreviewCommand =
  | { readonly kind: "create"; readonly id: string; readonly eventId: string }
  | { readonly kind: "save"; readonly threadId: string; readonly expectedRevision: string; readonly content: ExplanationContent }
  | { readonly kind: "submit"; readonly threadId: string; readonly expectedRevision: string }
  | { readonly kind: "approve"; readonly threadId: string; readonly expectedRevision: string; readonly note: string };
export const previewActors: readonly PreviewActor[] = [
  { id: "sample-operator", name: "Sample operator", reviewer: false },
  { id: "sample-reviewer", name: "Sample supervisor", reviewer: true },
];
export const previewShifts: readonly PreviewShift[] = [
  { id: "sample-s1", name: "Shift 1", businessDate: "2026-10-28", start: "2026-10-28T02:00:00Z", end: "2026-10-28T10:30:00Z" },
  { id: "sample-s2", name: "Shift 2", businessDate: "2026-10-28", start: "2026-10-28T10:30:00Z", end: "2026-10-28T18:30:00Z" },
  { id: "sample-s3", name: "Shift 3 · overnight", businessDate: "2026-10-28", start: "2026-10-28T18:30:00Z", end: "2026-10-29T02:00:00Z" },
];
function event(id: string, conventional: boolean, state: string, start: string, end: string | null, revision: string): PreviewEvent {
  return { id, machineId: conventional ? "22222222-2222-2222-2222-222222222222" : "11111111-1111-1111-1111-111111111111",
    machineName: conventional ? "Conventional machine · sample" : "CNC machine · sample",
    source: conventional ? "Sample MOXA status normalization" : "Sample CNC execution normalization",
    streamKey: conventional ? "sample:moxa" : "sample:cnc", processorId: "machine-state-activity",
    publicationRevision: revision, outputOrdinal: 0, position: "9007199254740993", instanceId: "7", sequence: "42",
    state, start, end, flags: ["Continuity not established; explanation cannot change this"] };
}
export const previewEvents: readonly PreviewEvent[] = [
  event("sample-event-1", false, "Stopped", "2026-10-28T05:00:00Z", "2026-10-28T05:15:00Z", "0"),
  event("sample-event-2", true, "Stopped", "2026-10-28T06:00:00Z", "2026-10-28T06:20:00Z", "9007199254740993"),
  event("sample-event-3", false, "Fault", "2026-10-28T08:00:00Z", "2026-10-28T08:10:00Z", "2"),
  event("sample-event-4", true, "Stopped", "2026-10-28T10:20:00Z", "2026-10-28T10:45:00Z", "3"),
  event("sample-event-5", false, "Running", "2026-10-28T09:00:00Z", null, "4"),
  event("sample-event-6", true, "Unknown", "2026-10-28T19:00:00Z", "2026-10-28T19:10:00Z", "5"),
];
export const emptyExplanation: ExplanationContent = { category: "Cause not established", detail: "", team: "Not established", note: "" };
export function initialPreviewThreads(): readonly PreviewThread[] {
  return [
    { id: "sample-thread-1", eventId: "sample-event-2", creator: "sample-operator", revisions: [{ revision: "1", author: "sample-operator", changedAt: "2026-10-28T06:30:00Z", status: "Draft", content: { category: "Tool change", detail: "Insert replacement", team: "Production", note: "Observed wear. Action taken: replaced insert and checked first component." } }] },
    { id: "sample-thread-2", eventId: "sample-event-3", creator: "sample-operator", revisions: [{ revision: "1", author: "sample-operator", submitter: "sample-operator", submittedAt: "2026-10-28T08:20:00Z", changedAt: "2026-10-28T08:15:00Z", status: "Submitted", content: { category: "Investigation", detail: "Alarm observed", team: "Maintenance", note: "Cause not established. Maintenance inspection requested." } }] },
    { id: "sample-thread-3", eventId: "sample-event-2", creator: "sample-reviewer", revisions: [{ revision: "1", author: "sample-reviewer", changedAt: "2026-10-28T06:35:00Z", status: "Draft", content: { ...emptyExplanation, note: "Independent observation awaiting clarification." } }] },
  ];
}
export function head(thread: PreviewThread): PreviewRevision { return thread.revisions[thread.revisions.length - 1]!; }
export function overlap(event: PreviewEvent, shift: PreviewShift): { start: string; end: string; minutes: number } | null {
  const start = Math.max(Date.parse(event.start), Date.parse(shift.start));
  const end = Math.min(Date.parse(event.end ?? shift.end), Date.parse(shift.end));
  return end > start ? { start: new Date(start).toISOString(), end: new Date(end).toISOString(), minutes: (end - start) / 60_000 } : null;
}
export function summary(events: readonly PreviewEvent[], threads: readonly PreviewThread[]) {
  const ids = new Set(events.filter(event => event.end !== null).map(event => event.id));
  const matching = threads.filter(thread => ids.has(thread.eventId));
  return { closedEvents: ids.size, withoutThreads: [...ids].filter(id => !matching.some(thread => thread.eventId === id)).length,
    threads: matching.length, draft: matching.filter(thread => head(thread).status === "Draft").length,
    submitted: matching.filter(thread => head(thread).status === "Submitted").length,
    approved: matching.filter(thread => head(thread).status === "Approved").length,
    changes: matching.filter(thread => head(thread).status === "Changes requested").length };
}
export function applyPreviewCommand(threads: readonly PreviewThread[], command: PreviewCommand, actor: PreviewActor,
  evidence: PreviewEvidenceState, now: string): { threads: readonly PreviewThread[]; error?: string } {
  const fail = (error: string) => ({ threads, error });
  if (command.kind === "create") {
    const selected = previewEvents.find(event => event.id === command.eventId);
    if (!selected || selected.end === null) return fail("Select an exact closed event. Open activity cannot receive an explanation.");
    if (evidence !== "resolved") return fail("Exact evidence is unavailable or conflicting. No thread was created.");
    if (threads.some(thread => thread.id === command.id)) return fail("Preview thread identity already exists.");
    return { threads: [...threads, { id: command.id, eventId: selected.id, creator: actor.id,
      revisions: [{ revision: "1", author: actor.id, changedAt: now, status: "Draft", content: { ...emptyExplanation } }] }] };
  }
  const thread = threads.find(item => item.id === command.threadId);
  if (!thread) return fail("Explanation thread unavailable.");
  const current = head(thread);
  if (current.revision !== command.expectedRevision) return fail("Revision conflict. Your input is retained; reopen the current revision before trying again.");
  let revisions: readonly PreviewRevision[];
  if (command.kind === "save") {
    if (thread.creator !== actor.id) return fail("Only this thread's creator can edit or submit it.");
    revisions = [...thread.revisions, { revision: (BigInt(current.revision) + 1n).toString(), author: actor.id,
      changedAt: now, status: "Draft", content: { ...command.content } }];
  } else if (command.kind === "submit") {
    if (thread.creator !== actor.id) return fail("Only this thread's creator can edit or submit it.");
    if (current.status !== "Draft") return fail("Only the current draft can be submitted.");
    if (evidence !== "resolved") return fail("Exact evidence must resolve without conflict before submission.");
    revisions = [...thread.revisions.slice(0, -1), { ...current, status: "Submitted", submitter: actor.id, submittedAt: now }];
  } else {
    if (!actor.reviewer) return fail("This preview persona has no review permission.");
    if (thread.revisions.some(revision => revision.author === actor.id) || current.submitter === actor.id)
      return fail("Self-approval is prohibited for anyone who authored this thread or submitted this revision.");
    if (current.status !== "Submitted") return fail("Only the current submitted revision can be reviewed.");
    if (evidence !== "resolved") return fail("Exact evidence must resolve without conflict before explanation acceptance.");
    revisions = [...thread.revisions.slice(0, -1), { ...current, status: "Approved", reviewer: actor.id, acceptedAt: now, decisionNote: command.note }];
  }
  return { threads: threads.map(item => item.id === thread.id ? { ...thread, revisions } : item) };
}
