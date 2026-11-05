# GG-003 revision 534 — containment contract proposal

Status: **Stage A evidence/evaluation implementation, opt-in only; pending CI and contract review.** Stage B is held. No production composition, schema migration, database replay, recovery or deployment is enabled. Domain validation remains intact. This change does not unblock production revision 534.

## Decision

The evaluator cannot establish incompatible eligibility from the current snapshot. Availability 1.0 sees 2683.614157 / 2452.013355 at revision 534 for SHIFT-1 and production day 2026-10-16. The source facts establish out-of-plan running, but that evidence does not reach evaluation. Do not infer its cause from a ratio above one, timestamp spans, GG-003 identity, revision number, or the sample planned-production configuration.

A test changes running eligibility flags and assignment IDs while preserving the aggregate inputs. Both shift and production-day aggregate evidence remain identical. This establishes the information gap directly, rather than relying on a numeric failure to infer scope.

## Traced contracts

| Boundary | Existing contract | Consequence |
|---|---|---|
| Durable source facts | DurableMetricInputFact carries FactId, position through PositionedMetricInputFact, interval boundaries, eligibility flag, planned assignment, contextualized activity and eligibility source IDs | Required evidence exists upstream |
| Historical snapshot | SQL and in-memory ReadAtRevisionAsync select contributions through exact source revision and reaggregate | Revision lineage is available, but per-fact eligibility is discarded |
| OperationalMetricComponent | OperandName, aggregate source identity, dimension, MetricAggregateValue | Only sum/unit/count/first-start/last-end; no eligibility or contribution membership |
| MetricOperandEvidence | Same aggregate metadata plus coherent revision | Cannot certify numerator/denominator scope |
| OperationalMetricEvaluator | Missing operand => InsufficientEvidence/MissingOperand; zero denominator => Unavailable/ZeroDenominator; other ratios pass ValidateDomain | Out-of-domain values remain InvalidDataException; no blanket catch is safe |
| OEE EvaluateProduct | Evaluates typed dependencies; Unavailable => DependencyUnavailable and null value, retaining nested evidence | Existing typed propagation can be reused after an evidence-backed Availability result exists. Other invalid dependencies still throw |
| Projection factory | Copies aggregate evidence, reason and dependency tree; rounds calculated values only | Any scope witness must be explicitly preserved, not dropped on projection |
| SQL projection | ReasonCode tinyint; migration 005 ReasonCode and StatusShape CHECK constraints explicitly allow only 0–5 | An enum-only addition cannot persist safely. Do not edit the frozen historical migration |
| Dependency snapshot | Frozen FCDS V1 writer explicitly rejects reason >5; decoder validates enum and canonical re-encoding | An enum-only addition cannot round-trip nested Availability in OEE |
| SQL query/summary readers | Validate reason via Enum.IsDefined; reconstruct aggregate evidence | Readers and evidence reconstruction need extension |
| HTTP mapper | Emits status vocabulary, nullable value, ReasonCode.ToString(), source revision | Existing reason string transport is extensible, but end-to-end tests are required for a new reason |

Relevant implementation files: Abstractions/OperationalMetricEvaluation.cs, Abstractions/OperationalMetricProjection.cs, Core/Metrics/OperationalMetricEvaluator.cs, Core/Metrics/OperationalMetricProjectionFactory.cs, Core/InMemoryMetricAggregationStore.cs, Persistence.SqlServer/SqlServerRevisionedOperationalMetricComponentSnapshotReader.cs, Persistence.SqlServer/Sql/005_OperationalMetricReportingPersistence.sql, Persistence.SqlServer/OperationalMetricDependencySnapshotV1Codec.cs and Decoder.cs, SqlServerOperationalMetricProjectionPublication.cs, SqlServerOperationalMetricProjectionQueryReader.cs, SqlServerOperationalMetricProjectionSummaryReader.cs, Api/Reporting/OperationalMetricHttpContracts.cs. Paths are relative to src/.

## Smallest evidence extension proposed

Add an optional typed **unplanned-running witness** to the snapshot, then carry that same immutable evidence through evaluation and projection. It is a positive witness of a specific incompatible contribution, not a boolean guessed by the evaluator and not a recomputed ratio. One positive, validated contribution is sufficient to prove the numerator includes time excluded by the planned-time operand contract; exporting an entire timeline into every projection is unnecessary for containment.

Suggested contract: AvailabilityScopeConflictEvidenceV1 with:

- Exact aggregation revision (processor, machine, stream, position), period, and both aggregate source identities.
- Offending running FactId and source input position (positive and <= snapshot revision).
- SourceEligibilityIntervalId and SourceContextualizedActivityIntervalId.
- Exact StartsAtUtc/EndsAtUtc, seconds value, IsPlannedProductionTime=false and null planned assignment.
- A fixed rule identity/version describing the check: positive running contribution outside planned-production eligibility. The witness does not assert why the schedule is wrong or claim approved schedule authority.

Build the witness from the **same committed source cut** already loaded by historical snapshot readers, restricted to the exact machine/stream/period and duration.running aggregate. Validate duration, unit, membership, identities, source revision, positive value, false eligibility and assignment shape at construction. Bind the denominator to duration.planned-production; reject mixed or mismatched witness identity. Select the lowest offending source position deterministically if several exist. Keep exact source-revision references to resolve the complete operand contribution sets. Absence of a witness means no positively established conflict, **not a certificate of compatible evidence**; legacy readers may provide no evidence. Missing/invalid eligibility cannot be silently treated as false.

For 534, a deterministic witness is position 380, eligibility rca-eligibility-380, contextualized activity rca-activity-380, 02:32:25.791360–02:36:45.428970 UTC, 259.637610 seconds. Its FactId is preserved in the committed test fixture. Both full operand sums and their existing aggregate provenance must remain attached to the unavailable outcome.

Append a reason with an explicit numeric value (proposed slot 6, RunningOutsidePlannedProduction) without renumbering old reasons. Apply only to the exact built-in Availability definition/operand contract, before ratio calculation, with both operands present and denominator positive. Missing operands and zero-denominator retain their existing outcomes. Reject structurally invalid components/witnesses; do not downgrade corruption to ordinary unavailability. Positive witnessed incompatibility produces Unavailable with null value regardless of whether the accidental ratio is above or below one. Without a witness, retain existing domain enforcement. Never change the ratio, infer break placement, or globally redefine ActualProductionTime.

## Persistence and replay change required before containment can ship

1. Add a new forward migration after the current catalog head (currently 020), leaving 005 and historical checksums unchanged. Extend both reason CHECK constraints and introduce explicit durable witness storage bound to the exact projection/evidence identity. Update startup compatibility/authority descriptors and migration expectations as required by the repository contracts.
2. Preserve aggregate provenance and witness fields in publication, strict read validation, replay comparison/equivalence, query detail reconstruction and nested dependencies. A reason without its witness must not pass the new contract.
3. Preserve frozen FCDS V1 bytes and handling of old reasons. Introduce a versioned dependency encoding for the new reason/witness, with explicit dispatch and SQL evidence version constraints. New readers must retain V1 support. Old readers must reject unsupported versions, never interpret an unfamiliar reason as a numeric result. Do not extend the frozen V1 encoder in place.
4. A versioned processor/policy selection must make new failure classification auditable. Do not replay incompatible new semantics into an already-published legacy logical slot or imply all retained historical assessments were reclassified. Verify exact-version report selection and deployment compatibility before enabling a new processor.
5. Add real-SQL round-trip, idempotent publication, evidence-tamper rejection, migration-upgrade, legacy V1 golden-byte, new codec canonicality and HTTP null-value/reason tests. Use a newly created isolated fixture database. Do not use either RCA database as a mutable SQL fixture.

## Original characterization baseline

Gg003AvailabilityContainmentContractTests uses a sanitized offline fixture of the 34 operand contributions (31 running, 3 planned), retaining fact/source identities and boundaries. It reconstructs sums with the real contribution aggregator and uses a snapshot-reader test adapter; it does not exercise live SQL or invoke a projection runtime. Tests cover both exact period identities, exact ratio/domain rejection, loss of eligibility information, valid zero/fraction/one, zero denominator with OEE unavailability and aggregate provenance, missing operands, and unrelated Quality domain failure.

Gg003AvailabilityPersistenceContractTests is database-free. It verifies existing typed unavailability and recursive provenance round-trip in the current dependency codec and HTTP mapping, and demonstrates that proposed reason slot 6 is rejected by the frozen V1 writer. Its zero-denominator case is synthetic contract evidence, not the revision-534 production outcome. No test claims a new reason can currently persist. SQL constraints were inspected in source; no live SQL persistence verification was performed.

Existing evaluator/product/evidence and HTTP endpoint tests are included in the targeted verification. New containment acceptance tests must be added alongside the proposed extension: witness in both scopes => specific unavailability and OEE propagation; same ratio without witness => domain error; mismatch below one => same specific unavailability; foreign/late/negative/invalid witness => rejection; complete aggregate provenance and witness survive SQL and codec replay.

## Separate controlled recovery validation — not run

After review and implementation of the complete extension:

1. Create a new disposable recovery-validation database from the approved baseline. Verify server/database identity and record baseline source revision, projection checkpoint and processor policy. Do not modify the original or existing working RCA database.
2. Apply only the reviewed schema/code there; first verify old history remains readable. Capture exact SHIFT-1/day snapshots at revision 534, both operand sums and the deterministic witness.
3. Process through normal runtime from the appropriate predecessor using an explicitly reviewed versioned processor strategy. Expect null Availability with the specific reason and null OEE with DependencyUnavailable. Require complete atomic publication and preserved lineage.
4. Verify normal checkpoint advancement only after successful batch commit, idempotence/restart, and next actual ledger revision above 534 (do not assume 535 exists).
5. Verify API/dashboard exact-version selection and null serialization. Capture build SHA, migration IDs, database identity and before/after evidence. Review this result separately before any Gajra deployment.

No recovery cycle or database command is authorized or executed by these offline tests. No clamp, schedule change, manual checkpoint advance, reset, repair or deployment is part of this change.

## Verification results (2026-10-17)

- Core targeted suite before Stage A sanitization: 39 passed, 0 failed (Gg003AvailabilityContainmentContractTests, OperationalMetricEvaluatorTests, OperationalMetricProductEvaluationTests, OperationalMetricProjectionEvidenceTests).
- Database-free persistence/HTTP targeted suite: 10 passed, 0 failed (Gg003AvailabilityPersistenceContractTests, OperationalMetricReportingEndpointTests).
- git diff --check and trailing-whitespace checks on new C# and proposal files passed.
- No live SQL tests, source-database access, recovery or deployment performed. These results verify existing contract behavior and the evidence gap; they do not validate an implemented new containment reason.


## Stage A identity sanitization

Deployment IDs in this review artifact are deterministic aliases, not production routing identities. Contribution positions, exact decimal values, UTC boundaries, business date, eligibility flags and identity relationships remain unchanged. The original identifiers, raw exports and identity mapping are retained locally and excluded from the Stage A commit. The TestData provenance manifest binds the sanitized fixture to the locally retained original source digest. No deployment host or connection string is included.

## Stage A local preparation verification

The sanitized Stage A fixture was checked against all 34 selected original contributions, the 167-fact normalized source, raw SQL JSON output, and the operand CSV. Source values, ordering and provenance relationships match; original identifiers and SQL export metadata are excluded. An additional offline integrity test checks the fixture digest, revision, identities, duration conservation, ordering and ownership.

After sanitization: 40 core characterization/evaluator/product/evidence tests and 10 database-free persistence/HTTP tests passed (50 total). No SQL diagnostic category or database fixture ran. The original artifacts and identity map remain local; Stage B, recovery and deployment have not begun.


## Stage A implementation and review map

`AvailabilityScopeConflictEvidenceBuilder.Build` consumes the same loaded committed
contributions as each `ReadAtRevisionAsync` reader. It validates the cut, rejects
late/foreign inputs, reaggregates the exact period operand sets and compares full
aggregate metadata before selecting the lowest positive out-of-plan running
position. A running fact elsewhere in the ledger cannot serve as a witness.
The immutable witness constructor is internal to Abstractions/Core; callers cannot
manufacture or mutate a proof from just flags and a numeric ratio.

The denominator contract is explicitly `duration.planned-production/eligibility-v1`:
only seconds-valued duration facts derived from eligibility=true and a nonempty
planned assignment belong to that operand. Both source IDs and exact duration
conservation are required. The builder rejects missing/inconsistent eligibility;
it does not certify the schedule, the effective POT configuration or approval.
The witness is bound to both complete aggregate identities/values/counts/bounds,
period, stream and revision. Snapshot/evaluation/projection construction rejects
rebinding to a foreign cut or a changed aggregate.

`OperationalMetricEvaluator(..., useAvailabilityContainmentV1: true)` explicitly
opts into policy `availability-scope-containment/1`. Existing constructors and
production composition use the default legacy behavior. The coherent production
batch source is unchanged and never requests witnesses. The opt-in evaluator
uses the new reason only for Availability 1.0 with the declared operand mapping,
a positive denominator and a valid witness. Missing/zero retain their existing
precedence; unwitnessed out-of-domain values still throw. OEE retains the nested
Availability witness and both original aggregate operands.

Every opt-in evaluation, including calculated and missing/zero outcomes, carries
the versioned policy. Projection creation requires the exact processor identity
`operational-metrics:<machine>:builtins-v1-availability-containment-v1`; it rejects
reuse of `builtins-v1`. Recursive projection construction preserves policy and
witness. Legacy evaluation/projection remains separate and unchanged.

Stage A deliberately cannot publish: `OperationalMetricProjectionCommit` rejects
policy-tagged projections, and frozen FCDS V1 rejects the new evidence/policy
rather than silently losing it. Existing V1 bytes are unchanged. These guards
must remain until Stage B supplies reviewed SQL/codec/replay/query compatibility.
There is no API version-selection activation in Stage A. Stage B must prove old
and new durable processors coexist and that HTTP queries select the exact
intended processor before recovery is authorized.

`Gg003AvailabilityStageATests` exercises the real in-memory historical reader,
including later-cut exclusion, deterministic selection, exact 534 outcomes in
both scopes, 75% witnessed versus unwitnessed behavior, invalid evidence,
foreign binding, unrelated-period running, OEE/projection retention and processor
publication guards. Aggregate-only missing/zero probes remain explicitly separate.
The SQL reader uses the same builder, but no real-SQL containment compatibility
claim is made at this stage. The original 167-fact export and identity-map
comparison remain a local provenance attestation, not independent remote proof.

Validation: no local .NET SDK is available in the review environment. GitHub CI
is the build/test authority for this implementation; results must be recorded
for the final reviewed commit. No new pass count is asserted before CI completes.
