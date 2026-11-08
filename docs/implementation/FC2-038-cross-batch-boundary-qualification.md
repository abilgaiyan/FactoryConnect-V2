# FC2-038 — Retained cross-batch boundary qualification

Base: merged PR #36, `965bb4d9`. This increment qualifies a bounded, read-only
candidate proof. It does not enable sustained activity publication across batches.
CI qualification and review disposition are tracked in the associated PR.

## Evidence contract

Sequence adjacency alone establishes no continuity. The existing immutable ingestion
request records the exact expected predecessor batch and source instance. Its request
hash and canonical request bytes bind operation, site, machine, acknowledgement,
stream, requested from-sequence and original document hash. No ingestion schema or
ingestion authorization behavior needs changing to inspect this evidence.

The internal `AssessRetainedBoundaryAsync` primitive acquires security Shared,
machine Shared and processor Shared gates in existing order. It resolves current
approved mapping authority after waits and checks both observation timestamps against
its half-open interpretation interval. The read-only serializable transaction checks
the durable source cursor and retrieves exactly two batch documents, immutable
ingestion receipts and endpoint projections by indexed identity. Each document is
at most 1 MiB, receipt 16 KiB, and endpoint payload explicitly bounded. Work does not
walk an acquisition chain or historical prefix. No new SQL index or migration is added.

Each receipt must have the batch's runtime actor and exact machine scope. Every
document and request hash is verified. The MTConnect parser verifies exact device,
headers, original timestamp, value, quality and canonical observation hash. Caller
endpoints must match retained SQL projections, not just genuine-looking source IDs.

A candidate retained link requires the successor receipt to name exactly the preceding
batch and instance, unchanged runtime/acknowledgement and mapping, exact nonoverlapping
header coverage, the predecessor's last signal observation and successor's first signal
observation, positively ordered timestamps and qualified state values. Signal sequence
numbers may be nonadjacent because other data items share the source sequence space.
Unknown/unavailable endpoints, gaps, overlaps, omitted boundary observations or
unrelated ancestry remain explicit breaks. Missing or altered evidence fails closed.

The returned proof pins exact endpoints, current qualification and both immutable
request hashes. Its profile is `accepted-receipt-boundary-candidate-v1-not-physical-completeness`.
It is an internal candidate snapshot, not a token or permission to publish. It does
not claim physical sensor completeness, agent truthfulness, or continuity through
arbitrary intervening/quiet batches. The pair read does not establish a full processor
source cut or checkpoint ancestry; existing production processing retains those checks.

## Acceptance and limits

Pure acceptance covers exact direct ancestry without signal sequence adjacency,
unrelated/bootstrap predecessors, instance/runtime changes, request/hash/document
conflicts, malformed receipts, endpoint/mapping conflicts, gaps, overlaps, omitted
successor observations and unavailable states. Existing same-batch policy remains
unchanged even when a candidate retained link is proved.

Disposable-SQL acceptance uses actual HTTP/mTLS-ingested observations in the real
activity-host fixture, repeated at deep history. It verifies a positive direct link,
altered caller input, wrong mapping, a checksum-consistent forged receipt predecessor,
malformed retained request and missing endpoint projection. Fixture corruption is
restored in finally blocks; the original evidence must qualify again. Checkpoint JSON,
period counts and activity receipt counts remain unchanged throughout assessment.

## Remaining release boundary

Publication still uses `retained-batch-continuity-v1`; cross-batch links remain breaks.
Enabling a stronger policy requires a separately versioned qualification and request
fingerprint, exact source-cut/predecessor binding, proof revalidation in the publishing
writer's transaction after waits, persisted proof ancestry and actual-process restart/
replay acceptance. An out-of-transaction candidate must never set
`ContinuityFromPrevious` directly. Existing checkpoint identities cannot be silently
reinterpreted under a new policy. Quiet windows continue their conservative anchor
break; no extrapolation or source-instance recovery is introduced.

Production commissioning, customer setup, quantities and shift/OEE inputs remain
separate. This increment performs no production action or UI/API enablement.
