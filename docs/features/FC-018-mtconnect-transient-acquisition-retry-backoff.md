# MTConnect Transient Acquisition Retry & Backoff

## Purpose

FC-018 adds bounded retry and backoff behavior to continuous MTConnect acquisition.

FC-017 established continuous acquisition in `FactoryConnect.Edge`.

A transient network or server failure still terminated that runtime immediately.

FC-018 allows the current acquisition operation to recover from explicitly classified transient failures without changing the MTConnect protocol package or corrupting the acquisition cursor.

```text
MtConnectAcquisitionRuntime
        ↓
MtConnectTransientRetryPolicy
        ↓
MtConnectAcquisitionSession
        ↓
MtConnectSampleClient
        ↓
MTConnect /sample
```

> FC-018 retries transient acquisition failures.

> It does not recover protocol continuity, replace sessions, or persist cursors.

---

## Architectural Boundary

Retry is an Edge runtime policy.

It does not belong inside `MtConnectSampleClient`.

```text
FactoryConnect.Protocols.MTConnect
        │
        ├── issue one HTTP request
        ├── parse MTConnect documents
        ├── preserve protocol errors
        └── maintain session continuity facts

FactoryConnect.Edge
        │
        ├── classify retryable runtime failures
        ├── bound the number of attempts
        ├── calculate backoff
        ├── apply jitter
        ├── wait between attempts
        └── log scheduled retries
```

This keeps protocol communication deterministic and allows different hosting environments to apply different resilience policies.

---

## Retry Boundary

FC-018 wraps only MTConnect acquisition.

```text
retry policy
    │
    ▼
session.AcquireNextAsync()
    │
    ▼
successful result
    │
    ▼
observation sink
```

The sink is outside the retry boundary.

Conceptually:

```csharp
var result = await retryPolicy.ExecuteAsync(
    cancellationToken =>
        session.AcquireNextAsync(
            endpoint,
            machineId,
            deviceKey,
            cancellationToken));

await sink.WriteAsync(result, cancellationToken);
```

This distinction prevents a failed sink handoff from issuing another MTConnect request.

```text
acquisition failure
        ↓
may retry acquisition


sink failure
        ↓
propagate immediately
        ↓
do not reacquire
```

---

## Retry Configuration

FC-018 introduces `MtConnectRetryOptions`.

```text
MtConnectRetryOptions
        │
        ├── MaxAttempts
        ├── InitialDelay
        ├── MaximumDelay
        └── JitterRatio
```

The Edge host loads these values from the nested `MTConnect:Retry` section.

```json
{
  "MTConnect": {
    "Retry": {
      "MaxAttempts": "3",
      "InitialDelay": "00:00:01",
      "MaximumDelay": "00:00:30",
      "JitterRatio": "0.20"
    }
  }
}
```

### MaxAttempts

`MaxAttempts` includes the initial acquisition attempt.

```text
MaxAttempts = 3

attempt 1
    ↓ failure
attempt 2
    ↓ failure
attempt 3
    ↓ failure
propagate final failure
```

There are at most `MaxAttempts - 1` retry delays.

`MaxAttempts` must be at least one.

### InitialDelay

`InitialDelay` is the base delay after the first failed attempt.

It must be greater than zero.

### MaximumDelay

`MaximumDelay` caps the calculated delay.

It must not be less than `InitialDelay`.

### JitterRatio

`JitterRatio` controls proportional random variation around the calculated backoff delay.

It must be finite and between zero and one.

```text
JitterRatio = 0
        ↓
jitter disabled


JitterRatio = 0.20
        ↓
up to ±20 percent variation
```

The configured default is `0.20`.

---

## Transient Failure Classification

FC-018 retries only `HttpRequestException` failures that represent transient transport or HTTP conditions.

### Retryable Transport Failures

An `HttpRequestException` without an HTTP status represents a transport-level failure.

Examples can include:

- connection refusal
- DNS resolution failure
- connection reset
- interrupted network communication

```text
HttpRequestException
StatusCode = null
        ↓
transient
        ↓
eligible for retry
```

### Retryable HTTP Statuses

FC-018 classifies the following HTTP responses as transient:

| Status | Meaning |
|---|---|
| 408 | Request Timeout |
| 429 | Too Many Requests |
| 500–599 | Server failure |

```text
HTTP 408
HTTP 429
HTTP 5xx
        ↓
eligible for retry
```

### Non-Transient HTTP Statuses

Other HTTP `4xx` responses are not retried.

Examples include:

- 400 Bad Request
- 401 Unauthorized
- 403 Forbidden
- 404 Not Found when the body is not an MTConnect error document

```text
non-transient HTTP failure
        ↓
propagate immediately
```

FC-018 does not assume a configuration or request error will become valid merely by waiting.

---

## Protocol Errors Are Not Transport Retries

FC-016 introduced `MtConnectProtocolException` for structured MTConnect error documents.

These failures are not `HttpRequestException` instances and are not retried by FC-018.

```text
MTConnectProtocolException
        │
        ├── OUT_OF_RANGE
        ├── NO_DEVICE
        ├── INVALID_REQUEST
        ├── QUERY_ERROR
        └── INVALID_XPATH
        ↓
propagate immediately
```

This is intentional.

An MTConnect protocol error requires protocol-aware recovery policy, not blind transport retry.

In particular, FC-018 does not reset the cursor after `OUT_OF_RANGE`.

---

## Exponential Backoff

The retry delay grows exponentially after consecutive transient failures.

Before jitter and capping:

```text
delay = InitialDelay × 2^(failed attempt - 1)
```

For an initial delay of one second:

```text
after attempt 1 → 1 second
after attempt 2 → 2 seconds
after attempt 3 → 4 seconds
after attempt 4 → 8 seconds
```

The calculated base delay is capped at `MaximumDelay`.

```text
calculated delay
        ↓
min(calculated delay, MaximumDelay)
```

This prevents an extended outage from creating unbounded wait times.

---

## Jitter

Deterministic exponential backoff can cause many machine sessions to retry simultaneously.

FC-018 applies proportional jitter to reduce synchronized retry behavior.

```text
capped exponential delay
        ↓
random proportional offset
        ↓
final maximum-delay cap
        ↓
retry delay
```

For `JitterRatio = 0.20`:

```text
1 second base → 0.8 to 1.2 seconds
2 second base → 1.6 to 2.4 seconds
4 second base → 3.2 to 4.8 seconds
```

The final result never exceeds `MaximumDelay`.

Jitter is supplied through `IMtConnectJitterSource`.

Production uses `SystemMtConnectJitterSource`.

Tests use deterministic jitter values so delay assertions remain stable.

---

## Delay Abstraction

Retry waiting is represented by `IMtConnectRetryDelay`.

```csharp
Task DelayAsync(
    TimeSpan delay,
    CancellationToken cancellationToken = default);
```

Production uses `SystemMtConnectRetryDelay`, which delegates to cancellable `Task.Delay`.

Tests replace it with recording or immediate implementations.

This allows tests to verify:

- delay count
- exponential progression
- jitter application
- maximum cap
- cancellation

without waiting in real time.

---

## Cursor Preservation

FC-015 established the invariant:

> Session state advances only after successful acquisition with valid instance continuity.

FC-018 relies on that invariant.

```text
session cursor = 101
        ↓
GET /sample?from=101
        ↓
transient failure
        ↓
session cursor remains 101
        ↓
retry
        ↓
GET /sample?from=101
```

A retry therefore requests the same cursor.

No observation range is intentionally skipped because of a failed transport attempt.

After a successful retry:

```text
successful response
NextSequence = 111
        ↓
session cursor advances to 111
        ↓
sink receives successful batch once
```

FC-018 does not calculate or modify the cursor directly.

---

## Successful Acquisition

When an attempt succeeds:

```text
successful acquisition
        ↓
retry policy returns result
        ↓
no additional attempt
        ↓
sink receives result once
```

A previous transient failure does not cause duplicate sink handoff within the same cycle.

Only the successful result exits the retry policy.

---

## Retry Exhaustion

If every permitted attempt fails transiently:

```text
attempt 1 fails
        ↓
delay
        ↓
attempt 2 fails
        ↓
delay
        ↓
final attempt fails
        ↓
propagate HttpRequestException
```

The final exception is not replaced by a generic retry exception.

This preserves the underlying HTTP or transport failure for the host and future diagnostics.

The sink is not invoked.

The session cursor remains unchanged.

---

## Cancellation

The host cancellation token flows through:

- every acquisition attempt
- every retry delay
- the observation sink after success
- the outer continuous runtime

```text
host cancellation
        ↓
active acquisition or backoff delay
        ↓
OperationCanceledException
        ↓
stop immediately
```

Cancellation is never classified as a transient failure.

FC-018 does not schedule another attempt after cancellation.

---

## Retry Logging

Every scheduled retry produces a structured warning log.

The log preserves:

- failed attempt number
- configured maximum attempts
- calculated delay in milliseconds
- HTTP status or transport-failure classification
- original exception

```text
attempt failed
        ↓
calculate delay
        ↓
log retry decision
        ↓
wait
        ↓
next attempt
```

No retry log is produced when:

- the first attempt succeeds
- the failure is non-transient
- maximum attempts have been exhausted
- cancellation occurs
- the sink fails

---

## Runtime Composition

The Edge dependency graph now includes the retry services.

```text
MtConnectRetryOptions
        ├── IMtConnectRetryDelay
        │       └── SystemMtConnectRetryDelay
        ├── IMtConnectJitterSource
        │       └── SystemMtConnectJitterSource
        ↓
MtConnectTransientRetryPolicy
        ↓
MtConnectAcquisitionRuntime
```

The retry policy is composed once for the current one-machine runtime.

Multi-machine runtime supervision remains outside FC-018.

---

## Relationship to FC-017

FC-017 established the polling cycle:

```text
acquire
    ↓
sink
    ↓
polling interval
    ↓
next cycle
```

FC-018 adds bounded retry inside the acquisition stage:

```text
acquire
    │
    ├── transient failure
    │       ↓
    │   retry + backoff
    │       ↓
    │   acquire same cursor
    │
    └── success
            ↓
          sink
            ↓
      polling interval
```

Retry delays and polling intervals have different purposes.

```text
Retry delay
    =
wait between failed attempts in one cycle


Polling interval
    =
wait between completed successful cycles
```

---

## Failure Behavior

| Failure | FC-018 behavior |
|---|---|
| Transport `HttpRequestException` without status | Retry |
| HTTP 408 | Retry |
| HTTP 429 | Retry |
| HTTP 5xx | Retry |
| Other HTTP 4xx | Propagate |
| `MtConnectProtocolException` | Propagate |
| Malformed MTConnect data | Propagate |
| `InstanceId` change | Propagate |
| Sink failure | Propagate without reacquisition |
| Cancellation | Propagate immediately |

---

## Validation

FC-018 verifies:

- retry configuration is preserved
- maximum attempts must be at least one
- maximum delay cannot be less than initial delay
- invalid, non-finite, or out-of-range jitter is rejected
- transport failures without status are retried
- HTTP 408 is retried
- HTTP 429 is retried
- HTTP 500 and 503 are retried
- non-transient HTTP failures are not retried
- execution stops at the configured maximum attempts
- delay count is one less than the attempt count
- exponential delays progress correctly
- deterministic jitter is applied
- jittered delays respect the maximum cap
- cancellation interrupts retry backoff
- non-HTTP failures are not retried
- runtime retries use the same session cursor
- a successful retried batch reaches the sink once
- sink failure does not trigger another acquisition

The FactoryConnect test suite after FC-018 contains:

```text
Total Tests: 133
Passed: 133
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

`git diff --check` also completes without errors.

---

## Scope Boundary

FC-018 implements:

- bounded transient acquisition retries
- transport-failure retry classification
- HTTP 408 retry classification
- HTTP 429 retry classification
- HTTP 5xx retry classification
- configurable maximum attempts
- exponential backoff
- maximum-delay capping
- configurable proportional jitter
- deterministic jitter testing
- cancellable retry delays
- structured retry logging
- same-cursor acquisition retries
- successful single sink handoff
- sink exclusion from the retry boundary

FC-018 does not implement:

- `Retry-After` header support
- per-status retry limits
- circuit breaking
- long-term health state
- `OUT_OF_RANGE` recovery
- Agent restart recovery
- session replacement
- cursor reset policy
- sequence-gap recovery
- cursor persistence
- session restoration
- durable observation persistence
- transactional checkpointing
- delivery guarantees
- multiple-machine supervision
- canonical signal mapping
- machine-state derivation
- admin/setup UI

These concerns belong to later resilience, recovery, persistence, and runtime slices.

---

## Result

FC-018 makes continuous acquisition tolerant of bounded transient transport and server failures.

```text
continuous acquisition cycle
        ↓
attempt acquisition
        │
        ├── transient failure
        │       ↓
        │   exponential backoff
        │       ↓
        │   proportional jitter
        │       ↓
        │   retry same cursor
        │
        ├── non-transient failure
        │       ↓
        │   propagate
        │
        └── success
                ↓
          advance session cursor
                ↓
          hand batch to sink once
```

The acquisition stack now has distinct responsibilities:

```text
MtConnectSampleClient
        =
one protocol request


MtConnectAcquisitionSession
        =
cursor and InstanceId continuity


MtConnectTransientRetryPolicy
        =
bounded transient retry decisions


MtConnectAcquisitionRuntime
        =
continuous scheduling and cancellation


IMtConnectObservationSink
        =
successful-batch handoff
```

FactoryConnect can now continue through short-lived network and MTConnect server failures without hiding protocol errors or expanding retry behavior into continuity recovery.
