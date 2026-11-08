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
