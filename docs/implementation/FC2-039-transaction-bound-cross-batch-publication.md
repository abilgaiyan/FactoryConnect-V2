# FC2-039 — Transaction-bound retained cross-batch publication

Baseline: FC2-038 merged at `cdbc0b3a`. Draft implementation and acceptance work;
none of the gates below is closed merely by opening this PR.

## Required contract

An explicitly selected, versioned cross-batch profile must have a distinct
qualification and request fingerprint. Existing retained-batch-continuity-v1
checkpoints and operations retain their exact semantics; no implicit upgrade,
historical reinterpretation or automatic reset is permitted.

After security, machine and processor gates, the publishing transaction revalidates
current mapping eligibility, the full bounded source cut, exact checkpoint predecessor,
original documents, immutable ingestion receipts and endpoint projections. A PR #38
assessment snapshot is never accepted as authorization. Output, proof ancestry,
source frontier, checkpoint and immutable operation receipt commit atomically.

Exact replay returns the immutable original result without replacing later progress;
changed policy, proof or request cannot reuse an operation ID. Missing/conflicting
provenance, gaps, overlaps, changed mapping/instance and revoked eligibility fail closed.
Quiet cuts remain bounded and generate no fabricated samples or periods. No-state
advancement conservatively breaks the activity anchor.

## Acceptance gates

- Actual accepted direct cross-batch state transition publishes under the new policy.
- 101+ verified quiet batches advance bounded proof-bearing cuts with no invented time.
- Missing/altered receipts, documents and endpoint projections leave all effects unchanged.
- Mapping/instance changes and legacy-checkpoint adoption reject without silent upgrade.
- Current eligibility is checked after observed lock waits, including expiry/revocation.
- Whole processes die before commit, after commit before response, during replay and
  after a later batch; restart/replay has no duplicate or silently bridged intervals.
- An independent eligible machine progresses while another machine's processor is held.
- Existing full SQL, worker, history, backend and dashboard regression stays green.

Evidence and measured bounds will be recorded against the actual executable head.
PR remains draft until these gates and final review pass. Production commissioning,
customer provisioning, UI expansion and OEE/quantity work are outside this PR.

Retained-source ancestry is not physical sensor completeness. Publication must
retain that assurance limit and never extrapolate an open interval to wall-clock now.

## Code review findings — 8 November 2026 (implementation gate OPEN)

The draft branch at `388047a1` contains only this contract. The current
`SqlActivityProcessorStore.ApplyAcceptedAsync` still calls
`AcceptedActivityContinuityQualifier.Qualify`, which pins the
`retained-batch-continuity-v1` identity; `ApplyCoreAsync` still gates
first-sample continuation on identical batch/document. No transaction-bound
cross-batch policy is implemented by this branch.

These invariants are **blocking**, not optional implementation guidance:

1. **Never route a new-policy cut through the legacy signature as an implicit
   selector.** Make the profile explicit at work-discovery, operation-ID
   derivation, request serialization, qualification, checkpoint reading and
   publishing. Keep historical v2 nonempty requests and v3 no-state requests
   replayable, byte-for-byte, on the legacy path. Reject new-profile adoption of
   an existing legacy checkpoint *before* publication, even when generation is
   zero or there are no closed periods.
2. **No snapshot-authorized publication.** The PR #38
   `AssessRetainedBoundaryAsync` takes independent shared locks and returns a
   candidate snapshot; it cannot be called as the source of an authorization
   boolean. The publishing transaction must reassemble immutable receipt and
   original-document evidence after the security shared → machine shared →
   processor exclusive gates and source/checkpoint locks. The same transaction
   must verify the exact predecessor and retained-cut frontier.
3. **Proof continuity is not adjacency.** For every claimed boundary, bind
   predecessor batch and instance, runtime identity, acknowledgment, mapping,
   original MTConnect headers, terminal/predecessor and initial/successor
   exact-address observations, source timestamps and verified endpoint
   projections. Verify intervening no-state/frontier witnesses rather than
   assuming that arbitrarily separated batches form a direct link.
4. **No weakly-bound operation receipt.** A new versioned request must bind
   exact policy, qualification, complete selected cut, evidence fingerprints,
   prior checkpoint effect, verified boundary ancestry and resulting frontier.
   Preserve immutable receipt replay behavior, but do not permit stale
   preflight evidence to authorize a changed commit.
5. **The existing anchor-provenance invariant must be extended consciously.**
   `VerifyPredecessorAsync` currently rejects an open anchor located in a
   different batch from `LastObservation`. A new proof must explain that
   ancestry, or reject, rather than relaxing the guard globally. The no-state
   branch currently clears the anchor and must remain conservative unless a
   separately proved retention policy explicitly changes it.
6. **No legacy operation collision.** The worker's deterministic operation ID
   must include the new profile (and exact source/request identity) so an
   operation cannot be interpreted under two policies. Validate same-ID
   different-request failures and explicit legacy-checkpoint rejection.

### Required executable evidence before merge

- At least one **real** direct cross-batch state transition publishes a period;
  an unrelated predecessor, missing receipt, checksum-consistent rebind,
  altered document, projection, header, mapping, actor or source instance
  leaves checkpoint, operation and periods unchanged.
- An independent machine proceeds while a second machine is blocked; expiry,
  revocation and source mutation across a lock wait fail closed.
- At least 101 verified quiet batches advance with bounded SQL work and
  **without** fabricated transition/period or silent anchor continuation.
- Exact replay, changed-request reuse, whole-child-process pre-commit death,
  committed-but-unacknowledged replay, later-batch recovery and restart must
  be exercised under the *new profile*, not inferred from legacy coverage.
- Full SQL regression, worker/history regressions, backend/dashboard CI and
  measured SQL envelopes must pass on the final head. Do not mark ready or
  merge before evidence is attached.

### Scope statement

The implementation is not complete in this review. Production commissioning,
physical sensor completeness, quantity/OEE processing and UI work remain
out of scope. A documentation-only change cannot qualify cross-batch
publication.
