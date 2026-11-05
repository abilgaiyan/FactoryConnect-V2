# Provisional Quality assessment — first implementation boundary

This is a pure, disconnected assessment boundary. It neither establishes external business authority nor activates a policy. Canonical quantity facts, operational projections and existing V1 SQL codecs are unchanged. SQL persistence, OEE derivation, HTTP contracts, UI labels and commissioning activation are subsequent work.

## Inputs and invariants

`QualityAssessmentScope` binds a typed shift occurrence or production day, machine, site, exact positive half-open UTC interval and typed context. Shift boundaries must match the occurrence. Production-day boundaries are supplied by the schedule authority; this layer does not infer them from UTC midnight.

`QualityProductionAdmission` is an authority-owned decision reference, admission-rule version, exact scope, population reference and source/dependency revisions. `CompleteAdmitted` requires produced > 0; `EstablishedZero` requires produced = 0. Both require a quantity-population reference. Incomplete, unproven and conflicting decisions expose no admitted population or quantity. The caller must establish continuity, lifecycle, completeness and attribution before supplying admission. Contract construction validates structural consistency, not that the caller has that authority.

`QualityMeasurementCoverage` carries its authority-owned coverage decision and revision for the exact assessed scope. Established absence must be explicitly supplied; an empty read is insufficient. Exact measurement requires nonnegative quantities, good + rejected = produced, and a measurement-set reference. The assessment target additionally requires that measured population and produced total match admitted production exactly. Partial, conflicting or indeterminate decisions contain no complete measured quantities and block the assumption.

`ApprovedAllGoodQualityPolicy` has an immutable policy ID/version, site, explicit unique machines, approval reference, approver and half-open UTC effective boundaries. Local approval dates must be resolved by commissioning before construction. No real factory approvals or defaults are installed.

The target snapshots evidence, machine and policy collections. It binds one exact authority-selected cut and one assessment-rule version. Revisions are opaque; no numerical or lexical comparison establishes authority or freshness.

## Decision order

| Production | Measurement | Applicable policy | Quality |
| --- | --- | --- | --- |
| Incomplete / unproven / conflicting | Any structurally consistent decision | Any | Unavailable with production reason |
| Established zero | Any structurally consistent decision | Any | Unavailable: zero production |
| Complete positive | Exact authoritative | Any | Measured good / produced |
| Complete positive | Partial / conflicting / indeterminate | Any | Unavailable with measurement reason |
| Complete positive | Established absent | Exactly one whole-period approval | PolicyAssumed, good = produced, rejects = 0, ratio = 1 |
| Complete positive | Established absent | Missing / outside scope / ambiguous | Unavailable with policy reason |

A policy must contain the entire period. A partial overlap is outside scope. Multiple matching approvals intersecting the period are ambiguous, even if only one covers the entire period. Approvals merely adjacent to the period do not overlap. Exact measurement does not need an applicable policy. Invalid contradictory inputs fail construction rather than receiving a calculated result.

## Deterministic identity and integrity

`QualityAssessmentIdentity` uses independent versioned QAI V1 encoding: strict UTF-8 length-prefixed strings, little-endian integers, explicit nullable markers, tagged scope and evidence types, UTC ticks and ordinal-sorted evidence/policy/machine collections. Decimal payloads use invariant G29 text so equal numeric quantities with different decimal scales have equal content fingerprints. There is no timestamp or culture-dependent object serialization. Existing operational-metric codec V1 is not reused or modified.

The assessment ID hashes scope, assessment-rule version, admission decision/rule/population and dependency references, measurement coverage/population/evidence references, and policy ID/version candidates. No policy is encoded as an explicit zero-length collection; nullable references and context identities carry presence markers. Period variants and evidence kinds carry distinct tags.

The separate content fingerprint additionally binds admission/measurement classifications and quantities, plus full approval payloads. Changed references or policy versions create new identities. Altered payload under unchanged immutable references retains the identity but changes the content fingerprint: this is an integrity failure, not a replacement. Authority owners must version their changed decisions. The assessor supports rule 1.0 only; unknown assessment rules are rejected.

## Exact-target selection

`QualityAssessmentSelector` checks conflicting content under any repeated identity, then selects only the exact requested target. Missing assessments are unavailable; an absent comparable authority-selected target returns unavailable/incomparable. A conflicting target payload fails integrity validation even when only one stored candidate exists. Old assessments are never used as fallback. Duplicate identical replays are accepted. The selector does not discover the current cut or compare opaque revisions; an upstream authority must supply that cut. Historical targets remain independently selectable.

Future SQL publication must enforce the same immutable identity/content rule atomically. Future presentation will render assumed Quality as “100% assumed—rejects pending”; only subsequently derived calculable OEE will be marked “Provisional.” This layer supplies neither production admission nor missing cycle-time/schedule evidence.

## SQL persistence — migration 016

Read-only baseline trace: `d3a71b2c14a49c98f8d4f79e52ededb2c1cd57cb`. The existing coverage store demonstrates transaction-owned identity application locks, immutable replay checks and canonical decode validation. The migration catalog discovers checksummed embedded scripts and the runtime compares repository-owned schema metadata against its current descriptor. Those patterns are retained without changing earlier scripts or operational-metric codecs.

Migration 016 adds only `QualityPolicy` and `QualityAssessment`; each stores identity hash, identity encoding version and exact bytes, payload version, full-payload hash and bounded payload bytes. `QualityStorageV1Codec` is independent of existing V1 codecs. Assessment payloads embed the complete target and the evaluated result, including applied policy. Decoding reconstructs validated contracts, re-evaluates the supported rule, verifies result identity/fingerprint/quantities, rejects trailing or noncanonical data, and re-encodes exactly. Unknown encoding/rule versions are unsupported, not absent.

`SqlServerQualityAssessmentStore` is explicitly constructed with a connection string and implements `IQualityAssessmentStore` and `IQualityPolicyStore`. It is not registered in Edge or API composition and activates nothing. Policy publication is also available independently. Assessment publication materializes all referenced policy versions in the same SQL transaction if absent. Existing conflicting policies prevent all writes.

All writers acquire transaction-owned exclusive application locks in ascending policy-hash order, then the assessment identity lock. Transactions use ReadCommitted: exact-key application locks protect missing as well as existing slots, avoiding Serializable empty-index range-lock conflicts between unrelated identities. Primary keys enforce identity-hash uniqueness. Full canonical identity and payload bytes are compared, not hashes alone. Identical replay performs no DML; changed content or a hash/identity collision throws InvalidDataException and rolls back. Unsupported stored encodings throw NotSupportedException. Cancellation before publication is observed before SQL access; failures before commit roll back, while uncertain acknowledgement after commit is resolved by exact read/replay.

Reads use committed immutable records. Policies and their referencing assessment become visible atomically, and the store never updates or deletes either. Reads validate the row envelope, decoded identity/content and every referenced durable policy. Separate policy rows are cross-validated against embedded approvals; a missing or corrupted policy makes the assessment corrupt. No mutable head, creation timestamp, inferred authority or latest-result selection is introduced. SQL operational failures propagate rather than masquerading as absent evidence.

Read-by-identity returns Found, Absent, Corrupt or UnsupportedEncoding. Exact-target reads additionally support IncomparableEvidence for a null comparable cut and enforce the pure selector's content-integrity rule. A changed exact target returns Absent until its own immutable assessment is published; earlier assessments remain independently retrievable. An existing identity with conflicting target content throws InvalidDataException. A found assessment may itself have unavailable Quality; storage presence does not imply a calculated metric.

Tests cover codec and SQL round trips, upgrade from post-015 and fresh schema equivalence, migration rerun, independent policy publication, changed production/policy/measurement targets, historical retrieval, restart/replay, concurrent identical/conflicting writes, transactional rollback, lost acknowledgement and corruption/misbound payload/missing-policy detection. Factory approvals, source-admission authority, derived OEE, API/UI and activation remain outside this step.
