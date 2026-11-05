# RR03-DP.3B.2B.1 — Durable acquisition evidence publication

Status: draft implementation; SQL CI and publication/recovery review required before merge.
Baseline: DP.3B.2A frozen contract. Based on main cc45be45.

## Publication authority

The existing SQL observation-ingestion transaction remains the only writer. Migration 020 adds immutable AcquisitionEvidencePublication records and a stream-scoped AcquisitionEvidenceHead. Observations, checkpoint, contact authority, publication and head advancement commit together. The checkpoint update lock serializes competing writers, including initial stream creation. No new worker or second acquisition writer is introduced.

Each explicit response publication retains a UUID created once in the pending acquisition. Its deterministic SHA-256 identity binds that UUID to machine and ordinal stream identity. Reusing it with changed payload is a conflict. V1 strict UTF-8 binary payloads bind expected/result checkpoints, UTC contact ticks, Agent instance, requested cursor, first/last buffer sequences, ordered recovery records and canonical observation contents. Hashes also bind publication ordinal, predecessor and accepted raw-position boundaries. Filtered observations need not have consecutive Agent sequences; explicit batches assign durable positions in sequence order.

A UUID is a publication identity, not a proof of acquisition continuity or an admission policy. First/last header values describe the Agent buffer; the requested cursor and returned next cursor describe this response window.

## Recovery ordering

The existing recovery policy retains each loss before invoking its logging reporter. The runtime carries those records across failed acquisition cycles, then freezes them with the first successful response and its original contact metadata. A failed durable write retries the same pending response, UUID, recovery list and timestamp.

Recovery events are embedded immutable members of the publication, not independently writable rows. Each has an exact identity derived from publication identity and event ordinal. Their canonical contents are covered by the publication hash. Exact event retrieval requires both parent publication identity and event identity; it never searches a current/latest row. A closed cut at publication N includes every event in publications 1..N, in event order, followed by the response batch in each publication. The future manifest must bind this whole prefix, not a selectable subset of events.

The predecessor foreign key and head foreign key protect the chain from ordinary deletion. Exact reads validate all prefix ordinals, links, hashes, codecs, stream identities, checkpoint relationships and accepted-position boundaries. Missing or invalid prefix content fails closed. After publication integrity validation, the same serializable transaction independently reconciles each referenced MachineObservation by stream, Agent instance and sequence, compares canonical contents and timestamp, bounds its durable position by the publication cut, and validates complete membership of newly accepted positions. Explicit response publications also enforce their deterministic sequence-to-position assignment. Missing or altered source rows make exact reads Corrupt and prevent replay/publication from extending that evidence. The publication reader is closed before source queries, so SQL MARS is not required. A successful read is not a continuity assessment.

Losses before a successful publication remain pending, not authoritative. A process crash before commit leaves no durable event or response; the old checkpoint remains the restart boundary. This slice does not certify uncommitted acquisition attempts or silence. A later continuity evaluator must treat absent/unknown opening authority conservatively.

## Replay and historical reads

Explicit evidence publication supports lost-acknowledgement replay after later batches: identical content resolves to the original identity without changing current checkpoint or contact authority. Changed timestamp, cursor, observations, recovery members or other payload under that identity is rejected. A different UUID cannot replay a stale expected checkpoint. New successful contact or checkpoint publication requires its own identity.

Legacy ingestion calls without response metadata retain existing current-state replay semantics. They are recorded as opaque publications with absent response evidence, never upgraded into MTConnect acquisition proof. Existing pre-migration observations are not backfilled as verified acquisition windows; a first record may have an already-populated AcceptedBefore boundary. That history remains unestablished. Exact reads expose an OpeningBoundary inherited from publication 1: UnverifiedLegacyPrefix when its AcceptedBefore or expected checkpoint was already populated, retaining that legacy raw-position boundary and checkpoint. Otherwise NoPriorDurablePrefix describes only the absence of earlier durable SQL state; it does not assert acquisition completeness, session continuity or coverage of the requested time interval. These values are derived from the hash-validated immutable opening record, never current/latest state. The migration-019-to-020 conformance fixture seeds legacy observations before upgrading, publishes contact-only evidence and verifies this boundary after restart and later publication.

Exact publication reads return Absent, Found, Corrupt or UnsupportedEncoding. Exact recovery reads inherit parent validation. There is no newest-row fallback. The mutable head is only a publication/CAS boundary and cannot substitute for a requested historical identity.

## Conformance

- Canonical round-trip and strict-version/malformed-payload tests.
- Nonconsecutive filtered observations within one cursor window.
- Mixed replay/new batches retain prior raw positions and assign only the new suffix, for legacy ingestion and an explicitly recorded recovery cursor rewind. Normal explicit responses still reject observations before their requested cursor.
- Publication identity retained when changed content must conflict.
- Explicit recovery chain from prior checkpoint through OUT_OF_RANGE and instance transition to accepted response.
- SQL rollback injected at checkpoint, observation, contact, evidence and head insert stages.
- Historical replay after newer acquisition and reader restart.
- Changed timestamp/cursor/payload/UUID rejected without mutation.
- Contact-only publication preserves accepted raw boundary without implying coverage.
- Concurrent identical replay and competing checkpoint publication.
- Missing predecessor, corrupt hash and unsupported encoding.
- Deleted/changed predecessor observation rows, sequence/instance/content/timestamp and swapped durable positions.
- Explicit legacy opening boundary after a seeded pre-020 upgrade, restart and later publication.
- Runtime tests retain recovery records and original pending evidence across write retries.

SQL fixtures require FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING. SQL CI execution is a merge gate; local ordinary tests alone do not close this implementation slice.

## Explicit exclusions

No continuity assessment, Established result, Running full-period zero authority, metric aggregation change, Running reader integration, assumed-break publication, DP.3C producer, Availability calculation, Dashboard/API activation, revision 534 recovery or factory deployment.
