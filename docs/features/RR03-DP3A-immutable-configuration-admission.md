# RR03-DP.3A — Immutable resolved configuration and admission

Status: implementation proposed for review; local library/integration-test builds, 11 Core authority tests and 704 ordinary integration tests passed. CI and SQL acceptance pending. No factory authority has been imported or activated.

## Two independent immutable records

`AssumedBreakConfigurationSnapshot` captures resolved configuration evidence for one machine/site/production-date/shift: UTC boundaries, shift and schedule identities/versions, calendar version, policy identity/version/effective range, the 30-minute allowance and instruction source, classified exclusions and source references. This is the resolved shift configuration, not a replacement for the recurring schedule model. No machine Running or coverage facts are inferred from configuration.

`AssumedBreakConfigurationAdmission` binds the exact snapshot identity to machine/site, an effective production-date range, a defined admission mode, admitting-authority identity and instruction/source references. Persisting a snapshot is not admission. The record stores an explicit authority assertion; it does not authenticate a signer, infer approval from a hash or import factory approval automatically. Any future admission entry point must restrict issuers to its authorized configuration source. No such activation endpoint is included here.

Both identities hash their complete canonical V1 payloads using distinct prefixes. Binary encoding uses fixed field order, little-endian primitives, strict UTF-8 strings, UTC/duration ticks and day numbers. Source-reference sets and exclusion evidence have deterministic ordinal ordering; exact duplicate evidence is canonicalized once. Conflicting definitions sharing a source reference are invalid. Codec reads reject identity mismatch, noncanonical bytes, unknown versions/kinds, malformed payloads and payloads above 1 MiB.

A content change produces a new identity. Publication also requires an expected identity: attempting to reuse the previous identity with changed content fails without mutation. Historical records remain accessible by exact identity; no newest-row lookup exists.

## Exclusions and admission

The manifest distinguishes Missing, CompleteEmpty and CompleteIntervals. A Missing snapshot may be retained as configuration evidence but cannot be admitted and cannot establish zero exclusions. Complete manifests require source references; the empty state is an explicit completeness assertion.

All intervals must be UTC, positive and contained in the resolved shift. Overlaps are unioned rather than summed. ActualBreak evidence may be preserved in a snapshot, but admission rejects every ActualBreak interval in either admission mode, regardless of source identity, ordering or overlap with OtherExclusion intervals, while this duration-assumed default policy is active, avoiding a second deduction. A future actual-break precedence rule requires its own explicit contract.

Admission validates snapshot identity, scope and the snapshot production date against inclusive effective start / exclusive effective end. Source policy effective dates are independently validated in the snapshot. Competing applicable admissions for the same exact snapshot return Ambiguous. The selector never chooses one by row order, issue time or latest version.

## SQL publication and reads

Migration 019 adds separate snapshot and admission envelopes. The admission's snapshot hash has a foreign key and a lookup index. Existing SQL migrations and canonical metric projections are unchanged.

Publication uses a transaction-owned exclusive application lock per immutable identity. An admission additionally reads and validates its exact snapshot within the same transaction with HOLDLOCK. Identical publication replays; conflicting, corrupt, unsupported or inadmissible publication fails without rewriting records. Admission selection reads its snapshot, enumerates its indexed candidate set, and validates every candidate against that same snapshot on one connection and one serializable transaction. Exact admission/dependency reads also use one serializable transaction. Both fail closed on corrupt or unsupported records. A SQL barrier regression verifies that the snapshot remains protected while candidate selection is blocked.

Exact reads verify identity and payload hashes, versions, canonical decoded identity and admission dependency binding. Outcomes include Absent, Found, Corrupt, UnsupportedEncoding, IncomparableEvidence and Ambiguous. SQL availability errors and cancellation propagate. Read results do not establish CNC Running completeness.

## Validation and remaining boundaries

Tests cover canonical identity and round trips, changed content/admission identities, effective boundaries, UTC/source validation, missing/empty exclusions, interval union and break conflicts; SQL restart/history/exact selection, identical and conflicting concurrent publication, ambiguous admissions, dependency absence/corruption, unknown encodings, and fresh/post-018 migration/schema/rerun.

This slice supplies storage and exact resolution for admitted configuration evidence. It does not seed a Gajra snapshot/admission, register a producer, resolve Running provenance, advance a producer checkpoint, change the reporting API/Dashboard, recover revision 534 or deploy to the factory. PR #110 remains the producer-prerequisite trace. DP.3B and DP.3C follow separate review.
