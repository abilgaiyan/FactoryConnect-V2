# RR03-DP.3B — Exact-revision Running observation provenance

Status: initial read-only implementation for review. DP.3B is not closed: authoritative continuity/completeness admission for Established remains outstanding. No numeric Availability or assessment publication is implemented.

## Trace

The state/activity processor publishes `DurableMachineActivityPeriod` history at an exact observation processor/stream/position. `ActivityContextIntervalAllocator` retains that origin while partitioning by context and shift. `ProductionTimeEligibilityAllocator` partitions it again without changing duration. `DurableMetricInputFactDeriver` emits scheduled/state duration facts; Unknown has no state-duration operand. The production-context store commits source outputs and facts atomically. The aggregation contribution ledger binds the incorporated fact positions to an exact aggregation processor/stream/revision.

These facts establish observed classified intervals. They do not independently establish full-period coverage or historically admitted continuity. In particular, the state processor can preserve state across an instance boundary under its selected policy. One activity output may carry the closing observation's instance, so source-instance labels alone cannot reconstruct every reset or missing-input boundary.

## Implemented SQL reader

`SqlServerRunningProvenanceReader.ReadExactAsync` accepts an exact aggregation checkpoint, shift occurrence, production day and a positive requested extent contained in that shift. It uses one serializable transaction to:

1. Verify the exact processor/stream/revision exists; never read a newer revision as fallback.
2. Reconstruct incorporated source facts only through that revision from the contribution ledger.
3. Validate scope and bind each relevant fact to its persisted eligibility and contextualized activity outputs, verifying source hashes, identities, hierarchy and state/bounds.
4. Re-derive expected facts from the persisted eligibility output and compare exact fact content. Source payloads that exist ahead of the aggregation cut do not authorize using state facts absent from that cut.
5. Validate the referenced machine-activity history row at its exact observation processor/stream/position, including state, bounds, instance, sequence and unique history identity. `MachineStateActivityAuthorityPublication.ValidateActivityPeriods` requires strictly increasing activity positions within a publication; `SqlServerMachineStateActivityAuthorityStore.IsStrictlyForward` requires every later output to advance beyond the previous projection position. A valid publication may contain multiple outputs at distinct positions. The reader binds the retained row's ProjectionRevision/OutputOrdinal into the fingerprint and rejects duplicate rows at a position as corruption. Same-position multi-output publication is not allowed by the canonical contract.
6. Reconcile observed slices without double-counting conflicts. Return a deterministic V1 evidence fingerprint over the requested cut/extent, source hashes/history binding and incorporated positions.

The existing historical operand reader receives only an optional transaction parameter on its private contribution helper; existing callers retain their previous behavior. No canonical projection publication, query-selection rule, schema or worker is changed.

## Outcomes and limits

- Missing: absent revision, no usable state contribution, or only Unknown/Offline observations. Observed Running is null, never a substituted zero.
- Partial: validated known state contributions exist. Retain their observed Running duration, source slices, unobserved geometry, unavailable intervals and observed instance transitions. Known non-Running evidence establishes zero Running only within its observed slices; it does not establish a zero shift total.
- Incomparable: wrong stream/machine, ownership/day mismatch, or incompatible observation-source bindings.
- Corrupt: referenced output absent/corrupt, fact/source mismatch, conflicting overlaps, or inconsistent activity-history reference. No numeric duration is returned.
- Established: reserved by the result contract, deliberately never emitted by this reader. Interval adjacency, closing time, durable hashes or configuration admission cannot substitute for the independent continuity/coverage authority required to emit this state.

Unobserved intervals describe missing slice geometry, not measured downtime. Unavailable intervals preserve Unknown/Offline and state facts missing at the requested cut. Visible instance changes are retained, but the reader does not claim comprehensive reset detection inside a source activity interval. All Partial results remain unsuitable for unqualified full-shift Availability.

## Validation and remaining work

Local validation: 11 Core conformance tests passed; integration-test project build passed with zero warnings/errors. SQL fixtures use real state/activity, production-context, metric-input and aggregation stores; tests cover Running/known-zero/Unknown, source corruption, missing source, historical restart/replay after newer cuts, ownership mismatch, exact-cut boundaries, instance changes and conflicting overlaps. SQL CI acceptance remains pending.

Before closing DP.3B, define and integrate an independently verified exact-cut continuity/coverage authority, including gaps, unavailable signals, reset-policy applicability and completion boundaries. Then add Established and full-period-zero conformance against that authority. Do not enable an Established result through a caller-supplied boolean or an opaque unverified coverage reference.

No DP.3C producer, factory snapshot/admission activation, reporting API/Dashboard integration, revision 534 recovery or deployment accompanies this slice.
