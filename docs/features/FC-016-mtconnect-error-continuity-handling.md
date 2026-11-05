# MTConnect Error & Continuity Handling

## Purpose

FC-016 introduces structured MTConnect protocol error handling for incremental acquisition.

FC-014 established stateless `/sample` acquisition.

FC-015 added stateful acquisition sessions and cursor continuation.

FC-016 adds the missing protocol-error boundary so FactoryConnect can distinguish MTConnect-reported errors from ordinary HTTP or transport failures.

```text
MTConnect Agent
      │
      ├── MTConnectStreams
      │        ↓
      │   normal acquisition
      │
      └── MTConnectError
               ↓
        structured error parsing
               ↓
      MtConnectProtocolException
```

> FC-016 preserves MTConnect error semantics.

> It does not implement retry, logging policy, polling, or automatic recovery.

---

## Error Model

MTConnect protocol errors are represented through two contracts:

```text
MtConnectError
    │
    ├── Code
    └── Message
```

and:

```text
MtConnectErrorResult
    │
    ├── InstanceId
    │
    └── Errors
          │
          └── MtConnectError[]
```

The error model preserves protocol facts without introducing recovery policy.

`Code` remains a string rather than an enum.

This keeps the protocol layer open to MTConnect error codes that may be added or encountered later.

---

## Error Parsing

FC-016 introduces `MtConnectErrorParser`.

The parser recognizes MTConnect error documents and converts them into structured error results.

```text
MTConnectError XML
      ↓
Header
      │
      └── instanceId
      ↓
Errors
      │
      ├── errorCode
      └── message
      ↓
MtConnectErrorResult
```

The parser distinguishes three different cases.

### Not an MTConnect Error Document

Examples include:

- empty response content
- invalid XML
- MTConnect stream documents
- other XML document types

In these cases:

```text
TryParse(...)
      ↓
false
```

No protocol error result is produced.

### Valid MTConnect Error Document

A valid MTConnect error document is converted into:

```text
MtConnectErrorResult
      │
      ├── InstanceId
      └── Errors[]
```

For example:

```xml
<MTConnectError>
  <Header instanceId="42" />
  <Errors>
    <Error errorCode="OUT_OF_RANGE">
      Requested sequence is outside the available range.
    </Error>
  </Errors>
</MTConnectError>
```

becomes conceptually:

```text
InstanceId = 42

Errors
  └── Code    = OUT_OF_RANGE
      Message = Requested sequence is outside the available range.
```

### Malformed MTConnect Error Document

Once a document is recognized as `MTConnectError`, invalid required protocol data is treated as malformed input.

Examples include:

```text
invalid instanceId
missing errorCode
no Error elements
```

These conditions raise `InvalidDataException`.

This preserves an important distinction:

```text
not an MTConnect error document
        ↓
TryParse = false


recognized MTConnect error document
but structurally invalid
        ↓
InvalidDataException
```

---

## InstanceId Preservation

MTConnect error documents may contain an Agent `instanceId`.

FC-016 preserves that value when present.

```text
Header instanceId="42"
        ↓
MtConnectErrorResult.InstanceId = 42
```

If `instanceId` is absent:

```text
InstanceId = null
```

If `instanceId` is present but malformed:

```text
InvalidDataException
```

FC-016 does not interpret the error `InstanceId` or use it to perform recovery.

It only preserves the protocol fact for higher layers.

---

## Multiple Errors

An MTConnect error response may contain more than one `Error` element.

FC-016 preserves all of them.

```text
MTConnectError
      │
      ├── OUT_OF_RANGE
      └── INVALID_REQUEST
              ↓
MtConnectErrorResult.Errors
      │
      ├── MtConnectError
      └── MtConnectError
```

No error is discarded or collapsed into a single message.

---

## Protocol Exception

FC-016 introduces `MtConnectProtocolException`.

This exception represents a successfully received HTTP response whose body contains structured MTConnect protocol errors.

```text
MtConnectProtocolException
      │
      ├── StatusCode
      └── ErrorResult
            │
            ├── InstanceId
            └── Errors[]
```

The exception message includes the HTTP status and MTConnect error details.

Conceptually:

```text
HTTP 404
+
OUT_OF_RANGE
+
Requested sequence is outside the available range.
        ↓
MtConnectProtocolException
```

This allows higher layers to distinguish MTConnect protocol failures from transport failures.

---

## HTTP Failure Classification

Before FC-016, `MtConnectSampleClient` used:

```text
EnsureSuccessStatusCode()
```

for all non-success HTTP responses.

This meant MTConnect protocol errors were reduced to ordinary HTTP failures.

FC-016 changes the flow.

```text
HTTP response
      │
      ▼
Read response body
      │
      ▼
Success?
  ┌───┴───┐
 yes      no
  │        │
  ▼        ▼
parse    TryParse MTConnectError
sample       │
         ┌───┴───┐
       parsed   not parsed
         │         │
         ▼         ▼
MtConnectProtocol  normal HTTP
Exception           failure
```

The resulting behavior is:

```text
Successful MTConnectStreams
        ↓
MtConnectSampleResult


Non-success HTTP
+
valid MTConnectError
        ↓
MtConnectProtocolException


Non-success HTTP
+
non-MTConnect response
        ↓
HttpRequestException


Non-success HTTP
+
malformed MTConnectError
        ↓
InvalidDataException
```

---

## OUT_OF_RANGE

FC-016 explicitly preserves protocol error codes such as:

```text
OUT_OF_RANGE
```

For example:

```text
Code = "OUT_OF_RANGE"
```

FC-016 deliberately does not yet classify this as recoverable or unrecoverable.

It also does not:

- reset the cursor
- choose a new sequence
- mark a data gap
- restart acquisition
- retry automatically

Those decisions belong to the acquisition runtime.

FC-016 only ensures the error is no longer hidden behind a generic HTTP exception.

---

## Relationship to Acquisition Sessions

FC-015 established the invariant:

> Session state advances only after a successful acquisition with valid instance continuity.

FC-016 preserves that rule.

A protocol error does not represent a successful acquisition.

Therefore:

```text
MtConnectProtocolException
        ↓
no session state commit
```

### Initial Session Protocol Error

```text
Session before request

InstanceId   = null
NextSequence = 101

        ↓

MTConnect protocol error

        ↓

Session remains

InstanceId   = null
NextSequence = 101
```

### Established Session Protocol Error

```text
Session before request

InstanceId   = 42
NextSequence = 111

        ↓

MTConnect protocol error

        ↓

Session remains

InstanceId   = 42
NextSequence = 111
```

The acquisition cursor is therefore never advanced because of a failed protocol request.

---

## Continuity Boundary

FC-016 provides continuity information without applying continuity policy.

For example:

```text
OUT_OF_RANGE
      ↓
protocol fact preserved
      ↓
higher layer decides recovery
```

Likewise:

```text
NO_DEVICE
INVALID_REQUEST
QUERY_ERROR
INVALID_XPATH
```

remain structured protocol errors.

The MTConnect layer does not decide whether they should be:

- retried
- ignored
- escalated
- logged
- recovered
- treated as configuration faults

That responsibility belongs above the protocol layer.

---

## Responsibility Boundary

The MTConnect acquisition stack now has four clear responsibilities:

```text
MTConnect protocol request
        │
        ▼
MtConnectSampleClient
        │
        │ one request
        ▼
MtConnectAcquisitionSession
        │
        │ cursor + InstanceId continuity
        ▼
MTConnect error semantics
        │
        │ structured protocol failures
        ▼
Future acquisition runtime
        │
        ├── logging
        ├── retry
        ├── backoff
        ├── recovery policy
        └── continuous execution
```

The protocol layer reports facts.

The runtime layer will decide policy.

---

## Internal Parsing Boundary

`MtConnectErrorParser` remains internal to the MTConnect protocol project.

It is not part of the public protocol API.

Tests access the parser through:

```xml
<InternalsVisibleTo Include="FactoryConnect.Protocols.MTConnect.Tests" />
```

This preserves the intended public surface while allowing direct verification of internal protocol parsing behavior.

Public contracts include:

```text
MtConnectError
MtConnectErrorResult
MtConnectProtocolException
MtConnectSampleClient
MtConnectAcquisitionSession
```

Internal implementation includes:

```text
MtConnectErrorParser
MtConnectSampleParser
MtConnectCurrentParser
MtConnectObservationParser
```

---

## Validation

FC-016 verifies:

- `OUT_OF_RANGE` parsing
- `NO_DEVICE` parsing
- error message preservation
- `InstanceId` preservation
- missing `InstanceId` handling
- multiple MTConnect errors
- non-MTConnect document rejection
- invalid XML rejection
- empty response handling
- missing `errorCode` rejection
- missing error elements rejection
- malformed `instanceId` rejection
- `MtConnectProtocolException` creation
- HTTP status preservation
- non-MTConnect HTTP failure preservation
- malformed MTConnect error propagation
- acquisition-session state preservation on protocol failure

The FactoryConnect test suite after FC-016 contains:

```text
Total Tests: 103
Passed: 103
Failed: 0
Skipped: 0
Result: SUCCEEDED
```

`git diff --check` also completes without errors.

---

## Scope Boundary

FC-016 implements:

- MTConnect error contracts
- MTConnect error document parsing
- `errorCode` preservation
- error message preservation
- optional `InstanceId` preservation
- multiple-error preservation
- malformed MTConnect error validation
- `MtConnectProtocolException`
- HTTP status preservation
- distinction between protocol errors and ordinary HTTP failures
- structured `OUT_OF_RANGE` exposure
- acquisition-session state preservation on protocol errors
- internal parser test visibility

FC-016 does not implement:

- continuous polling
- logging policy
- `ILogger`
- retry policies
- retry backoff
- configurable resilience
- automatic cursor reset
- automatic continuation after `OUT_OF_RANGE`
- Agent restart recovery
- sequence-gap recovery
- reconnect orchestration
- polling intervals
- timers
- `BackgroundService`
- persistence
- runtime hosting
- canonical signal mapping
- machine-state derivation
- admin/setup UI

These concerns belong to later acquisition and runtime slices.

---

## Result

FC-016 establishes the structured failure boundary required before continuous acquisition and resilience can be introduced.

FactoryConnect can now distinguish:

```text
normal MTConnect acquisition
        ↓
MtConnectSampleResult


MTConnect protocol failure
        ↓
MtConnectProtocolException


ordinary HTTP / transport failure
        ↓
HttpRequestException


malformed MTConnect protocol data
        ↓
InvalidDataException
```

The MTConnect acquisition progression is now:

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
        ↓
FC-016
error & continuity semantics
structured protocol failures
```

FC-016 does not attempt to make acquisition resilient.

Instead, it gives the next runtime layer enough structured information to implement resilience safely.

That future runtime can decide how to:

```text
log
retry
back off
recover
mark continuity loss
continue acquisition
```

without changing the protocol acquisition contracts established by FC-014 through FC-016.
