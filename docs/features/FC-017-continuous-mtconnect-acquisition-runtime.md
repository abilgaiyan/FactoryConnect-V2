# Continuous MTConnect Acquisition Runtime

## Purpose

FC-017 introduces continuous MTConnect acquisition as an Edge runtime responsibility.

FC-014 established stateless `/sample` acquisition.

FC-015 added stateful cursor continuation and `InstanceId` continuity.

FC-016 preserved structured MTConnect errors without deciding recovery policy.

FC-017 composes those protocol capabilities into a continuously hosted acquisition loop.

```text
FactoryConnect Edge host
        ↓
FactoryConnectWorker
        ↓
MtConnectAcquisitionRuntime
        ↓
MtConnectAcquisitionSession
        ↓
MtConnectSampleClient
        ↓
MTConnect /sample
```

> FC-017 owns when acquisition occurs and how it continues until shutdown.

> It does not move scheduling, hosting, or runtime policy into the MTConnect protocol package.

---

## Architectural Transition

FC-017 is the point where MTConnect acquisition moves out of protocol construction and into runtime operation.

Before FC-017, the MTConnect package could perform one request and preserve continuity across explicit calls.

```text
Caller
   ↓
MtConnectAcquisitionSession.AcquireNextAsync()
   ↓
one acquisition
```

The caller still had to decide:

- when to acquire
- how long to wait
- when to acquire again
- how cancellation stops execution
- where successful batches are handed off

FC-017 assigns those responsibilities to `FactoryConnect.Edge`.

```text
FactoryConnect.Protocols.MTConnect
        │
        ├── HTTP communication
        ├── XML parsing
        ├── protocol error semantics
        ├── sequence cursor
        └── InstanceId continuity

FactoryConnect.Edge
        │
        ├── continuous execution
        ├── polling interval
        ├── host cancellation
        ├── runtime configuration
        ├── runtime logging
        └── successful-batch handoff
```

The protocol layer continues to report facts.

The Edge runtime controls continuous operation.

---

## Runtime Model

The continuous acquisition boundary consists of four Edge components.

```text
FactoryConnectWorker
        ↓
IMtConnectAcquisitionRuntime
        ↓
MtConnectAcquisitionRuntime
        ↓
IMtConnectObservationSink
```

### FactoryConnectWorker

`FactoryConnectWorker` is the hosting adapter.

It receives the host shutdown token and delegates execution to the acquisition runtime.

```text
BackgroundService.ExecuteAsync()
        ↓
runtime.RunAsync(stoppingToken)
```

The worker does not contain:

- an acquisition loop
- MTConnect request logic
- cursor state
- polling calculations
- error classification
- persistence logic

Keeping the worker thin allows continuous acquisition to be tested independently of `BackgroundService`.

### IMtConnectAcquisitionRuntime

`IMtConnectAcquisitionRuntime` defines the hosted runtime boundary.

```csharp
Task RunAsync(
    CancellationToken cancellationToken = default);
```

The worker depends on this contract rather than the concrete runtime.

This makes host delegation independently verifiable and prevents hosting concerns from becoming embedded in acquisition behavior.

### MtConnectAcquisitionRuntime

`MtConnectAcquisitionRuntime` owns the continuous acquisition loop.

It composes:

- one `MtConnectAcquisitionSession`
- one `MtConnectEndpoint`
- one `MachineId`
- one MTConnect device key
- one observation sink
- one polling interval

It exposes two execution operations.

```text
RunCycleAsync()
        =
one acquisition and one successful-batch handoff


RunAsync()
        =
repeat acquisition cycles until cancellation
```

### IMtConnectObservationSink

`IMtConnectObservationSink` defines the successful-batch handoff boundary.

```csharp
ValueTask WriteAsync(
    MtConnectSampleResult result,
    CancellationToken cancellationToken = default);
```

The runtime does not decide how observations are ultimately stored or processed.

FC-017 supplies `LoggingMtConnectObservationSink` as the first temporary sink implementation.

---

## Single Acquisition Cycle

`RunCycleAsync()` represents one complete runtime acquisition cycle.

```text
MtConnectAcquisitionRuntime
        ↓
MtConnectAcquisitionSession.AcquireNextAsync()
        ↓
MtConnectSampleResult
        ↓
IMtConnectObservationSink.WriteAsync()
        ↓
return successful result
```

A cycle has two required stages:

1. acquire the next MTConnect sample batch
2. hand the successful batch to the sink

The sink is not invoked when acquisition fails.

```text
acquisition succeeds
        ↓
sink receives batch


acquisition fails
        ↓
sink is not invoked
```

`RunCycleAsync()` also returns the acquired result.

This supports deterministic verification and allows later runtime composition without duplicating the acquisition operation.

---

## Continuous Execution

`RunAsync()` repeatedly performs acquisition cycles.

```text
start
  ↓
acquire and hand off batch
  ↓
cancellation requested?
  ├── yes → stop
  └── no
       ↓
     wait polling interval
       ↓
     next cycle
```

The runtime uses the same `MtConnectAcquisitionSession` for every cycle.

Therefore each successful acquisition continues from the previous response's `NextSequence`.

```text
configured FromSequence = 101
        ↓
GET /sample?from=101
        ↓
result NextSequence = 111
        ↓
session cursor = 111
        ↓
wait polling interval
        ↓
GET /sample?from=111
```

The Edge runtime does not calculate sequence numbers.

Cursor advancement remains the responsibility of `MtConnectAcquisitionSession`.

---

## Polling Interval

FC-017 introduces an explicit polling interval.

```text
successful cycle
        ↓
PollingInterval
        ↓
next cycle
```

The interval must be greater than zero.

A zero or negative interval is rejected during runtime configuration.

The interval separates completed acquisition cycles.

It is not:

- an HTTP timeout
- a retry delay
- a failure backoff
- a scheduling guarantee
- a measurement timestamp

The actual time between request starts includes both acquisition duration and the configured interval.

Conceptually:

```text
request duration
        +
polling interval
        =
approximate time between request starts
```

---

## Cancellation Semantics

The host shutdown token flows through every FC-017 runtime layer.

```text
Host shutdown
        ↓
FactoryConnectWorker
        ↓
MtConnectAcquisitionRuntime
        ├── acquisition
        ├── sink handoff
        └── polling delay
```

FC-017 handles cancellation in three positions.

### Before the Next Cycle

If cancellation has already been requested, the runtime does not begin another acquisition.

```text
cancellation requested
        ↓
no next cycle
```

### During Acquisition or Sink Handoff

Cancellation is propagated through the active operation.

The runtime does not reinterpret cancellation as an acquisition failure or retry it.

```text
cancellation during active work
        ↓
OperationCanceledException
        ↓
propagate
```

### During the Polling Interval

Cancellation interrupts the polling delay and ends continuous execution cleanly.

```text
polling delay
        ↓
host cancellation
        ↓
stop runtime
```

This allows the Edge process to shut down without waiting for the full polling interval.

---

## Configuration

FC-017 introduces `MtConnectAcquisitionOptions`.

```text
MtConnectAcquisitionOptions
        │
        ├── Endpoint
        ├── MachineId
        ├── DeviceKey
        ├── FromSequence
        └── PollingInterval
```

The initial Edge configuration is loaded from the `MTConnect` section.

```json
{
  "MTConnect": {
    "BaseUri": "http://localhost:5000",
    "MachineId": "11111111-1111-1111-1111-111111111111",
    "DeviceKey": "CNC-01",
    "FromSequence": "1",
    "PollingInterval": "00:00:01"
  }
}
```

These values are development placeholders.

Production values must come from machine and site configuration.

The configured `FromSequence` establishes the initial in-memory session cursor.

FC-017 does not persist or restore that cursor.

---

## Runtime Composition

The Edge host composes one continuous MTConnect acquisition pipeline.

```text
Configuration
      ↓
MtConnectAcquisitionOptions
      ↓
HttpClient
      ↓
MtConnectSampleClient
      ↓
MtConnectAcquisitionSession
      ↓
MtConnectAcquisitionRuntime
      ↓
FactoryConnectWorker
```

The first runtime composition represents one machine and one MTConnect device stream.

FC-017 deliberately does not introduce multi-machine supervision.

That requires a later runtime layer to manage multiple independently configured acquisition sessions.

---

## Observation Handoff

FC-017 introduces a sink boundary rather than writing persistence directly into the runtime.

```text
MtConnectSampleResult
        ↓
IMtConnectObservationSink
        ↓
current: logging
future: observation pipeline / persistence
```

`LoggingMtConnectObservationSink` records:

- MTConnect Agent `InstanceId`
- returned `NextSequence`
- observation count

The logging sink proves the successful-batch handoff without prematurely selecting a durable storage design.

It does not persist the observations.

---

## Delivery Semantics

FC-017 provides in-process handoff, not durable delivery.

The acquisition session advances its cursor after a successful MTConnect acquisition and before the Edge sink completes.

```text
successful MTConnect response
        ↓
session cursor advances
        ↓
sink receives result
```

If the sink fails:

```text
sink failure
        ↓
runtime failure
        ↓
no automatic retry
```

FC-017 does not claim:

- transactional cursor and observation persistence
- at-least-once delivery
- exactly-once delivery
- durable checkpointing
- automatic replay after sink failure

This is acceptable for the FC-017 runtime boundary because sink failures terminate the current runtime execution and no retry policy is implemented.

A future persistence slice must explicitly coordinate observation storage with cursor checkpointing before durable delivery guarantees can be defined.

---

## Failure Semantics

FC-017 does not catch acquisition failures in order to continue automatically.

The established failure classifications remain visible to the host.

```text
MtConnectProtocolException
        ↓
runtime stops


HttpRequestException
        ↓
runtime stops


InvalidDataException
        ↓
runtime stops


InstanceId continuity failure
        ↓
runtime stops
```

This behavior is deliberate.

FC-017 establishes continuous scheduling but does not introduce resilience policy.

The runtime must not silently:

- retry malformed protocol data
- reset an out-of-range cursor
- ignore an Agent restart
- suppress configuration errors
- continue after a failed sink handoff

Those decisions require explicit recovery policy in later slices.

---

## Relationship to FC-014 Through FC-016

The MTConnect acquisition progression now has five distinct layers.

```text
FC-013
/current
latest observation snapshot
        ↓
FC-014
/sample
one stateless sequence-aware request
        ↓
FC-015
acquisition session
stateful cursor continuation
        ↓
FC-016
error and continuity semantics
structured protocol failures
        ↓
FC-017
continuous Edge runtime
hosted polling and successful-batch handoff
```

Each slice answers a different question.

| Slice | Question |
|---|---|
| FC-013 | What is the latest known machine state? |
| FC-014 | What observations are available from this sequence? |
| FC-015 | Where should the next acquisition continue? |
| FC-016 | What protocol failure occurred? |
| FC-017 | When should acquisition run again, and how does it stop? |

---

## Responsibility Boundary

FC-017 preserves a strict dependency direction.

```text
FactoryConnect.Edge
        ↓
FactoryConnect.Protocols.MTConnect
        ↓
FactoryConnect.Abstractions
```

The MTConnect protocol project does not reference the Edge project.

Protocol contracts remain usable without:

- `BackgroundService`
- dependency-injection composition
- polling loops
- runtime logging
- hosting configuration

This keeps the protocol package reusable and independently testable.

---

## Validation

FC-017 verifies:

- a single cycle uses the configured initial sequence
- a successful batch is handed to the sink
- a successful cycle returns its sample result
- continuous execution uses the session's returned `NextSequence`
- cancellation stops continuous execution
- acquisition failure does not invoke the sink
- acquisition cancellation is propagated
- a non-positive polling interval is rejected
- runtime options preserve configured values
- empty machine identifiers are rejected
- the worker delegates execution to the runtime
- worker shutdown reaches the runtime cancellation token
- asynchronous worker startup is synchronized deterministically in tests

The FactoryConnect test suite after FC-017 contains:

```text
Total Tests: 113
Passed: 113
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

`git diff --check` also completes without errors.

---

## Scope Boundary

FC-017 implements:

- continuous MTConnect acquisition in `FactoryConnect.Edge`
- one-cycle acquisition execution
- continuous polling
- configurable polling interval
- host cancellation propagation
- clean cancellation during the polling interval
- acquisition and sink cancellation propagation
- runtime configuration
- one-machine runtime composition
- successful-batch sink abstraction
- temporary batch logging
- thin `BackgroundService` hosting
- a dedicated Edge test project

FC-017 does not implement:

- automatic retries
- retry backoff
- transient-failure classification
- `OUT_OF_RANGE` recovery
- Agent restart recovery
- automatic session replacement
- sequence-gap recovery
- reconnect orchestration
- cursor persistence
- session restoration
- durable observation persistence
- transactional cursor checkpointing
- delivery guarantees
- multiple-machine supervision
- canonical signal mapping
- machine-state derivation
- metric calculation
- admin/setup UI

These concerns belong to later acquisition, persistence, and runtime slices.

---

## Result

FC-017 turns the existing MTConnect acquisition primitives into a continuously hosted Edge capability.

```text
Edge host starts
        ↓
FactoryConnectWorker
        ↓
MtConnectAcquisitionRuntime
        ↓
AcquireNextAsync
        ↓
MtConnectSampleResult
        ↓
observation sink
        ↓
polling interval
        ↓
next sequence-aware acquisition
        ↓
continue until shutdown
```

The final responsibility model is:

```text
MtConnectSampleClient
        =
one protocol request


MtConnectAcquisitionSession
        =
cursor and InstanceId continuity


MtConnectAcquisitionRuntime
        =
continuous scheduling and cancellation


IMtConnectObservationSink
        =
successful-batch handoff


FactoryConnectWorker
        =
host lifecycle adapter
```

FactoryConnect can now continuously acquire sequence-aware MTConnect observations without placing runtime orchestration inside the protocol package.

The next acquisition slices can add resilience, recovery, cursor persistence, and multi-machine supervision on top of this boundary without changing the protocol contracts established by FC-014 through FC-016.
