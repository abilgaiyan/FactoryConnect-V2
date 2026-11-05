import assert from "node:assert/strict";
import test from "node:test";
import { applyPreviewCommand, head, initialPreviewThreads, overlap, previewActors, previewEvents, previewShifts, summary } from "../src/application/supervisor-preview.ts";
const now = "2026-10-29T02:00:00Z";
const operator = previewActors[0];
const reviewer = previewActors[1];
test("independent threads do not inflate event counts; publication revisions remain exact strings", () => {
  const original = initialPreviewThreads();
  const result = applyPreviewCommand(original, { kind: "create", id: "new-thread", eventId: "sample-event-2" }, operator, "resolved", now);
  assert.equal(result.error, undefined);
  assert.equal(result.threads.filter(t => t.eventId === "sample-event-2").length, 3);
  const counts = summary(previewEvents.filter(e => overlap(e, previewShifts[0])), result.threads);
  assert.equal(counts.closedEvents, 4);
  assert.equal(counts.threads, 4);
  assert.equal(counts.withoutThreads, 2);
  assert.equal(previewEvents[0].publicationRevision, "0");
  assert.equal(previewEvents[1].publicationRevision, "9007199254740993");
});
test("cross-shift overlaps preserve one original event and overnight occurrence includes next-date evidence", () => {
  const event = previewEvents[3];
  assert.equal(overlap(event, previewShifts[0]).minutes, 10);
  assert.equal(overlap(event, previewShifts[1]).minutes, 15);
  assert.equal(event.start, "2026-10-28T10:20:00Z");
  assert.equal(event.end, "2026-10-28T10:45:00Z");
  assert.equal(overlap(previewEvents[5], previewShifts[2]).minutes, 10);
  assert.equal(previewShifts[2].businessDate, "2026-10-28");
});
test("open or unavailable evidence cannot create an explanation", () => {
  const threads = initialPreviewThreads();
  for (const [eventId, evidence] of [["sample-event-5", "resolved"], ["sample-event-1", "unavailable"], ["sample-event-1", "conflict"]]) {
    const result = applyPreviewCommand(threads, { kind: "create", id: "blocked", eventId }, operator, evidence, now);
    assert.ok(result.error);
    assert.equal(result.threads, threads);
  }
});
test("creator-only edits and expected revision checks reject without modifying the current state", () => {
  const threads = initialPreviewThreads();
  const edit = { kind: "save", threadId: "sample-thread-1", expectedRevision: "1", content: head(threads[0]).content };
  assert.match(applyPreviewCommand(threads, edit, reviewer, "resolved", now).error, /creator/);
  assert.match(applyPreviewCommand(threads, { ...edit, expectedRevision: "0" }, operator, "resolved", now).error, /Revision conflict/);
  assert.equal(head(threads[0]).revision, "1");
});
test("self-approval is blocked even when the actor has a reviewer persona", () => {
  const threads = initialPreviewThreads();
  const actor = { ...operator, reviewer: true };
  assert.match(applyPreviewCommand(threads, { kind: "approve", threadId: "sample-thread-2", expectedRevision: "1", note: "self" }, actor, "resolved", now).error, /Self-approval/);
});
test("approved explanation revision stays historical; successor has no inherited approval or changed evidence", () => {
  const beforeEvidence = JSON.stringify(previewEvents);
  let result = applyPreviewCommand(initialPreviewThreads(), { kind: "approve", threadId: "sample-thread-2", expectedRevision: "1", note: "reviewed" }, reviewer, "resolved", now);
  assert.equal(head(result.threads[1]).status, "Approved");
  result = applyPreviewCommand(result.threads, { kind: "save", threadId: "sample-thread-2", expectedRevision: "1", content: { ...head(result.threads[1]).content, note: "Clarified observation" } }, operator, "unavailable", now);
  assert.equal(result.threads[1].revisions[0].status, "Approved");
  assert.equal(head(result.threads[1]).status, "Draft");
  assert.equal(head(result.threads[1]).reviewer, undefined);
  assert.equal(result.threads[1].eventId, "sample-event-3");
  assert.equal(JSON.stringify(previewEvents), beforeEvidence);
});
test("submission and acceptance require resolved evidence", () => {
  const threads = initialPreviewThreads();
  assert.match(applyPreviewCommand(threads, { kind: "submit", threadId: "sample-thread-1", expectedRevision: "1" }, operator, "unavailable", now).error, /resolve/);
  assert.match(applyPreviewCommand(threads, { kind: "approve", threadId: "sample-thread-2", expectedRevision: "1", note: "review" }, reviewer, "conflict", now).error, /resolve/);
});

test("optional note submits; saved, submitted and accepted times stay revision-specific", () => {
  const savedAt = "2026-10-29T02:00:00Z";
  const submittedAt = "2026-10-29T02:05:00Z";
  const acceptedAt = "2026-10-29T02:10:00Z";
  let result = applyPreviewCommand(initialPreviewThreads(), { kind: "create", id: "optional-note", eventId: "sample-event-1" }, operator, "resolved", savedAt);
  result = applyPreviewCommand(result.threads, { kind: "submit", threadId: "optional-note", expectedRevision: "1" }, operator, "resolved", submittedAt);
  assert.equal(result.error, undefined);
  let revision = head(result.threads.find(t => t.id === "optional-note"));
  assert.equal(revision.content.note, "");
  assert.equal(revision.changedAt, savedAt);
  assert.equal(revision.submittedAt, submittedAt);
  assert.equal(revision.acceptedAt, undefined);
  result = applyPreviewCommand(result.threads, { kind: "approve", threadId: "optional-note", expectedRevision: "1", note: "sample decision" }, reviewer, "resolved", acceptedAt);
  assert.equal(result.error, undefined);
  revision = head(result.threads.find(t => t.id === "optional-note"));
  assert.equal(revision.changedAt, savedAt);
  assert.equal(revision.submittedAt, submittedAt);
  assert.equal(revision.acceptedAt, acceptedAt);
  result = applyPreviewCommand(result.threads, { kind: "save", threadId: "optional-note", expectedRevision: "1", content: revision.content }, operator, "unavailable", "2026-10-29T02:15:00Z");
  const thread = result.threads.find(t => t.id === "optional-note");
  assert.equal(thread.revisions[0].acceptedAt, acceptedAt);
  assert.equal(head(thread).submittedAt, undefined);
  assert.equal(head(thread).acceptedAt, undefined);
  assert.equal(head(thread).status, "Draft");
});
