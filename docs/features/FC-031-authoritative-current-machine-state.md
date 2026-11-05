# FC-031 — Authoritative Current Machine State

## Status

```text
FC-031
Authoritative Current Machine State

Status          COMPLETE / CLOSED / FROZEN
Closure baseline 0dc0cb96da0a034884a956b1aed23cdb8b63dfe5
Production work  COMPLETE
Remaining work   NONE
```

FC-031 closes the authoritative current-machine-state path from durable acquisition evidence through API transport and dashboard machine-detail consumption. This document is the whole-feature closure record; detailed slice records remain the authority for their frozen contracts and conformance evidence.

## Delivered end-to-end boundary

```text
Acquisition and durable observations
        ↓
contact, coverage and continuity authorities
        ↓
stable authority cut
        ↓
semantic current-state reader
        ↓
InMemory and SQL realization
        ↓
HTTP current-state surface
        ↓
Dashboard machine-detail consumption
```

The completed feature does not create a second machine-state model in the API or browser. Acquisition, mapping and state/activity authority are composed into a stable authority cut; the Core reader evaluates that authority; the API transports the result; and the dashboard presents the authoritative response for configured machines.

## Delivered slices

The whole feature was delivered through the following closed boundaries:

- **FC-031.2A — Current-State Contract Foundation** established the portable current-state vocabulary and ownership boundary.
- **FC-031.2B — Acquisition / Contact Authority** established durable stream-scoped contact authority.
- **FC-031.2C — Mapping Coverage Authority** established authoritative mapping progress/coverage.
- **FC-031.2D — State / Activity Continuity Authority** established continuity-preserving state/activity publication.
- **FC-031.2E — In-Memory Authority-Cut Provider** closed provider-owned authority composition, stable-cut reading, capability activation, observation/production handoff, and whole-provider conformance.
- **FC-031.3 — Core Current-State Semantic Reader** established the provider-neutral semantic reader over the stable authority cut.
- **FC-031.4B — SQL Current-State Authority Realization** added SQL schema authority, Mapping persistence, joint state/activity publication, durable read surfaces, stable authority-cut composition, and restart/forward conformance.
- **FC-031.5A — HTTP Current-State Surface** exposed the semantic reader through the versioned API.
- **FC-031.5B — Dashboard Current-State Consumption** added exact same-origin forwarding, generated transport consumption, configured-machine correlation, lifecycle ownership, and machine-detail presentation.

The SQL realization also consumed the durable observation-processing and operational-metric SQL prerequisites required by the composed production graph. Those prerequisites did not redefine the portable current-state semantics.

## Semantic boundary

The public HTTP surface is:

```text
GET /api/machines/v1/{machineId}/current-state
```

Successful authoritative outcomes remain distinct:

```text
evidence
no-evidence
```

`no-evidence` is a successful authoritative result, not a not-found or transport failure.

The response preserves separate semantic dimensions:

```text
outcome
coverage
machine state
freshness
usability
read-as-of
```

Coverage, machine state, freshness, and usability must not be collapsed into a browser-derived composite status. In particular, unknown evidence is not the same as no evidence, behind is not stale, stale is not indeterminate, and authority unavailability is not successful absence.

## Provider realization

The portable current-state contract is realized by both supported persistence paths.

### InMemory

The InMemory provider owns one compatible acquisition/contact, mapping, state/activity and stable-cut authority graph. Whole-provider conformance proves that current-state and production activity-history reads observe the same provider-owned authority lineage.

### SQL Server

The SQL Server provider persists the current-state authorities and exposes `CurrentStateAuthorityReading` when that capability is requested. The SQL composed-current-state proof covers durable observation flow through semantic evidence, reconstruction/restart, forward continuation, and non-duplication of durable state/activity history.

Provider activation remains capability-driven. A composition root does not silently mix an unsupported provider with an in-memory current-state fallback.

## Request-driven runtime boundary

FC-031 introduced no additional background current-state runtime.

```text
acquisition / observation processing
        existing runtime ownership

current-state reading
        request-driven
```

The API composes `ICurrentMachineStateReader` and evaluates it when the current-state endpoint is requested. The Dashboard requests that endpoint when presenting a configured machine. FC-031 does not add a current-state worker, queue, cache, scheduler, or independent acquisition loop.

## Dashboard consumption

Dashboard machine-detail consumption is constrained to configured machine identities. A route machine identifier is correlated with `DashboardRuntimeSource.MachineId`; an unconfigured route does not become arbitrary machine discovery.

The browser contract is generated from the FactoryConnect.Api OpenAPI document. The dashboard does not maintain a competing handwritten transport DTO authority.

Delivered refresh behavior is:

```text
initial load       YES
manual refresh     YES
periodic polling   NO
background refresh NO
visibility refresh NO
focus refresh      NO
streaming / push   NO
```

Each current-state execution uses cancellation and generation ownership so a superseded or disposed request cannot publish stale state. Manual refresh may retain the prior successful result while the replacement request is in flight.

Polling, streaming, push updates, focus/visibility refresh, and other live-monitoring behavior remain optional future product work. Their absence is not incomplete FC-031 behavior.

## FC-029 Machine Status deferral

FC-029 deferred Machine Status until an authoritative server-side current-state reader/API existed. FC-031.3 supplied the semantic reader, FC-031.5A supplied the HTTP surface, and FC-031.5B supplied dashboard machine-detail consumption.

That FC-029 deferral is therefore closed. FC-029 reporting semantics remain unchanged; current state is consumed from the separate FC-031 authority path rather than inferred from operational metrics.

## Deployment boundary

The dashboard current-state route uses the existing same-origin gateway and the same configured `Dashboard:ReportingApiBaseAddress` and `Dashboard:RequestTimeout` used by reporting requests. FC-031.5B requires no additional dashboard upstream endpoint, listener, or port.

The selected API persistence provider must supply the current-state authority capability and the API current-state inventory/composition must be valid. Those are runtime composition prerequisites, not browser inference.

TLS termination, certificate lifecycle, firewall policy, operating-system service supervision, automatic restart, startup ordering, log collection, secret management, and machine-level hardening remain deployment/infrastructure responsibilities. FC-031 does not implement them.

## Explicitly deferred / separate work

The following are not FC-031 closure blockers:

- periodic polling or auto-refresh;
- streaming or push-based current-state delivery;
- focus/visibility refresh;
- alerting or notification policy;
- cross-machine current-state aggregation;
- predictive/AI interpretation;
- authentication/authorization policy expansion;
- TLS, reverse-proxy, supervision, hardening, or rollout automation.

These require separate product or deployment authorization.

## Closure

FC-031 has no remaining required production slice.

The delivered repository now contains a coherent authoritative path from acquisition evidence to dashboard presentation, with InMemory and SQL provider realization, stable semantic/HTTP boundaries, and request-driven application consumption.

Detailed frozen FC-031 slice records are intentionally not rewritten by this closure. They remain historical and contractual evidence for the decisions made within each slice.
