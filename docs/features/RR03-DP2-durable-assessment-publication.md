# RR03-DP.2 — Immutable SQL assessment publication

Status: implemented for review; SQL/CI acceptance pending.

Migration 018 adds only `dbo.AssumedBreakAssessment`; canonical metric projections and their processors are unchanged. Migration descriptors and repository-owned schema recognition advance to Post018. Earlier migration SQL and checksums are unchanged.

`IAssumedBreakAssessmentStore` publishes the frozen V1 target and payload. The SHA-256 target identity is the primary key. Identity bytes, identity encoding version, payload encoding version, SHA-256 content hash and canonical payload bytes are retained. The SQL size limits match the V1 codec's 1 MiB bound.

Publication takes a transaction-owned exclusive application lock per target hash, validates any existing envelope and decoded identity, then either inserts or accepts byte-identical replay. Different valid payload content under the same target raises `InvalidDataException` without mutation. Corrupt and unsupported existing rows also reject publication. Disposal rolls back failed transactions.

Reads select only the requested identity hash. They verify both hashes, both codec versions, decoded target identity, canonical payload and exact target bytes. No newest-row or scope-only fallback exists. Outcomes are `Absent`, `Found`, `Corrupt`, `UnsupportedEncoding` and `IncomparableEvidence` (missing/invalid exact target). Invalid identity syntax is a caller argument error. Cancellation and SQL availability failures propagate rather than becoming evidence outcomes.

This store preserves the bound aggregation checkpoint but does not itself establish that its source revision, schedule, policy or Running reference is authoritative. Authoritative producer admission is RR03-DP.3 work; no producer, service registration, reporting API or Dashboard is wired by this slice. Forecast and completed targets have distinct V1 identities. Missing Running remains null; evidence-backed zero stays zero.

Acceptance coverage includes fresh and post-017 migration/rerun/schema comparison; restart round trips; missing versus zero Running; forecast/completed identity separation; changed-policy/schedule/revision absence without fallback; identical and conflicting concurrent publication; unchanged immutable winner; corrupt hash/identity/payload; unknown versions; and valid-hash payload substitution.

Revision 534 recovery, report freshness and restored-Gajra end-to-end acceptance remain separate release gates. This change is not factory deployment authorization.
