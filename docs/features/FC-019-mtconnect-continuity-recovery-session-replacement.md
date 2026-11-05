# MTConnect Continuity Recovery & Session Replacement

## Purpose

FC-019 adds explicit recovery for MTConnect sequence and Agent-instance discontinuity.

FC-015 established sequence and `InstanceId` continuity.

FC-016 exposed structured MTConnect protocol errors.

FC-017 introduced continuous Edge acquisition.

FC-018 added bounded transient retry and backoff.

FC-019 handles failures that waiting and retrying cannot solve:

- the requested sequence is no longer available
- the MTConnect Agent has restarted and changed `InstanceId`

```text
continuous acquisition
        ↓
continuity failure
        ↓
record continuity loss
        ↓
select earliest available sequence
        ↓
replace acquisition session
        ↓
resume acquisition
```

> FC-019 recovers continuity explicitly.

> It does not hide data loss or treat continuity failure as a transient transport failure.

---

## Continuity Failure Model

MTConnect sequence numbers belong to one Agent instance and its retained observation buffer.

Two conditions invalidate an existing acquisition session.

### Sequence Outside the Buffer

```text
session NextSequence = 101
        ↓
Agent firstSequence = 500
        ↓
GET /sample?from=101
        ↓
OUT_OF_RANGE
```

The old cursor cannot be recovered through another request for sequence 101.

The runtime must discover the Agent's current sequence window and create a new session.

### Agent Instance Changed

```text
session InstanceId = 42
        ↓
sample response InstanceId = 43
        ↓
Agent restart detected
```

Sequence values from instance 42 do not establish continuity with instance 43.

The runtime must record the discontinuity and replace the old session.

---

## Architectural Boundary

FC-019 separates protocol facts from Edge recovery policy.

```text
FactoryConnect.Protocols.MTConnect
        │
        ├── preserve /current header metadata
        ├── detect InstanceId changes
        ├── expose typed discontinuity facts
        └── preserve failed session state

FactoryConnect.Edge
        │
        ├── classify recoverable continuity failures
        ├── choose recovery sequence
        ├── report continuity loss
        ├── create replacement sessions
        └── bound recovery attempts per cycle
```

The protocol layer does not decide that a cursor should be reset.

The Edge runtime applies that policy explicitly.

---

## Current Metadata Result

Before FC-019, `MtConnectCurrentClient.AcquireAsync()` returned only current observations.

The MTConnect `/current` header already contained sequence-window metadata, but that information was discarded.

FC-019 introduces `MtConnectCurrentResult`.

```text
MtConnectCurrentResult
        │
        ├── InstanceId
        ├── FirstSequence
        ├── LastSequence
        ├── NextSequence
        └── Observations
```

The richer result is available through:

```csharp
Task<MtConnectCurrentResult> AcquireResultAsync(...)
```

The existing observation-only API remains available:

```csharp
Task<IReadOnlyList<MachineObservation>> AcquireAsync(...)
```

This preserves FC-013 compatibility while providing the metadata required for recovery.

---

## Current Header Validation

`AcquireResultAsync()` requires a valid MTConnect streams header.

Required values are:

- `instanceId`
- `firstSequence`
- `lastSequence`
- `nextSequence`

Missing or malformed values raise `InvalidDataException`.

```text
/current document
        ↓
Header validation
        │
        ├── valid → MtConnectCurrentResult
        └── invalid → InvalidDataException
```

Recovery never invents a cursor when the Agent does not provide valid sequence metadata.

---

## Current Structured-Error Consistency

Both `MtConnectCurrentClient` operations now preserve the same non-success response semantics established for `/sample`.

```text
/current non-success
        ↓
read response body
        ↓
valid MTConnectError
        → MtConnectProtocolException

recognized but malformed MTConnectError
        → InvalidDataException

ordinary non-MTConnect response
        → HttpRequestException
```

This applies to both:

- observation-only `AcquireAsync()`
- metadata-rich `AcquireResultAsync()`

Continuity recovery therefore receives structured `OUT_OF_RANGE` facts instead of a generic HTTP failure.

The malformed-error behavior is verified explicitly at the current-client boundary rather than relying only on parser-level tests.

---

## Typed Instance Discontinuity

FC-015 originally raised a generic `InvalidOperationException` when the Agent `InstanceId` changed.

FC-019 introduces `MtConnectInstanceChangedException`.

```text
MtConnectInstanceChangedException
        │
        ├── PreviousInstanceId
        ├── CurrentInstanceId
        └── FirstSequence
```

The exception derives from `InvalidOperationException`, preserving the earlier semantic category while exposing structured recovery data.

When a changed instance is detected:

```text
sample response parsed
        ↓
compare response InstanceId
with session InstanceId
        ↓
different
        ↓
throw typed exception
        ↓
do not commit session state
```

The old session retains its previous `InstanceId` and `NextSequence`.

---

## Session Factory

FC-019 introduces `IMtConnectAcquisitionSessionFactory`.

```csharp
MtConnectAcquisitionSession Create(
    ulong fromSequence);
```

`MtConnectAcquisitionSessionFactory` creates a fresh session using the existing `MtConnectSampleClient`.

A recovery never mutates the identity of the failed session into a different Agent instance.

Instead:

```text
failed session
        ↓
recovery sequence
        ↓
session factory
        ↓
new session
```

This makes session replacement explicit and testable.

---

## Continuity Loss Record

FC-019 introduces `MtConnectContinuityLoss`.

```text
MtConnectContinuityLoss
        │
        ├── MachineId
        ├── Reason
        ├── PreviousInstanceId
        ├── CurrentInstanceId
        ├── PreviousSequence
        └── RecoverySequence
```

Supported reasons are:

```text
OutOfRange
InstanceChanged
```

A continuity loss records both the abandoned position and the selected recovery position.

It is not represented only as a log message.

The contract can later support persistence, diagnostics, alarms, and operational reporting.

---

## Continuity Reporter

`IMtConnectContinuityReporter` defines the reporting boundary.

```csharp
ValueTask ReportAsync(
    MtConnectContinuityLoss continuityLoss,
    CancellationToken cancellationToken = default);
```

FC-019 provides `LoggingMtConnectContinuityReporter`.

The structured warning includes:

- machine identifier
- recovery reason
- previous Agent instance, when known
- current Agent instance
- previous sequence
- recovery sequence

Numeric instance formatting uses invariant culture.

FC-019 logs continuity loss but does not yet persist it durably.

---

## OUT_OF_RANGE Recovery

An `OUT_OF_RANGE` response means the requested session cursor is outside the Agent's retained buffer.

```text
MtConnectProtocolException
        ↓
contains OUT_OF_RANGE?
        │
        ├── no → propagate
        └── yes
              ↓
        acquire /current metadata
              ↓
        select FirstSequence
              ↓
        report continuity loss
              ↓
        create replacement session
```

The check is case-insensitive and examines all structured MTConnect errors in the response.

Other MTConnect protocol errors remain unrecoverable in FC-019.

---

## Why Recovery Uses FirstSequence

FC-019 resumes from `FirstSequence`, not `NextSequence`.

```text
Agent buffer

FirstSequence                         NextSequence
     │                                     │
     ▼                                     ▼
   500  501  502  ...  598  599  600      601
```

Starting at 500 requests the earliest observations still available.

Starting at 601 would skip the entire retained buffer.

Therefore:

```text
recovery cursor = current.FirstSequence
```

Some observations before `FirstSequence` have already been lost.

FC-019 records that discontinuity rather than claiming seamless continuation.

---

## Transient Failure During Recovery Discovery

`OUT_OF_RANGE` recovery calls `/current`.

That HTTP operation can itself encounter transient failure.

FC-019 executes current-metadata acquisition through the FC-018 transient retry policy.

```text
OUT_OF_RANGE
        ↓
GET /current
        │
        ├── transient failure
        │       ↓
        │   retry + backoff + jitter
        │
        └── success
                ↓
          recovery metadata
```

Structured or malformed current data is not blindly retried as transport failure.

---

## Agent Instance Change Recovery

When `MtConnectAcquisitionSession` receives a sample from a different Agent instance, the parsed sample header already provides the earliest available sequence.

```text
session InstanceId = 42
        ↓
sample response
InstanceId = 43
FirstSequence = 500
        ↓
MtConnectInstanceChangedException
        ↓
report continuity loss
        ↓
create replacement session from 500
```

No additional `/current` request is required.

The failed response is not handed to the observation sink because continuity validation did not succeed.

The replacement session reacquires from the new instance's earliest available sequence.

---

## Runtime Recovery Loop

`MtConnectAcquisitionRuntime` now owns a replaceable session.

A cycle performs:

```text
acquire with transient retry
        │
        ├── success
        │       ↓
        │   sink handoff
        │
        ├── OUT_OF_RANGE
        │       ↓
        │   replace session
        │       ↓
        │   acquire again
        │
        └── InstanceId changed
                ↓
            replace session
                ↓
            acquire again
```

The observation sink remains outside continuity recovery.

Only a continuity-valid successful result is handed off.

---

## One Replacement Per Cycle

FC-019 permits at most one session replacement in one acquisition cycle.

```text
first continuity failure
        ↓
replace session
        ↓
retry acquisition
        │
        ├── success → continue
        └── second continuity failure → propagate
```

This prevents an unbounded recovery loop when:

- the Agent buffer moves faster than recovery
- the Agent repeatedly restarts
- configuration selects the wrong device
- a server continually returns inconsistent metadata

A later outer runtime or operational policy may decide when to try again.

FC-019 does not silently loop forever.

---

## State Transition Safety

Recovery follows an ordered transition.

```text
continuity failure
        ↓
resolve recovery metadata
        ↓
construct continuity-loss record
        ↓
report continuity loss
        ↓
create replacement session
        ↓
assign replacement to runtime
        ↓
reacquire
```

If metadata acquisition fails, reporting fails, or cancellation occurs before replacement is returned, the runtime does not assign a partially constructed replacement session.

---

## Cancellation

The host cancellation token flows through:

- failed acquisition handling
- transient retry during `/current`
- current-document parsing
- continuity reporting
- replacement acquisition

Cancellation is not treated as continuity loss.

```text
cancellation
        ↓
stop active recovery
        ↓
propagate cancellation
```

---

## Failure Classification

| Failure | FC-019 behavior |
|---|---|
| Structured `OUT_OF_RANGE` | Recover once |
| Agent `InstanceId` changed | Recover once |
| Second continuity failure in same cycle | Propagate |
| Other MTConnect protocol error | Propagate |
| Malformed `/current` header | Propagate |
| Transient failure during `/current` | FC-018 retry policy |
| Recovery reporting failure | Propagate |
| Observation sink failure | Propagate without recovery |
| Cancellation | Propagate immediately |

---

## Runtime Composition

The Edge composition now includes:

```text
MtConnectSampleClient
        ↓
IMtConnectAcquisitionSessionFactory
        ↓
initial and replacement sessions

MtConnectCurrentClient
        ↓
MtConnectContinuityRecoveryPolicy

IMtConnectContinuityReporter
        ↓
LoggingMtConnectContinuityReporter

MtConnectTransientRetryPolicy
        ↓
MtConnectContinuityRecoveryPolicy
        ↓
MtConnectAcquisitionRuntime
```

The initial session is created through the same factory used for replacement sessions.

---

## Validation

FC-019 verifies:

- `/current` header metadata is preserved
- observation-only current acquisition remains compatible
- missing current headers are rejected
- missing sequence metadata is rejected
- current acquisition preserves structured MTConnect protocol errors
- both current APIs preserve MTConnect error codes
- malformed current error documents raise `InvalidDataException`
- ordinary current HTTP failures remain `HttpRequestException`
- private current parsing uses a concrete observation array
- changed Agent instances raise a typed exception
- typed exceptions preserve old and new instance identifiers
- typed exceptions preserve `FirstSequence`
- failed instance transitions preserve existing session state
- session factory creates sessions at explicit cursors
- `OUT_OF_RANGE` selects current `FirstSequence`
- recovery current acquisition uses transient retry
- non-`OUT_OF_RANGE` protocol errors are rejected
- instance-change recovery uses exception metadata
- continuity-loss records preserve machine and cursor facts
- runtime follows `sample → current → sample from FirstSequence`
- runtime reacquires after Agent instance replacement
- recovered results reach the observation sink once
- only one replacement is permitted per cycle
- structured continuity logging formats instance values invariantly

The FactoryConnect test suite after FC-019 contains:

```text
Total Tests: 147
Passed: 147
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

`git diff --check` also completes without errors.

---

## Scope Boundary

FC-019 implements:

- richer current-result metadata
- backward-compatible observation-only current acquisition
- typed Agent-instance discontinuity
- explicit acquisition-session factory
- continuity-loss model
- continuity-loss reporting contract
- structured continuity warning logging
- `OUT_OF_RANGE` classification
- `OUT_OF_RANGE` recovery through `/current`
- recovery from the earliest retained sequence
- transient retry during recovery discovery
- Agent restart recovery
- runtime session replacement
- one replacement per acquisition cycle
- continuity-valid sink handoff

FC-019 does not implement:

- durable continuity-loss persistence
- cursor checkpoint persistence
- session restoration after process restart
- transactional observation and cursor storage
- at-least-once or exactly-once delivery
- automatic long-running recovery supervision
- circuit breaking
- recovery cooldown
- multiple-machine supervision
- machine health-state persistence
- canonical signal mapping
- machine-state derivation
- metric calculation
- admin/setup UI

These concerns belong to later persistence, supervision, and processing slices.

---

## Result

FC-019 allows FactoryConnect to resume after MTConnect buffer discontinuity and Agent restart without pretending continuity was preserved.

```text
existing session
        ↓
incremental acquisition
        │
        ├── transient failure
        │       ↓
        │   FC-018 retry
        │
        ├── OUT_OF_RANGE
        │       ↓
        │   /current metadata
        │       ↓
        │   record loss
        │       ↓
        │   replace from FirstSequence
        │
        ├── InstanceId changed
        │       ↓
        │   typed discontinuity
        │       ↓
        │   record loss
        │       ↓
        │   replace from FirstSequence
        │
        └── continuity-valid success
                ↓
             sink once
```

The MTConnect acquisition stack now has distinct responsibilities:

```text
MtConnectSampleClient
        =
one sequence-aware protocol request


MtConnectAcquisitionSession
        =
cursor and InstanceId continuity validation


MtConnectTransientRetryPolicy
        =
bounded transient retry and backoff


MtConnectContinuityRecoveryPolicy
        =
explicit discontinuity recovery


MtConnectAcquisitionRuntime
        =
continuous execution and session replacement


IMtConnectContinuityReporter
        =
continuity-loss handoff
```

FactoryConnect now distinguishes between:

- retryable transient failure
- recoverable continuity loss
- unrecoverable protocol failure
- successful continuous acquisition

without moving runtime recovery policy into the MTConnect protocol package.
