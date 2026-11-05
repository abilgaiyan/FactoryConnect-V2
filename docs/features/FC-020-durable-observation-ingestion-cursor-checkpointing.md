# Durable Observation Ingestion & Cursor Checkpointing

## Purpose

FC-020 establishes the durability boundary between MTConnect acquisition and downstream observation storage.

FC-017 introduced continuous acquisition.

FC-018 added transient retry and backoff.

FC-019 added continuity recovery and acquisition-session replacement.

FC-020 prevents a successful acquisition cursor from becoming durable unless the observations represented by that cursor become durable in the same commit.

```text
acquired MTConnect batch
        ↓
persist sequenced observations
        +
persist InstanceId / NextSequence checkpoint
        ↓
commit atomically
        ↓
acknowledge sink handoff
        ↓
advance in-memory acquisition cursor
```

> FC-020 never persists the cursor independently from its observations.

> A cursor is evidence that every observation before it has been accepted by the ingestion boundary.

---

## Failure Being Prevented

Persisting observations and cursor state independently can permanently skip machine data.

```text
acquire observations 101–110
        ↓
persist NextSequence = 111
        ↓
observation write fails
        ↓
process restarts from 111
        ↓
observations 101–110 are lost
```

The inverse ordering is also unsafe without replay semantics:

```text
persist observations 101–110
        ↓
checkpoint write fails
        ↓
restart from 101
        ↓
observations are replayed
```

FC-020 makes the second case safe through idempotent sequence identity while eliminating the first case through one atomic store operation.

---

## Architectural Boundary

FC-020 introduces a protocol-neutral ingestion contract in
`FactoryConnect.Abstractions`.

```text
FactoryConnect.Protocols.MTConnect
        │
        ├── InstanceId
        ├── NextSequence
        └── sequenced observations
                ↓
FactoryConnect.Edge
        │
        ├── canonical stream identity
        ├── MTConnect result translation
        ├── expected checkpoint handoff
        └── commit-before-advance ordering
                ↓
FactoryConnect.Abstractions
        │
        ├── ingestion batch
        ├── checkpoint
        ├── sequenced observation
        └── ingestion store contract
                ↓
FactoryConnect.Infrastructure
        │
        └── in-memory atomic implementation
```

The abstractions do not depend on MTConnect.

The Edge adapter translates protocol-specific acquisition results into the protocol-neutral persistence model.

---

## Observation Stream Identity

A persistence stream is not identified by `MachineId` alone.

One machine may eventually expose more than one acquisition stream.

`ObservationStreamId` therefore contains:

- `MachineId`
- `StreamKey`

For MTConnect, `MtConnectObservationStreamId.Create()` constructs the key centrally.

```text
machine = <MachineId>
device  = " cnc-01 "
        ↓
stream key = "mtconnect:CNC-01"
```

Whitespace is trimmed and the device key is normalized with invariant uppercase.

This prevents configuration spelling differences such as `CNC-01` and
`cnc-01` from creating two durable streams.

Case-insensitive equality is not embedded in the generic
`ObservationStreamId` type. Each protocol adapter remains responsible for canonical identity construction.

---

## Checkpoint Model

`ObservationCheckpoint` represents one committed acquisition position.

It contains:

```text
ObservationCheckpoint
        │
        ├── StreamId
        ├── InstanceId
        └── NextSequence
```

`NextSequence` is the next sequence the acquisition runtime should request after the committed batch.

The checkpoint does not claim that the MTConnect Agent still retains that sequence. FC-019 continuity recovery remains responsible for Agent restarts and buffer loss.

---

## Sequenced Observations

`SequencedMachineObservation` preserves the source protocol sequence beside the canonical `MachineObservation`.

```text
SequencedMachineObservation
        │
        ├── Sequence
        └── MachineObservation
```

The in-memory adapter identifies a stored observation by:

```text
StreamId + InstanceId + Sequence
```

This provides replay identity without treating sequence values from different Agent instances as equivalent.

---

## Atomic Ingestion Batch

`ObservationIngestionBatch` is the unit of commit.

It contains:

- `ExpectedCheckpoint`
- the new `Checkpoint`
- an immutable snapshot of sequenced observations

```text
ObservationIngestionBatch
        │
        ├── ExpectedCheckpoint = X
        ├── Checkpoint = Y
        └── Observations
                ↓
        atomic transition X → Y
```

The constructor copies the observation collection into an array. Later changes to the caller's collection cannot alter the batch after it has been prepared.

Expected and new checkpoints must identify the same stream.

---

## Optimistic Checkpoint Concurrency

MTConnect `InstanceId` values are continuity identifiers, not values FactoryConnect can safely order numerically.

The store therefore does not assume that a larger `InstanceId` is newer.

Instead, FC-020 uses expected-checkpoint semantics.

### Initial Commit

```text
durable checkpoint = null
expected checkpoint = null
new checkpoint = 42 / 103
        ↓
commit succeeds
```

A first commit with a non-null expected checkpoint is rejected.

### Normal Continuation

```text
durable checkpoint = 42 / 103
expected checkpoint = 42 / 103
new checkpoint = 42 / 111
        ↓
commit succeeds
```

### Agent Instance Transition

```text
durable checkpoint = 42 / 111
expected checkpoint = 42 / 111
new checkpoint = 43 / 2
        ↓
commit succeeds
```

The lower sequence is valid because the transition explicitly starts from the currently durable old-instance checkpoint.

### Stale Writer

```text
durable checkpoint = 43 / 500
expected checkpoint = 42 / 103
new checkpoint = 42 / 200
        ↓
expected != durable
        ↓
reject before mutation
```

This protects against:

- delayed retries
- stale runtime instances
- accidental multiple writers
- future multi-machine supervision races
- process replacement overlapping an old process

A failed comparison changes neither observations nor checkpoint state.

---

## Idempotent Replay

A caller may not know whether a commit succeeded if acknowledgement is interrupted after storage completed.

FC-020 permits an exact checkpoint replay.

```text
durable checkpoint = Y
incoming checkpoint = Y
        ↓
all incoming observations already exist identically?
        ├── yes → idempotent success
        └── no  → reject
```

An idempotent replay cannot add previously missing observations beneath an already committed checkpoint.

A duplicate sequence with a different observation is also rejected before mutation.

This prevents a caller from using the replay path to alter the meaning of an existing durable cursor.

---

## Batch Validation

Before any state changes, the in-memory store validates that:

- the durable checkpoint matches `ExpectedCheckpoint`, unless the operation is an exact replay
- same-instance checkpoints do not move backwards
- every observation belongs to the checkpoint machine
- every observation sequence is less than `NextSequence`
- duplicate identities do not contain conflicting observations
- replay does not add observations beneath an existing checkpoint
- cancellation has not already been requested

Validation and staging complete before the observation dictionary or checkpoint dictionary is mutated.

---

## Empty Observation Batches

An empty observation set may legitimately advance an MTConnect checkpoint.

MTConnect sequence numbers are global to the Agent stream. The Agent may advance because of other devices or data items while the selected device produces no observations.

```text
expected checkpoint = 42 / 103
observations = []
new checkpoint = 42 / 111
        ↓
valid atomic commit
```

FC-020 therefore does not require every checkpoint transition to contain an observation.

The transition is still protected by expected-checkpoint concurrency.

---

## In-Memory Store

`InMemoryObservationIngestionStore` implements
`IObservationIngestionStore`.

The interface exposes only:

```csharp
ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(...);

ValueTask CommitAsync(
    ObservationIngestionBatch batch,
    CancellationToken cancellationToken = default);
```

`ReadObservations()` exists only on the concrete in-memory adapter for tests and diagnostics. Production ingestion code does not depend on observation-query behavior.

The adapter uses one lock to validate, stage, write observations, and replace the checkpoint as one in-memory critical section.

### Important Durability Qualification

The contract defines durable ingestion semantics, but the FC-020 adapter stores data only in process memory.

It does not survive process termination.

A later SQL Server adapter must implement the same contract with a database transaction and optimistic checkpoint comparison.

FC-020 intentionally establishes the semantics before selecting the database schema and transaction implementation.

---

## MTConnect Durable Sink

`MtConnectDurableObservationSink` translates an
`MtConnectSampleResult` into an `ObservationIngestionBatch`.

```text
MtConnectSampleResult
        │
        ├── InstanceId
        ├── NextSequence
        └── MtConnectSampleObservation[]
                ↓
MtConnectDurableObservationSink
                ↓
ObservationIngestionBatch
        │
        ├── ExpectedCheckpoint
        ├── new ObservationCheckpoint
        └── SequencedMachineObservation[]
                ↓
IObservationIngestionStore.CommitAsync
```

Store failures, cancellation, sequence conflicts, and stale-checkpoint conflicts propagate to the runtime.

The sink does not acknowledge a failed commit.

---

## Prepare, Commit, Then Advance

Before FC-020, `MtConnectAcquisitionSession.AcquireNextAsync()` advanced its in-memory cursor immediately after successful HTTP acquisition.

That is correct for direct protocol use, but it is too early for a durability-aware runtime.

FC-020 adds a two-phase session path:

```text
PrepareNextAsync
        ↓
acquire and validate result
        ↓
do not advance session
        ↓
durable sink commit
        ↓
Advance
```

The existing `AcquireNextAsync()` API remains backward-compatible. It performs prepare and advance internally.

The Edge runtime uses the two-phase API.

---

## Runtime Commit Ordering

`MtConnectAcquisitionRuntime.RunCycleAsync()` now performs:

```text
capture last committed checkpoint X
        ↓
prepare MTConnect result
        ↓
sink commits observations + checkpoint Y
        │
        ├── failure → propagate; session remains at old cursor
        └── success
                ↓
        advance session cursor
                ↓
        remember checkpoint Y
                ↓
        return successful result
```

The runtime passes the checkpoint from which the acquisition originated.

The sink does not read the latest checkpoint immediately before committing and substitute it as the expected state. Doing so would allow a stale writer to overwrite a newer writer.

---

## Commit Failure and Reacquisition

A store failure leaves both durable and in-memory cursor state unchanged.

```text
GET /sample?from=101
        ↓
result NextSequence = 111
        ↓
store commit fails
        ↓
runtime cycle fails
        ↓
next cycle
        ↓
GET /sample?from=101
        ↓
store commit succeeds
        ↓
session advances to 111
```

This is at-least-once acquisition behavior at the ingestion boundary.

It does not claim end-to-end exactly-once processing. Downstream processing and external side effects require their own idempotency and transaction boundaries.

---

## Continuity Recovery Integration

FC-019 may replace the acquisition session after `OUT_OF_RANGE` or an Agent-instance change.

FC-020 retains the last successfully committed checkpoint while recovery prepares a result from the replacement session.

```text
durable checkpoint = instance 42 / sequence 111
        ↓
Agent changes to instance 43
        ↓
FC-019 replaces session from firstSequence 500
        ↓
prepare instance 43 result
        ↓
atomic commit
expected = 42 / 111
new      = 43 / NextSequence
        ↓
advance replacement session
```

The instance transition is explicit and protected from stale writers.

---

## Runtime Composition

The Edge worker now composes:

```text
MtConnectSampleClient
        ↓
MtConnectAcquisitionSession
        ↓
MtConnectAcquisitionRuntime
        ↓
MtConnectDurableObservationSink
        ↓
IObservationIngestionStore
        ↓
InMemoryObservationIngestionStore
```

The in-memory adapter exercises the real acquisition-to-ingestion path during FC-020.

It is not presented as process-restart persistence.

---

## Cancellation

Cancellation is checked before in-memory mutation and flows through:

- HTTP acquisition
- transient retry
- continuity recovery
- durable sink handoff
- ingestion store commit
- polling delay

A pre-cancelled commit changes neither observations nor checkpoint state.

Cancellation during a failed cycle does not advance the acquisition session cursor.

---

## Failure Classification

| Condition | FC-020 behavior |
|---|---|
| Successful first commit | Store observations and checkpoint |
| Successful continuation | Compare expected state and advance |
| Explicit Agent-instance transition | Commit old-instance → new-instance state |
| Empty observation result | May advance checkpoint |
| Duplicate identical replay | Idempotent success |
| Replay adds a missing observation | Reject |
| Duplicate sequence conflicts | Reject atomically |
| Same-instance cursor regression | Reject atomically |
| Stale expected checkpoint | Reject atomically |
| Observation sequence equals `NextSequence` | Reject |
| Observation belongs to another machine | Reject |
| Store failure | Propagate; do not advance session |
| Cancellation | Propagate; do not mutate state |

---

## Validation

FC-020 verifies:

- observations and checkpoint commit together
- ingestion batches snapshot their input observations
- initial commit requires a null expected checkpoint
- same-instance continuation requires the current durable checkpoint
- same-instance checkpoint regression is rejected
- explicit instance replacement may begin at a lower sequence
- stale instance and stale writer commits are rejected
- failed stale commits mutate neither observations nor checkpoint
- identical replay is idempotent
- replay cannot add observations below an existing checkpoint
- conflicting duplicate sequences are rejected atomically
- multiple streams remain isolated
- empty batches may advance checkpoints
- observation sequence must be less than `NextSequence`
- pre-cancelled commits do not mutate state
- MTConnect stream keys are canonicalized centrally
- MTConnect results translate into protocol-neutral ingestion batches
- durable sink conflicts propagate without mutation
- runtime passes the expected pre-acquisition checkpoint
- runtime advances its session only after sink success
- sink failure causes the next cycle to reacquire the same cursor
- continuity recovery preserves the old durable checkpoint as expected state
- the real HTTP runtime, durable sink, and in-memory store recover from a first commit failure and successfully commit the reacquired batch

The FactoryConnect test suite after FC-020 contains:

```text
Total Tests: 167
Passed: 167
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

`git diff --check` also completes without errors.

---

## Scope Boundary

FC-020 implements:

- protocol-neutral observation-stream identity
- checkpoint and sequenced-observation contracts
- immutable atomic ingestion batches
- expected-checkpoint optimistic concurrency
- explicit Agent-instance checkpoint transitions
- idempotent replay rules
- atomic conflict rejection
- empty-batch checkpoint advancement
- in-memory ingestion store
- canonical MTConnect stream keys
- MTConnect durable sink translation
- two-phase acquisition-session advancement
- commit-before-cursor-advance runtime ordering
- same-cursor reacquisition after commit failure
- Edge worker composition through the ingestion store

FC-020 does not implement:

- SQL Server persistence
- process-restart durability
- database schema or migrations
- startup checkpoint restoration
- distributed locking
- runtime leader election
- observation query APIs
- retention or archival policy
- downstream processing transactions
- end-to-end exactly-once delivery
- durable continuity-loss reporting
- multi-machine supervision

These concerns belong to later persistence, startup composition, supervision, and processing slices.

---

## Result

FC-020 turns a successful sink handoff into a precise state transition.

```text
acquired batch from X
        ↓
validate expected durable state X
        ↓
atomically store observations + checkpoint Y
        │
        ├── rejected or failed
        │       ↓
        │   keep session at X
        │       ↓
        │   reacquire from X
        │
        └── committed
                ↓
            advance session to Y
                ↓
            acknowledge cycle success
```

FactoryConnect now has a stable ingestion contract that a later SQL Server implementation can preserve without redesigning delivery semantics.
