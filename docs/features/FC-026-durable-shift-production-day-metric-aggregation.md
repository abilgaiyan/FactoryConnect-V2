# FC-026 — Durable Shift and Production-Day Metric Aggregation

## Purpose

FC-026 converts the durable metric-input facts produced by FC-025 into durable additive totals by shift occurrence and production day. It does not reclassify production context and does not calculate presentation formulas such as OEE, utilization, incentives, or reporting labels.

## End-to-end boundary

```text
FC-025 activity processor      FC-025 quantity processor
          │                              │
          └──────── independent source checkpoints ────────┐
                                                         │
                         atomic producer publication       │
                                                         ↓
                  machine metric-input stream
                  + immutable temporal ownership
                  + persistence-owned position
                              ↓
                  machine aggregation processor
                              ↓
             atomic aggregation transaction
             ├── contribution idempotency
             ├── shift-occurrence projection
             ├── production-day projection
             └── aggregation checkpoint
```

FC-025 owns `ShiftOccurrenceId` and `ProductionDayId`. Persistence owns `MetricInputPosition`. FC-026 consumes those values exactly as persisted and never recomputes schedules, time zones, production-day boundaries, or calendar ownership.

## Durable input stream

Each machine has one `MetricInputStreamId`. Activity-derived and quantity-derived metric facts retain independent producing checkpoints but publish into the same machine-scoped stream. Persistence allocates monotonically increasing positions transactionally.

Replay rules are:

```text
new FactId + valid immutable payload
    → allocate one position

existing FactId + equivalent payload
    → preserve original position

existing FactId + conflicting payload
    → reject
```

Producer publication and producer checkpoint advancement are one durability boundary. A positioned fact is never visible unless its producing FC-025 checkpoint commits, and a producing checkpoint cannot acknowledge a fact that was not published.

## Aggregate identities

Shift aggregates are identified by machine, resolved shift occurrence, and metric-input key. Production-day aggregates are identified by machine, resolved production day, and metric-input key. `Unit` is deliberately excluded from aggregate identity.

```text
same aggregate key + same unit
    → merge

same aggregate key + incompatible unit
    → reject entire commit
```

The aggregate stores generic decimal value, unit, input count, first input timestamp, and last input timestamp.

## Aggregation transaction

For each durable read window the aggregation store performs one atomic commit:

```text
validate expected checkpoint
        ↓
verify durable positioned facts
        ↓
identify new/replayed contributions
        ↓
merge shift aggregates
        ↓
merge production-day aggregates
        ↓
record contribution identities
        ↓
advance checkpoint
        ↓
commit
```

If any step fails, neither projection, the contribution ledger, nor the checkpoint changes.

A new contribution must lie after the expected checkpoint and at or before the proposed checkpoint. An identical already-contributed fact may replay at or behind acknowledged progress as a no-op.

## Provider boundary

Persistence provider selection activates one coherent provider service set:

- `IObservationIngestionStore`
- `IProductionContextProcessingStore`
- `IMetricInputReader`
- `IMetricAggregationStore`

The neutral persistence assembly selects only by registered provider key. It contains no provider-name switch.

The in-memory provider shares one `InMemoryProductionContextProcessingStore` instance between FC-025 publication and downstream metric-input reading. The SQL Server provider uses one configured connection string across observation ingestion, FC-025 producer commits, metric-input reads, and aggregation storage.

SQL Server additionally provides executable additive migrations, relational ownership constraints, canonical decimal serialization, canonical aggregate keys with fixed-size lookup hashes, and full canonical-key verification after hash lookup.

## Machine execution and failure isolation

Edge creates one acquisition runtime, one activity producer, one quantity producer, and one `MetricAggregationProcessingRuntime` per configured machine. Acquisition, producer, and aggregation hosted workers supervise independent machine/runtime loops.

A machine acquisition, producer, or aggregation reader/store failure is logged for that processor and only that machine/runtime loop is retried. Other machines continue processing and advancing their own checkpoints. Cancellation is propagated to all loops and each worker joins its loops before stopping.

This operational isolation complements the durable identity isolation already provided by machine-scoped streams and processor-scoped checkpoints.

## Restart and replay

Each FC-025 producer and FC-026 aggregation runtime restores its own durable checkpoint before its first read. Successful commit is the only acknowledgement boundary.

Therefore:

- producer restart resumes after the last committed activity or quantity evidence position;
- aggregation restart resumes after the last committed metric-input position;
- identical producer replay preserves the existing metric-input position and cannot inflate totals;
- conflicting producer replay is rejected without producer or aggregation progress;
- a failed producer commit exposes no positioned metric input downstream;
- empty aggregation read windows may advance explicit source progress without creating aggregates.

## Edge composition

`MTConnect:Machines` is the root machine inventory for the executable Edge host. Each entry supplies the machine identity, MTConnect endpoint, device key, bootstrap sequence, and acquisition polling interval. The inventory derives the canonical observation stream for every machine.

`ProductionProcessing:Machines` then binds those same machines and activity streams to quantity streams, company/site/line scope, production-context assignments, shift schedules, and planned-production schedules.

`AddFactoryConnectEdgeApplication` constructs the shared inventory once and supplies it to acquisition, FC-024 observation processing, FC-025 production processing, and FC-026 aggregation. `Program.cs` invokes that same composition method, so the executable host and composition tests use one root path.

```text
MTConnect:Machines
        ├── Machine A / Stream A
        └── Machine B / Stream B
              ↓ shared inventory
MTConnect acquisition runtimes + durable sinks
              ↓
FC-024 observation processing
              ↓
FC-025 machine-scoped producer pairs
              ↓
provider-selected machine metric-input streams
              ↓
FC-026 machine-scoped aggregation processors
```

`ObservationProcessing:Streams` and `ProductionProcessing:Machines` must match the shared MTConnect-derived stream inventory. Missing, extra, duplicate, or mismatched machine/stream configuration is rejected during composition.

`ProductionProcessing:BatchSize` and `ProductionProcessing:PollingInterval` configure producer execution. `MetricAggregation:BatchSize` and `MetricAggregation:PollingInterval` configure aggregation execution. MTConnect retry policy remains shared while each machine has its own acquisition options, sink, startup checkpoint, and runtime factory.

The machine inventory retains backward compatibility with the former single-machine `MTConnect` shape when `MTConnect:Machines` is absent, but the production composition model itself is multi-machine.

## Conformance

The composed FC-026 scenarios prove:

- the real Edge application root creates acquisition, FC-024, FC-025, and FC-026 layers for two configured machines from one inventory;
- activity and quantity producers publish into one downstream metric-input sequence per machine;
- two machines retain distinct producer checkpoints, metric-input streams, aggregation checkpoints, and totals;
- overnight facts after midnight retain the opening shift's production-day identity through aggregation;
- one machine producer failure does not prevent another machine from publishing and aggregating, and recovery occurs without host restart;
- identical producer replay preserves the original positioned fact;
- conflicting producer replay leaves producer and aggregation progress unchanged;
- failed producer commit makes no metric input visible downstream;
- in-memory and SQL Server provider selection both execute producer publication, ordered metric-input consumption, aggregation, and restart semantics.

## FC-027 handoff

FC-026 ends at durable additive operational totals. The next reporting/evaluation feature may read shift and production-day aggregates to calculate formulas and reporting views. FC-026 remains intentionally unaware of OEE formulas, downtime labels, operators, jobs, incentives, dashboards, and historical correction orchestration.
