# FC-024 — Durable Observation Processing Pipeline

## Purpose

FC-024 transforms acquired machine observations into durable, replay-safe
factory-domain history without coupling the domain runtime to a persistence
provider.

## Transformation chain

```text
Protocol observation
    ↓
Atomic raw ingestion and acquisition checkpoint
    ↓
Durable raw observation position
    ↓
Canonical signal mapping
    ↓
Durable canonical observation
    ↓
Machine state/activity projection
    ├── durable state change
    └── durable completed activity period
```

Each stage owns independent progress. A downstream failure never moves the
upstream processing checkpoint for that stage.

## Durable identities

Raw and canonical observations preserve:

- observation stream
- durable observation position
- protocol instance
- protocol sequence

State changes and completed activity periods additionally preserve the stable
processor identity. A completed activity period uses the position of the
observation that closes the period:

```text
Position  = closing observation identity
StartedAt = activity start timestamp
EndedAt   = closing transition timestamp
```

## Processing semantics

Processing does not imply output:

```text
raw processed + no mapping
    → no canonical observation
    → raw processing checkpoint advances

canonical processed + unchanged state
    → no state change or completed activity
    → projection position advances
```

Configuration changes do not implicitly replay earlier filtered observations.
Historical reinterpretation requires an explicit processor identity or
checkpoint reset policy.

## Canonical state-value semantics

`MachineStateActivityProcessor` evaluates the canonical state-driving signals
`state.running`, `state.idle`, and `state.fault` only when they carry
`SignalType.Digital` Boolean values. FC-024 does not normalize protocol-specific
enumerations into those Boolean semantics.

Therefore a mapping such as:

```text
MTConnect execution = "ACTIVE"
    → state.running / Enumeration
```

is invalid for state projection and is rejected during Edge composition.
State-driving mappings must already provide normalized Digital Boolean values,
for example from a digital input or a future normalization stage.

The checked-in MTConnect Edge configuration intentionally contains no default
state-driving mapping. MTConnect execution-state normalization, such as
`ACTIVE → true` and `STOPPED/READY → false`, is deferred to a later slice rather
than silently acknowledging a canonical value that the state evaluator cannot
interpret.

## Delivery and replay

Delivery between stages is at least once.

- equivalent replay is idempotent
- conflicting output at the same durable identity is rejected
- state/activity projection state and derived outputs commit atomically
- canonical-store failure leaves raw processing progress unchanged
- canonical write followed by raw processing checkpoint failure replays the raw
  observation and remains idempotent at the canonical boundary
- projection-store failure leaves canonical input eligible for retry
- restart resumes after independently persisted stage progress

Canonical positions may contain gaps because unmapped raw observations are
still successfully processed. Readers therefore order by position and do not
require positions to be contiguous.

## Provider-neutral stage boundary

Canonical output is never passed directly from the mapping processor to the
state/activity processor. The mapping stage writes through
`IMappedMachineObservationSink`, and the next stage resumes through
`IDurableMappedObservationReader` and `MappedObservationProcessingRuntime`.
This keeps both processing stages independently checkpointed and retryable and
allows a future durable provider to replace the in-memory canonical store
without changing Edge orchestration.

## Multi-machine and multi-stream composition

The observation-processing registration supports one or more observation
streams. Every stream receives its own:

- `MachineSignalMappingConfiguration`
- raw-to-canonical processor/runtime
- canonical-to-state/activity processor/runtime
- independent raw processing checkpoint
- independent state/activity projection

Canonical and projection stores may be shared because their durable keys include
stream identity. Processor identities remain stable across streams, while
progress stays isolated by processor plus stream.

For a single stream, `ObservationProcessing:Mappings` remains supported. When
multiple streams are registered, mappings are bound explicitly under
`ObservationProcessing:Streams`, with each entry identifying its `MachineId`,
`StreamKey`, and mapping collection. Missing, duplicate, or extra stream
configuration fails during composition rather than silently applying another
machine's mapping.

The singular `DurableObservationProcessingPipeline` compatibility service is
registered only for a single configured stream. Multi-stream composition exposes
`DurableObservationProcessingPipelineSet` instead, so a valid multi-stream
container does not contain a service descriptor that is guaranteed to fail when
resolved.

## Edge composition

The Edge composition root registers:

- the selected raw observation persistence provider
- machine/stream-specific canonical mapping configuration
- raw-to-canonical runtimes
- durable canonical reader/store
- canonical-to-state/activity runtimes
- state/activity projection store
- one hosted coordinator over all configured stream pipelines

A selected raw persistence provider must implement
`IDurableObservationReader` and
`IObservationProcessingCheckpointStore`. Missing capabilities cause a clear
startup failure. The current complete end-to-end composition uses the in-memory
reference provider.

### Current executable-host limitation

The reusable observation-processing composition is multi-stream capable, but the
current `FactoryConnect.Edge` executable still binds one `MTConnect` acquisition
section, creates one acquisition runtime, derives one observation stream, and
calls the single-stream observation-processing overload. This is an explicit
host limitation, not a limitation of the FC-024 processing architecture.

Composing multiple live acquisition runtimes belongs to the acquisition/host
composition boundary and is intentionally not expanded inside FC-024. A future
multi-machine Edge-host slice can bind a collection of acquisition definitions,
derive all stream identities, and pass those identities to the existing
multi-stream processing registration without changing the durable processing
contracts defined here.

## Conformance proof

The in-memory conformance scenario proves:

- processing across multiple durable batches
- acknowledgement of unmapped raw observations
- canonical observations that produce no state transition
- sparse canonical positions
- restart/resume without duplicate canonical or derived output
- canonical sink failure without raw checkpoint advancement
- canonical write followed by raw processing checkpoint failure and idempotent
  replay
- projection commit failure followed by equivalent retry
- independent mappings and progress across machines and streams
- a valid multi-stream service graph without a singular pipeline registration
- empty mapping configuration is valid when no canonical derivation is intended
- non-Digital state-driving mappings fail clearly at composition time

## Deferred boundaries

FC-024 intentionally excludes:

- SQL Server canonical and state/activity persistence
- production and shift context enrichment
- metric-input derivation and aggregation
- reporting queries
- downtime reason classification
- implicit historical reprocessing
- multi-acquisition executable-host composition
- protocol-specific canonical value normalization such as MTConnect execution
  enumeration to Digital running state

These belong to subsequent provider and factory-domain slices.
