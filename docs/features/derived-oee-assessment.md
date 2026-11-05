# Separate derived OEE assessment

## Read-only trace and boundary

Baseline: `09b8fbce53c43108907704a62f5f7ac3c7ceb3b3`.

Canonical Availability 1.0 divides ActualProductionTime by PlannedOperatingTime. Performance 1.0 divides IdealProductionDuration (production reference time) by ActualProductionTime. The coherent batch source reads components at one exact MetricAggregationCheckpoint; canonical projections retain processor/evaluation identity, source revision, component evidence and unavailable reasons. Projection persistence rounds calculated ratios to eight decimals, ToEven. Canonical OEE multiplies canonical dependencies and remains entirely unchanged here.

The Quality assessment has an independent admission/measurement/policy lineage. Matching a machine/period or a positive quantity does not prove that its population belongs to the A/P source cut. No existing factory coherence issuer is established by this implementation.

## Exact binding

`DerivedOeeEvidenceCut` requires an authority-owned SourceDependency decision reference/revision, typed reporting scope including exact interval/context, canonical projection processor, exact aggregation checkpoint, and exact Quality identity/content fingerprint. Its state is Established, Unproven or Conflicting. The contract validates structural consistency only. The caller must obtain the coherence decision from an authorized issuer; this layer never compares unrelated revision numbers or infers authority from matching scope.

`DerivedOeeMetricDependency` binds a complete canonical A/P snapshot to its reporting interval. Projection machine, period and context must match that scope. Only built-in A/P definition version 1.0, ratio units and component-backed evidence are accepted. Calculated operands must match canonical keys/dimensions/units, be nonnegative, have positive denominator and reproduce the rounded canonical ratio. Both A/P dependencies must have the exact processor and source checkpoint selected by the cut, and their shared ActualProductionTime evidence must be identical. Production-day interval boundaries come from the reporting/schedule authority; they are not inferred from a UTC date.

The target additionally binds the exact Quality identity and content fingerprint. Scope, interval, context, processor, cut or population-binding mismatches fail construction. A matching scope with an Unproven or Conflicting cut produces no numeric OEE.

## Calculation and provenance

Rule 1.0 multiplies the stored canonical Availability ratio, stored canonical Performance ratio and selected Quality ratio as decimals, without a further rounding stage. No zero/one/100% defaults are supplied. All unavailable/insufficient-evidence A/P statuses, reason codes and operand names, and the Quality reason are retained as structured failures. Coherence failure has its own reason and does not discard dependency failures. Numeric zero is a valid result only when every dependency is valid and numeric.

Performance can exceed one under its canonical nonnegative domain. It is not clamped. If the resulting OEE exceeds one, the derived assessment is unavailable with ProductOutOfRange, preserving the canonical OEE bounded domain.

Policy-assumed Quality carries its complete immutable approval object (policy ID/version, approval reference, approver, scope and effective boundaries) into the derived assessment. A numeric result using that assumption is Provisional. An unavailable result retains the assumed policy provenance but remains nonnumeric. Measured Quality does not mark the derived result provisional. No UI labels or activation are implemented in this step.

## Identity and storage

DOAI V1 uses strict UTF-8 length-prefixed strings, typed identities, little-endian numbers, explicit nullable values and invariant numeric encodings. It includes the QAI typed scope and Quality references, exact A/P processor/definition/revision identities, coherence decision reference and derivation-rule version. Its separate content fingerprint includes complete Quality content, cut classification and canonical component snapshots. Evidence order is normalized. A changed reference/rule creates a new identity; changed content under unchanged identities is an integrity failure. No creation timestamp participates.

Migration 017 adds only DerivedOeeAssessment and advances the schema descriptor/recognition set. Earlier migrations, Quality storage codec V1 and operational dependency codec V1 are unchanged. A new bounded derived payload embeds Quality payload and normalized canonical A/P snapshots using the existing codecs as-is. Decode reconstructs validated targets, re-evaluates rule 1.0 and verifies every byte of the canonical payload, including result, failure reasons and provisional flag.

The explicitly constructed SqlServerDerivedOeeAssessmentStore requires exact Quality to be durably published first, including its referenced policies. It never publishes or modifies Quality or canonical projections. Canonical snapshots are retained in the derived payload because canonical current projection rows are not a historical dependency-selection authority. Capture and cross-lineage coherence remain caller responsibilities.

Publication uses a transaction-owned exclusive identity application lock and a ReadCommitted transaction. Identical canonical identity/content replays without DML. Conflicts or corrupt rows fail without mutation. Reads validate row hashes, identity, decoded content, calculated result and exact durable Quality dependency. Missing exact targets return Absent; a missing comparable cut returns IncomparableEvidence. No historical fallback or mutable current pointer is introduced. Historical targets remain retrievable. SQL operational failures propagate rather than appearing as missing evidence.

Tests cover exact scope/interval/context/processor/source-cut binding, explicit coherence failure, all dependency failure reasons, numeric zero, measured versus assumed provenance, input integrity, changed revisions, deterministic replay/ordering/culture, storage codecs, SQL round-trip/restart, concurrent replay/conflict, missing/corrupt Quality dependencies and fresh/post-016 migration/rerun. API/UI, factory activation and actual factory coherence/approval issuance remain subsequent work.
