# MTConnect Acquisition Session & Cursor Continuation

## Purpose

FC-015 introduces stateful MTConnect acquisition sessions for sequence-based continuation across multiple `/sample` requests.

FC-014 established a stateless acquisition primitive:

```text
/sample?from=N
      ↓
MtConnectSampleClient
      ↓
MtConnectSampleResult
```

Each FC-014 request acquires one sequence-aware batch but does not remember where acquisition should continue.

FC-015 adds that responsibility through `MtConnectAcquisitionSession`.

```text
Initial sequence
      ↓
MtConnectAcquisitionSession
      ↓
/sample?from=N
      ↓
MtConnectSampleResult
      ↓
NextSequence
      ↓
Session cursor
      ↓
next /sample request
```

> FC-015 owns sequence continuity across successful acquisitions.
>
> It does not own scheduling, polling intervals, retries, or persistence.

---

## Session Model

An acquisition session represents the continuation state for a sequence of MTConnect `/sample` requests.

```text
MtConnectAcquisitionSession
    │
    ├── InstanceId
    ├── NextSequence
    │
    └── AcquireNextAsync()
```

The session is initialized with an explicit starting sequence.

```text
fromSequence = 101
        ↓
MtConnectAcquisitionSession
        ↓
NextSequence = 101
```

Before the first successful acquisition:

```text
InstanceId   = null
NextSequence = 101
```

The session does not invent or discover its initial cursor.

The caller decides where acquisition begins.

---

## Cursor Ownership

FC-014 deliberately keeps `MtConnectSampleClient` stateless.

FC-015 introduces the layer that owns the continuation cursor.

```text
MtConnectSampleClient
    │
    └── one /sample request


MtConnectAcquisitionSession
    │
    └── state across /sample requests
```

For every acquisition, the session supplies its current `NextSequence` to the sample client.

```text
Session
NextSequence = 101
      │
      ▼
GET /sample?from=101
```

When the request succeeds and continuity validation passes, the session adopts the `NextSequence` returned by the MTConnect Agent.

```text
MtConnectSampleResult
NextSequence = 111
      │
      ▼
Session
NextSequence = 111
```

The next acquisition therefore becomes:

```text
GET /sample?from=111
```

The session never calculates the continuation cursor from individual observation sequences.

It uses the `NextSequence` supplied by the MTConnect response.

---

## Acquisition Lifecycle

A successful session progresses through a sequence of acquisition transitions.

```text
Initial session

InstanceId   = null
NextSequence = 101

        │
        ▼

GET /sample?from=101

        │
        ▼

Result

InstanceId   = 42
NextSequence = 111

        │
        ▼

Session

InstanceId   = 42
NextSequence = 111

        │
        ▼

GET /sample?from=111

        │
        ▼

Result

InstanceId   = 42
NextSequence = 121

        │
        ▼

Session

InstanceId   = 42
NextSequence = 121
```

This establishes incremental continuation without placing state inside the protocol client.

---

## State Transition Rule

FC-015 follows one central invariant:

> **Session state advances only after a successful acquisition with valid instance continuity.**

The transition order is:

```text
Acquire sample
      ↓
Parse response
      ↓
Validate InstanceId
      ↓
Commit session state
      ├── InstanceId
      └── NextSequence
```

State mutation occurs only after the preceding operations succeed.

This prevents failed acquisitions from corrupting the continuation cursor.

Conceptually:

```text
successful acquisition
        +
valid instance continuity
        ↓
advance session state


failure
cancellation
instance discontinuity
        ↓
preserve session state
```

---

## InstanceId Continuity

MTConnect sequence numbers belong to an Agent instance.

FC-014 preserves the Agent `InstanceId` in every `MtConnectSampleResult`.

FC-015 uses that value to establish continuity across requests.

The first successful acquisition establishes the session instance:

```text
Session before acquisition

InstanceId = null

        ↓

Result

InstanceId = 42

        ↓

Session

InstanceId = 42
```

Subsequent acquisitions must return the same `InstanceId`.

```text
Session

InstanceId = 42

        ↓

Result

InstanceId = 42

        ↓

continue
```

If the Agent returns a different instance:

```text
Session

InstanceId = 42

        ↓

Result

InstanceId = 43

        ↓

continuity failure
```

FC-015 rejects the transition.

The established session state remains unchanged.

```text
InstanceId   = 42
NextSequence = previous cursor
```

FC-015 detects the instance change but does not decide how acquisition should recover from it.

Agent restart and recovery policy belong to a later acquisition layer.

---

## Failure Semantics

Acquisition failures must not advance session state.

This applies both before and after the session has been established.

### Initial Acquisition Failure

```text
Session

InstanceId   = null
NextSequence = 101

        ↓

acquisition failure

        ↓

Session remains

InstanceId   = null
NextSequence = 101
```

The caller can retry from the original sequence.

### Subsequent Acquisition Failure

```text
Session

InstanceId   = 42
NextSequence = 111

        ↓

acquisition failure

        ↓

Session remains

InstanceId   = 42
NextSequence = 111
```

The failed request therefore does not cause observations to be skipped.

A later attempt can safely retry using the established cursor.

---

## Cancellation

FC-015 propagates cancellation through the existing MTConnect sample acquisition path.

```text
AcquireNextAsync
      ↓
MtConnectSampleClient
      ↓
HttpClient
```

If acquisition is cancelled before a successful state transition, session state remains unchanged.

```text
Cancellation
      ↓
no committed acquisition
      ↓
no cursor advancement
```

The session does not catch cancellation in order to retry or continue automatically.

Scheduling and retry behavior remain outside this slice.

---

## Relationship to FC-014

FC-014 and FC-015 deliberately have different responsibilities.

FC-014 owns one stateless `/sample` request:

```text
FC-014

MtConnectSampleClient
      ↓
one stateless /sample request
      ↓
MtConnectSampleResult
```

FC-015 composes that primitive into a stateful acquisition session:

```text
FC-015

MtConnectAcquisitionSession
      │
      ├── InstanceId
      ├── NextSequence
      │
      ▼
MtConnectSampleClient
      │
      ▼
/sample?from=N
```

`MtConnectSampleClient` remains unchanged and stateless.

This keeps protocol communication separate from acquisition-session state.

---

## Relationship to Machine Observations

FC-015 does not change the observation model introduced by earlier slices.

```text
MtConnectSampleResult
      │
      └── MtConnectSampleObservation
                │
                ├── Sequence
                │
                └── MachineObservation
```

`MachineObservation` remains protocol-neutral.

FC-015 only controls which MTConnect sequence is requested next.

It does not perform canonical signal mapping or machine-state derivation.

---

## Responsibility Boundary

The acquisition architecture now has three distinct responsibilities:

```text
MTConnect protocol request
        │
        ▼
MtConnectSampleClient
        │
        │ stateless
        ▼
MtConnectAcquisitionSession
        │
        │ stateful sequence continuation
        ▼
Future acquisition orchestration
        │
        │ scheduling / recovery / persistence
        ▼
Continuous operation
```

This separation prevents scheduling and recovery concerns from leaking into protocol communication.

The resulting responsibility model is:

```text
MtConnectSampleClient
        =
one MTConnect protocol request


MtConnectAcquisitionSession
        =
sequence continuity across requests


Future acquisition runtime
        =
when acquisition occurs
how failures are recovered
how state is persisted
```

---

## Validation

FC-015 verifies:

- the initial sequence becomes the session cursor
- the first acquisition uses the supplied sequence
- a successful acquisition establishes `InstanceId`
- a successful acquisition advances to the returned `NextSequence`
- the next acquisition uses the previous result's `NextSequence`
- the same `InstanceId` permits continuation
- a changed `InstanceId` rejects the state transition
- an initial acquisition failure does not advance state
- a subsequent acquisition failure preserves established state
- cancellation does not advance state

The FactoryConnect test suite after FC-015 contains:

```text
Total Tests: 87
Passed: 87
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

---

## Scope Boundary

FC-015 implements:

- stateful MTConnect acquisition sessions
- explicit initial sequence ownership
- continuation using `NextSequence`
- `InstanceId` establishment
- `InstanceId` continuity validation
- atomic session-state advancement after successful acquisition
- preservation of state after acquisition failure
- preservation of state after continuity failure
- cancellation propagation without cursor advancement

FC-015 does not implement:

- continuous polling loops
- polling intervals
- timers
- `BackgroundService`
- cursor persistence
- automatic session restoration
- retry policies
- retry backoff
- reconnect orchestration
- MTConnect error document parsing
- `OUT_OF_RANGE` recovery
- Agent restart recovery
- sequence-gap recovery
- automatic cursor reset
- canonical signal mapping
- machine-state derivation
- persistence
- admin/setup UI

These concerns belong to later acquisition and runtime slices.

---

## Result

FC-015 establishes the stateful continuation boundary required for reliable incremental MTConnect acquisition.

```text
Caller
   │
   │ initial sequence
   ▼
MtConnectAcquisitionSession
   │
   │ current NextSequence
   ▼
MtConnectSampleClient
   │
   ▼
MTConnect /sample
   │
   ▼
MtConnectSampleResult
   │
   ├── InstanceId
   ├── NextSequence
   └── Observations
   │
   ▼
continuity validation
   │
   ▼
commit session state
   │
   └──────────────► next acquisition
```

FactoryConnect now has a clear MTConnect acquisition progression:

```text
FC-013
/current
latest observation snapshot
        ↓
FC-014
/sample
stateless incremental acquisition
        ↓
FC-015
acquisition session
stateful cursor continuation
```

FC-013 answers:

> What is the latest known machine state?

FC-014 answers:

> What sequence-aware observations can be acquired from this cursor?

FC-015 answers:

> Where should the next incremental acquisition continue?

The next acquisition layer can build scheduling, MTConnect error handling, recovery, and eventually continuous operation on top of this session without changing the protocol acquisition boundary.