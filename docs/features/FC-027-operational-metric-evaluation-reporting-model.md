> **P0-A retention amendment:** [Historical reporting retention](FC-030-historical-reporting-retention-amendment.md) supersedes processor-wide replacement, omission-based removal, manifest-only reporting, and global equality-to-checkpoint semantics below. Existing isolation, lock ordering, atomicity, identity, and exact-batch replay guarantees remain. This document preserves the earlier contract/proof record.

# FC-027 — Operational Metric Evaluation and Reporting Model

## Status

FC-027 defines the provider-neutral operational metric layer above FC-026 durable shift and production-day aggregates.

The feature deliberately separates component aggregation, logical metric evaluation, durable projection, and reporting consumption.

```text
FC-026 durable aggregates
        ↓
exact aggregation revision discovery
        ↓
coherent component snapshot
        ↓
versioned logical metric evaluation
        ↓
durable operational metric projection
        ↓
provider-neutral summary/detail reporting
```

## Definition identity and dependency graph

An operational metric definition is identified by both `MetricKey` and `Version`. Dependent metrics reference an exact `OperationalMetricDefinitionId`; a new version never silently retargets an existing formula.

The built-in `1.0` definition set contains:

- Availability = Actual Production Time / Planned Operating Time
- Utilization = Actual Production Time / Machine Power-On Time
- Performance = Production Reference Time / Actual Production Time
- Quality = Good Quantity / Produced Quantity
- OEE = Availability × Performance × Quality

The catalog validates the complete dependency graph before use, including duplicate definitions, exact dependency availability, scope propagation, dimensional/unit compatibility, and cycles. Evaluation order is deterministic.

## Evaluation grain

FC-027 evaluates the aggregate grain produced by FC-026:

- machine + shift occurrence; or
- machine + production day.

The current FC-026 aggregate key does not partition by production order, operation, part, or operator. Therefore the composed FC-027 runtime accepts only `OperationalMetricEvaluationContextKey.Unpartitioned`. Typed context remains part of evaluation/report identity for future compatible expansion, but the runtime must not fabricate partitioned metrics from unpartitioned aggregates.

Production-day ratios are calculated from production-day components. Shift ratios are never averaged to produce a production-day ratio.

## Coherent evaluation

`CoherentOperationalMetricEvaluationBatchSource` consumes `MetricAggregationRevisionChange` records. For every affected period it reconstructs one component snapshot at the exact FC-026 revision and evaluates the complete definition set supported by that period scope.

A root evaluation session pins:

- machine;
- period;
- context;
- exact FC-026 aggregation checkpoint;
- validated component map; and
- exact-version dependency graph.

Logical decimal values remain unrounded through dependency composition. Durable precision policy is applied only when creating the projection.

Business outcomes such as missing evidence or zero denominator are represented as metric statuses. Corrupt durable state, incompatible units/dimensions, domain violations, and identity/revision mismatches are processing failures.

## Durable projection and replay

`OperationalMetricProjectionProcessingRuntime` converts one coherent logical batch into durable projections and advances its projection checkpoint atomically through `IOperationalMetricProjectionStore`.

A projection retains:

- exact evaluation key and definition version;
- calculated/unavailable/insufficient-evidence status;
- durable rounded value when calculated;
- reason when not calculated;
- exact FC-026 source revision;
- component evidence; and
- recursive dependency evidence.

The checkpoint contains an order-independent batch manifest of the complete projected key set. Replay at the same FC-026 revision is accepted only when the manifest and every recursively persisted projection are structurally equivalent. Omitted, added, or changed projections fail replay validation.

## Definition-set deployment rule

Projection processor identity owns a definition-set lineage. The composed built-in runtime currently uses a processor identity ending in `builtins-v1`.

Changing the registered definition set must use either:

1. a new projection processor identity; or
2. a future explicit re-evaluation/backfill workflow.

Reusing an existing processor identity with changed same-revision output is intentionally rejected by replay-manifest validation.

## Reporting boundary

Reporting is read-only and never recalculates metrics.

Period summaries expose scalar reporting data only:

- exact definition ID;
- status/value/unit;
- reason; and
- one coherent report-level FC-026 source revision.

`OperationalMetricProjectionSummary` can be materialized directly from scalar provider columns. A SQL provider therefore does not need to hydrate recursive evidence for an ordinary period report.

Exact metric detail is a separate lookup requiring an exact `OperationalMetricDefinitionId`. Detail exposes the complete component and recursive dependency lineage. There is no `MetricKey`-only or "latest version" lookup.

Mixed source revisions in one period summary are invalid.

## Persistence-provider capabilities

FC-027 durability participates in the same provider lifecycle established by FC-022. A selected persistence provider declares the capabilities it supports before activation.

The FC-027 runtime requires:

- `IMetricAggregationRevisionReader`;
- `IRevisionedOperationalMetricComponentSnapshotReader`;
- `IOperationalMetricProjectionStore`; and
- `IOperationalMetricProjectionQueryReader`.

The in-memory provider currently supplies the complete capability set. It shares one `InMemoryMetricAggregationStore` across ordinary aggregation, revision discovery, and exact historical snapshots, and one `InMemoryOperationalMetricProjectionStore` across durable projection writes and reporting queries.

The current SQL Server provider remains **core-only**. It continues to support the existing observation, production-context, metric-input, and FC-026 aggregate persistence contracts, but it does not yet implement the four FC-027 capabilities above. Therefore a full Edge application configured with `Persistence:Provider = SqlServer` is rejected during persistence finalization rather than silently mixing SQL FC-026 state with in-memory FC-027 state. SQL remains valid for core-only composition until SQL-backed FC-027 durability is implemented.

## Runtime composition

Edge composition creates one operational metric projection runtime per configured machine. Each runtime has:

- its machine-specific FC-026 aggregation processor ID;
- its machine metric-input stream;
- its own projection processor ID;
- the immutable built-in definition catalog;
- coherent revision/snapshot readers;
- durable projection store; and
- provider-neutral reporting reader.

Machine loops run independently. A failure in one machine's projection loop is logged and retried without terminating another machine loop.

The selected persistence provider must expose the FC-027 revision, historical-snapshot, projection-store, and projection-query capabilities. These capabilities are required because FC-027 must reconstruct exact historical revisions rather than evaluate old checkpoints from current aggregate values, and its durable checkpoint/evidence state must survive according to the selected provider's durability semantics.

## Explicitly deferred

FC-027 does not provide:

- HTTP/REST/GraphQL reporting APIs;
- dashboards or chart rendering;
- cross-machine, line, or site rollups;
- incentives or ranking;
- downtime classification workflows;
- manual correction/backfill orchestration;
- SQL-specific reporting optimization or SQL-backed FC-027 projection persistence;
- alerting or predictive metrics; or
- unresolved plant-specific metric formulas.

Those concerns belong to later features, beginning with FC-028 reporting API composition.
