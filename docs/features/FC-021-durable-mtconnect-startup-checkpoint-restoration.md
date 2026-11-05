# Durable MTConnect Startup Checkpoint Restoration

## Purpose

FC-021 restores the last durable MTConnect acquisition position when the Edge host starts.

FC-020 established the atomic ingestion boundary:

```text
observations + InstanceId / NextSequence
        ↓
commit atomically
        ↓
advance the in-memory session
```

FC-021 closes the process-lifecycle gap:

```text
Edge host starts
        ↓
resolve canonical observation stream
        ↓
read durable checkpoint
        ↓
restore InstanceId + NextSequence
        ↓
create acquisition runtime
        ↓
begin continuous acquisition
```

> A durable checkpoint is not only a sequence cursor. It is the continuity identity of the MTConnect Agent instance from which that cursor originated.

---

## Failure Being Prevented

Without startup restoration, every Edge restart begins from the configured bootstrap sequence even when a newer checkpoint already exists.

```text
durable checkpoint = instance 42 / next 500
        ↓
Edge process restarts
        ↓
configured bootstrap = 101
        ↓
request /sample?from=101
        ↓
replay old data or encounter OUT_OF_RANGE
```

Restoring only `NextSequence` is also incomplete.

```text
durable checkpoint = instance 42 / next 500
        ↓
session = instance null / next 500
        ↓
Agent restarted offline
        ↓
/sample?from=500 returns instance 43
        ↓
session accepts 43 as its first observed instance
```

That would bypass FC-019 instance-change recovery on the first request after startup.

FC-021 therefore restores both:

- `InstanceId`
- `NextSequence`

---

## Architectural Boundary

Startup checkpoint restoration belongs to the Edge lifecycle, not the protocol client and not the ingestion store.

```text
FactoryConnect.Infrastructure
        │
        └── read durable checkpoint
                ↓
FactoryConnect.Edge
        │
        ├── resolve stream identity
        ├── choose bootstrap or restore
        ├── create acquisition session
        └── compose runtime
                ↓
FactoryConnect.Protocols.MTConnect
        │
        └── acquire from restored session state
```

The ingestion store remains protocol-neutral. It returns an `ObservationCheckpoint` for an `ObservationStreamId`.

The Edge layer interprets that checkpoint as MTConnect startup state because it owns the canonical MTConnect stream identity and runtime composition.

---

## Startup State

`MtConnectStartupState` represents the decision produced at startup.

It contains:

```text
MtConnectStartupState
        │
        ├── FromSequence
        └── Checkpoint?
```

The two values remain consistent:

| Startup condition | FromSequence | Checkpoint |
|---|---:|---|
| No durable checkpoint | Configured bootstrap sequence | `null` |
| Durable checkpoint exists | Durable `NextSequence` | Durable checkpoint |

The original checkpoint is preserved rather than reduced to a sequence number. The runtime needs it as the expected state for the first FC-020 atomic commit.

---

## Canonical Stream Resolution

`MtConnectStartupCheckpointResolver` constructs the stream identity through `MtConnectObservationStreamId.Create()`.

```text
MachineId + configured DeviceKey
        ↓
canonical MTConnect ObservationStreamId
        ↓
IObservationIngestionStore.ReadCheckpointAsync
```

This ensures startup lookup uses the same normalization as the durable sink.

For example, device-key whitespace and casing cannot make startup restoration read from a different stream than ingestion writes to.

A checkpoint returned for another stream is rejected rather than silently used.

---

## Bootstrap Startup

When no checkpoint exists, the resolver uses the configured bootstrap sequence.

```text
checkpoint lookup = null
configured FromSequence = 101
        ↓
sessionFactory.Create(101)
        ↓
session state
InstanceId   = null
NextSequence = 101
        ↓
runtime initial checkpoint = null
```

The first successful acquisition establishes the Agent `InstanceId`.

The first durable sink commit uses:

```text
ExpectedCheckpoint = null
Checkpoint         = acquired InstanceId / NextSequence
```

This preserves FC-020 initial-creation concurrency semantics.

---

## Restored Startup

When a checkpoint exists, it takes precedence over the configured bootstrap sequence.

```text
configured FromSequence = 101
durable checkpoint      = instance 42 / next 500
        ↓
sessionFactory.Restore(42, 500)
        ↓
session state
InstanceId   = 42
NextSequence = 500
        ↓
runtime initial checkpoint = 42 / 500
```

The session sends its first request to:

```text
/sample?from=500
```

The runtime passes the restored checkpoint to the sink as the expected state for the first post-restart commit.

---

## Explicit Session Lifecycle

`IMtConnectAcquisitionSessionFactory` exposes two distinct operations:

```csharp
MtConnectAcquisitionSession Create(
    ulong fromSequence);

MtConnectAcquisitionSession Restore(
    ulong instanceId,
    ulong nextSequence);
```

`Create` starts a session whose Agent identity is not yet known.

`Restore` reconstructs a session whose Agent identity and next cursor were previously committed together.

This explicit API prevents restored continuity state from being accidentally represented as an ordinary first acquisition.

---

## Same-Instance Continuation

If the Agent still has the same `InstanceId`, acquisition continues normally.

```text
restored session = instance 42 / next 500
        ↓
GET /sample?from=500
        ↓
response instance = 42
        ↓
prepare result
        ↓
commit expected 42/500 → new 42/511
        ↓
advance session
```

No continuity recovery is reported because the durable and current Agent identities agree.

---

## Agent Restarted While Edge Was Offline

If the Agent restarted while the Edge process was not running, the restored session detects the change on its first response.

```text
restored session = instance 42 / next 500
        ↓
GET /sample?from=500
        ↓
response instance = 43
        ↓
MtConnectInstanceChangedException
        ↓
FC-019 continuity recovery
        ↓
replacement session from Agent firstSequence
        ↓
acquire instance 43
```

The old durable checkpoint remains the expected state:

```text
ExpectedCheckpoint = instance 42 / next 500
NewCheckpoint      = instance 43 / recovered NextSequence
```

The ingestion store can therefore commit the Agent-instance transition atomically while still rejecting a stale writer.

---

## Runtime Creation at Host Execution

Before FC-021, the host composed the acquisition session and runtime synchronously during dependency registration.

Checkpoint restoration requires asynchronous storage access, so the worker now depends on `IMtConnectAcquisitionRuntimeFactory`.

```text
host service registration
        ↓
FactoryConnectWorker.ExecuteAsync
        ↓
runtimeFactory.CreateAsync
        ↓
resolve durable startup state
        ↓
create or restore session
        ↓
construct runtime
        ↓
runtime.RunAsync
```

This avoids blocking dependency-injection factories and places asynchronous restoration at the actual hosted-service lifecycle boundary.

The runtime is created once per worker execution.

---

## Runtime Factory

`MtConnectAcquisitionRuntimeFactory` coordinates startup composition.

Its responsibilities are:

1. Resolve startup state from the ingestion store.
2. Create a bootstrap session or restore a durable session.
3. Pass the restored checkpoint into `MtConnectAcquisitionRuntime`.
4. Preserve the existing retry, continuity-recovery, sink, endpoint, machine, device, and polling configuration.

It does not read observations or attempt to reconstruct application state beyond the acquisition checkpoint.

---

## CAS Preservation

FC-021 preserves the optimistic-concurrency contract established by FC-020.

```text
durable checkpoint X
        ↓
startup resolver reads X
        ↓
runtime restores session from X
        ↓
acquire result Y
        ↓
sink commit
ExpectedCheckpoint = X
Checkpoint         = Y
```

The runtime does not replace the expected checkpoint with a fresh store read immediately before committing.

If another writer advances the durable stream after startup, the first commit from the restored runtime is rejected as stale.

---

## Failure and Cancellation Semantics

Startup failures are not converted into bootstrap startup.

If checkpoint lookup fails:

```text
store read fails
        ↓
runtime creation fails
        ↓
worker acquisition does not begin
```

Falling back to the configured sequence after a storage failure could replay or skip data while hiding an unavailable durability boundary.

Cancellation flows through checkpoint resolution and runtime creation. A cancelled startup does not create or run an acquisition session.

---

## Validation

FC-021 verifies:

- missing checkpoints use the configured bootstrap sequence
- durable checkpoints override the configured bootstrap sequence
- canonical MTConnect stream identity is used for lookup
- checkpoints from another stream are rejected
- checkpoint-read failures propagate
- cancellation propagates before runtime acquisition begins
- the worker asynchronously creates the runtime at execution time
- bootstrap sessions begin without an established `InstanceId`
- restored sessions receive both `InstanceId` and `NextSequence`
- the runtime receives the restored checkpoint as its initial CAS state
- same-instance responses continue from the durable cursor
- changed-instance responses raise continuity loss
- Agent restart while Edge was offline enters FC-019 recovery
- recovery requests the replacement Agent's `FirstSequence`
- the first recovered commit still expects the old durable checkpoint

The FactoryConnect test suite after FC-021 contains:

```text
Total Tests: 176
Passed: 176
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

`git diff --check` also completes without errors.

---

## Scope Boundary

FC-021 implements:

- durable checkpoint lookup during Edge startup
- canonical MTConnect stream resolution
- configured bootstrap fallback when no checkpoint exists
- asynchronous runtime creation at worker execution
- full session restoration from `InstanceId + NextSequence`
- restored runtime expected-checkpoint state
- same-instance continuation after process restart
- offline Agent-restart detection
- integration with FC-019 continuity recovery
- preservation of FC-020 optimistic concurrency semantics

FC-021 does not implement:

- SQL Server checkpoint persistence
- database schema or migrations
- distributed locks or leader election
- multi-machine runtime supervision
- retrying startup store failures
- checkpoint repair or administrative reset
- retention and archival policies
- durable continuity-loss reporting
- observation replay APIs
- downstream processing restoration

The current in-memory store exercises the lifecycle contract but does not survive a real process termination. A later persistent store will provide physical restart durability without changing the FC-021 startup semantics.

---

## Result

FC-021 makes process startup a continuation of the last committed acquisition state.

```text
last durable checkpoint
InstanceId + NextSequence
        ↓
restore session continuity identity
        ↓
acquire from the committed cursor
        │
        ├── same Agent instance
        │       ↓
        │   continue normally
        │
        └── changed Agent instance
                ↓
            FC-019 recovery
                ↓
            atomic old-instance → new-instance commit
```

FactoryConnect now has a complete logical path from durable ingestion state to safe MTConnect acquisition startup.
