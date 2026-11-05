# FC-029 — First Factory Dashboard / Daily Report

## Status

- **FC-029.1 — Dashboard Application Foundation:** complete
  - **FC-029.1A — merged FC-028 contract inventory:** complete
  - **FC-029.1B — dashboard host foundation:** complete
  - **FC-029.1C — OpenAPI → TypeScript generation:** complete
  - **FC-029.1D — typed reporting client boundary:** complete
  - **FC-029.1E — React application shell and query state:** complete
    - **FC-029.1E.1 — React/Vite build foundation:** complete
    - **FC-029.1E.2 — runtime configuration and same-origin gateway:** complete
    - **FC-029.1E.3 — deterministic application router and shell:** complete
    - **FC-029.1E.4 — QueryState and lifecycle controller:** complete
    - **FC-029.1E.5 — cancellation and stale-response conformance:** complete
    - **FC-029.1E.6 — shell integration and production asset build:** complete
  - **FC-029.1F — first vertical reporting proof:** complete
  - **FC-029.1G — architecture, deployment, and whole-feature conformance:** complete
- **FC-029.2 — Production-Day Overview:** complete
  - **FC-029.2A — Factory Overview Configuration:** complete
  - **FC-029.2B — Production-Day Query Orchestration:** complete
  - **FC-029.2C — Production-Day Presentation Model:** complete
  - **FC-029.2D — Production-Day Overview UI:** complete
  - **FC-029.2E — Production-Day Overview Contract & Behavior Conformance:** complete
- **FC-029.3 — Shift Performance Overview:** complete
  - **FC-029.3A — Shift Reporting Query Boundary:** complete
    - **FC-029.3A.1 — authoritative production-day-to-shift selection:** complete
    - **FC-029.3A.2 — shift query orchestration:** complete
    - **FC-029.3A.3 — pagination/lifecycle conformance:** complete
  - **FC-029.3B — Shift Presentation Model:** complete
  - **FC-029.3C — Shift Performance UI:** complete
  - **FC-029.3D — Shift Detail Interaction:** deferred / optional
  - **FC-029.3E — Whole-Feature Conformance:** complete
- **FC-029.4 — Daily Report / Print Surface:** complete / frozen
  - **FC-029.4A — Contract / Repository Inventory:** complete / frozen
  - **FC-029.4B — Daily Report Implementation:** complete / frozen
    - **FC-029.4B.1 — Composition / Presentation Model:** complete / frozen
    - **FC-029.4B.2 — Lifecycle / Query Orchestration:** complete / frozen
    - **FC-029.4B.3 — Surface / Navigation / Print:** complete / frozen
  - **FC-029.4C — Whole-Feature Conformance:** complete / frozen
  - **FC-029.4D — Final Closure / Documentation:** complete / frozen

**Machine Status deferral: closed by FC-031.** FC-031 delivers the authoritative current-state reader, HTTP surface, and configured-machine dashboard consumption. FC-029's reporting contracts and historical closure remain unchanged.

## Architectural invariant

The dashboard consumes authoritative reporting and current-state contracts. It does not reconstruct factory metrics, reporting periods, production context, current machine state, or reporting persistence.

```text
FC-027 durable operational metric projections
    ↓
FC-028 Reporting API
    ↓ HTTP/OpenAPI
FactoryConnect.Dashboard
    ↓
presentation only
```

`FactoryConnect.Dashboard` has no FactoryConnect project or runtime assembly dependency. Browser transport contracts come from the generated FactoryConnect.Api OpenAPI artifact; FC-029 reporting types and the later FC-031 current-state types share that generated transport authority.

The dashboard may format, group, filter, navigate, and render authoritative reporting results and the authoritative FC-031 current-state response. It must not calculate Availability, Performance, Quality, OEE, Utilization, production-day boundaries, current machine state, or factory-wide percentages. It must not combine metric-definition versions or reinterpret reporting absence/failure states.

## FC-029.1 closure evidence

Final regression evidence for the completed Dashboard Application Foundation:

```text
frontend       108/108 passed
non-SQL        783/783 passed
SQL Server      75/75 passed
Release build  passed
git diff --check clean
working tree   clean
branch         synchronized with origin
```

The generated FC-028 browser contract remains synchronized through `npm run contracts:check`.

## FC-029.1A — authoritative FC-028 inventory

FC-028 exposes two reporting operations used by the dashboard foundation:

```text
POST /api/reporting/v1/operational-metrics/shifts/query
POST /api/reporting/v1/operational-metrics/production-days/query
```

Source identity is exactly `(MachineId, ProcessorId)`. Successful no-match queries return an empty page. Transport statuses are exactly:

```text
calculated
unavailable
insufficient-evidence
```

Calculated zero remains distinct from unavailable or insufficient evidence.

## FC-029.1B — dashboard host foundation

`FactoryConnect.Dashboard` is an ASP.NET Core factory-LAN host with:

```text
DashboardOptions + startup validation
/health/live
/health/ready
static production asset hosting
SPA fallback for presentation routes only
```

Production configuration intentionally contains no usable reporting endpoint or source defaults. Startup requires an absolute HTTP/HTTPS reporting address and a positive timeout no greater than five minutes. Configured reporting sources are `0..N`; when present, each source must have valid identity/presentation metadata and `(MachineId, ProcessorId)` pairs must be unique. Production rejects loopback reporting addresses.

Health semantics remain narrow:

```text
/health/live  → process is running
/health/ready → frontend index asset exists
```

Neither endpoint probes FC-028.

## FC-029.1C — OpenAPI → TypeScript generation

The browser reporting contract is generated from the authoritative FC-028 OpenAPI document. A normal `FactoryConnect.Api` build emits the intermediate OpenAPI document and `openapi-typescript` generates:

```text
src/FactoryConnect.Dashboard/ClientApp/src/api/generated/reporting-contract.ts
```

The generated artifact is committed, marked generated, and checked for deterministic regeneration and drift through:

```text
npm run contracts:check
```

Node is pinned. TypeScript is strict. Generated transport contracts are not manually patched with additional reporting semantics.

## FC-029.1D — reporting client boundary

The browser-side `ReportingClient` executes the authoritative FC-028 reporting operations exposed through the dashboard gateway.

```text
createReportingClient(options)
        ↓
raw HTTP transport
        ↓
request executor
        ↓
response decoder
        ↓
generated OperationalMetricPage
```

The client owns URI composition, request serialization, timeout, caller cancellation, network classification, HTTP/protocol decoding, and runtime response shape validation. It does not own metric calculations or presentation semantics.

Known FC-028 invalid-query Problem Details remain distinct from malformed/incompatible continuation-token failures. Unknown valid Problem Details remain HTTP failures. Calculated zero, nullable reasons, full source revision, context, and opaque continuation tokens are preserved.

## FC-029.1E — React application shell and query state

### E.1 — React/Vite build foundation

The dashboard uses the pinned React/Vite stack and produces hashed production assets into the dashboard web root. Frontend TypeScript and Node/Vite configurations remain strict and separated.

### E.2 — runtime configuration and same-origin gateway

The browser loads only:

```text
GET /dashboard/config
```

Browser-safe runtime configuration contains:

```text
reportingBasePath = "/"
requestTimeoutMilliseconds
sources[] =
  MachineId
  ProcessorId
  DisplayName
  GroupName?
  DisplayOrder
```

`GroupName` is optional. `DisplayOrder` is non-negative and is currently factory-wide. Configuration determines source inclusion and presentation metadata only; it does not contain metric values, formulas, statuses, current machine state, or inferred production truth.

The upstream `ReportingApiBaseAddress` is never exposed to the browser.

The factory pilot uses a same-origin gateway:

```text
browser
  ↓ same origin
FactoryConnect.Dashboard
  ↓ controlled forwarding
FC-028 Reporting API
```

The gateway preserves request bytes and response status/body/content type. It does not retry, cache, reshape, calculate, or classify reporting results. Upstream timeout maps to 504; upstream connection/runtime failure maps to 502; browser cancellation aborts forwarding.

### E.3 — deterministic application router and shell

The root-hosted application route set includes:

```text
/
/production-days/{productionDay}
/machines/{machineId}
/production-days/{productionDay}/report
```

Routing is exact, query/fragment independent, safely URI-decoded, history-aware, and explicitly root-hosted. Reserved host paths do not fall through to the SPA shell.

### E.4 — QueryState and lifecycle controller

The closed presentation lifecycle vocabulary is:

```text
idle
loading
success<T>
empty
invalidRequest
failed
```

Required distinctions include:

```text
successful reporting result with zero items → empty query result
invalid reporting query                     → invalidRequest
calculated value = 0                        → success containing zero
timeout/network/http/protocol               → failed
failure                                     ↛ empty
```

FC-029.2 maps an empty authoritative production-day result for configured sources into explicit presentation-level `missing` metric slots rather than hiding configured machines.

### E.5 — cancellation and stale-response conformance

Each execution owns a generation and `AbortController`. Superseded or disposed requests are aborted, but generation ownership—not cooperative abort behavior—is authoritative for stale publication suppression.

```text
A starts
B supersedes A
A completes late
        ↓
A cannot publish over B
```

Unexpected cancellation of the current request remains a failure. Obsolete programming exceptions remain observable to their awaiting caller but cannot mutate current UI state.

### E.6 — shell integration and production asset build

Runtime configuration is loaded once at bootstrap and the same-origin `ReportingClient` is composed once. React mounts/unmounts controller subscriptions correctly. Every QueryState variant has deterministic presentation without metric interpretation.

Vite production assets are generated into `wwwroot`, included in clean `dotnet publish`, and served by the dashboard host. Generated web assets are not repository source-of-truth files.

## FC-029.1F — first vertical reporting proof

FC-029.1F established the first real reporting surface at:

```text
/production-days/{productionDay}
```

That slice proved route selection, DateOnly validation, generated transport usage, reporting-client execution, lifecycle ownership, and authoritative result rendering. FC-029.2 supersedes its intentionally narrow query/presentation behavior with the complete Production-Day Overview contract documented below.

## FC-029.1G — architecture, deployment, and whole-feature conformance

FC-029.1G closed the dashboard foundation and introduced no new metric authority. Deployment and hosting details remain documented in:

```text
docs/deployment/factoryconnect-dashboard-lan.md
```

## FC-029.2 — Production-Day Overview

### Goal

Given the factory-configured machine population and a selected production day, query the authoritative FC-028 production-day reporting surface and present each configured machine's exact-version operational metric evaluations in deterministic configured grouping/order.

The governing invariant is:

> FC-029.2 visualizes authoritative production-day truth; it does not manufacture new production truth.

### FC-029.2A — Factory Overview Configuration

Configured sources are `0..N`, including an empty factory. No seven-machine assumption exists.

Each source exposes only reporting identity and presentation metadata:

```text
MachineId
ProcessorId
DisplayName
GroupName?
DisplayOrder
```

Validation requires non-empty trimmed identity/display values, optional non-empty `GroupName`, non-negative `DisplayOrder`, and unique `(MachineId, ProcessorId)` identity.

Configured source order is deterministic:

```text
DisplayOrder
GroupName ordinal
DisplayName ordinal
MachineId
ProcessorId ordinal
```

Configuration does not introduce metric values, formulas, statuses, inferred machine state, or production truth.

### FC-029.2B — Production-Day Query Orchestration

The production-day overview requests exactly these metric definitions:

```text
Availability / 1.0
Utilization  / 1.0
Performance  / 1.0
Quality      / 1.0
OEE          / 1.0
```

The request uses the exact configured `(MachineId, ProcessorId)` identities and canonical unpartitioned context:

```text
ProductionOrderId = null
OperationId       = null
PartId            = null
OperatorId        = null
UnpartitionedOnly = true
```

The browser does not calculate metric values or infer reporting context.

Pagination is complete and opaque. The query service:

- consumes every continuation page before returning success;
- forwards continuation tokens unchanged and never decodes/interprets them;
- uses a bounded maximum of 100 pages;
- rejects repeated-token and multi-token cycles;
- preserves cancellation across the entire traversal;
- rejects the whole query when any later page fails;
- never returns partial accumulated results after traversal failure.

Traversal violations enter the existing reporting protocol-failure lifecycle rather than escaping as uncontrolled application exceptions.

### FC-029.2C — Production-Day Presentation Model

The presentation mapper is pure and deterministic. It receives the complete authoritative result set plus configured presentation metadata and produces grouped machine-level display models.

Exact correlation identity is:

```text
ProcessorId
MachineId
ProductionDay
Unpartitioned Context
MetricKey
DefinitionVersion
```

Every authoritative item is validated before any missing slot is manufactured. The mapper rejects:

```text
duplicate-result
unexpected-source
unexpected-scope
unexpected-period
unexpected-context
unexpected-metric
invalid-result-shape
```

Unexpected reporting sources are contract failures; they are not rendered as extra dashboard rows.

Authoritative evaluation states are preserved exactly:

```text
calculated
unavailable
insufficient-evidence
```

`missing` is presentation-only and means no authoritative result exists for an expected configured source/day/context/metric/version identity. Missing does not fabricate value, unit, reason, revision, reporting status, or zero.

Calculated values, including numeric strings and zero, are preserved without metric arithmetic, normalization, clamping, averaging, or OEE recomputation.

Duplicate authoritative identities are rejected; the mapper never resolves duplicates by latest revision, sort order, first-wins, or last-wins behavior.

### FC-029.2D — Production-Day Overview UI

The UI composes:

```text
selected date-only route
        ↓
FC-029.2B controlled query lifecycle
        ↓
complete authoritative result set
        ↓
FC-029.2C controlled presentation mapping
        ↓
grouped production-day metric matrix
```

The matrix renders configured group/machine order and the five exact metric columns. It distinguishes calculated, unavailable, insufficient-evidence, and missing states. Supplied reason information remains visible.

Formatting is display-only. Numeric-string values are formatted without converting authoritative strings through floating-point arithmetic. The UI does not calculate percentages from other metrics and does not recompute OEE.

Expected request/reporting/presentation failures are contained in controlled view states and rendered through visible alerts. `ProductionDayPresentationFailure` does not escape React rendering uncaught. Unexpected programmer errors are not silently reclassified.

Refresh preserves lifecycle ownership and records last successful retrieval time only after a successful authoritative retrieval. Date changes isolate prior request ownership; stale responses cannot publish into the newly selected day.

### FC-029.2E — Production-Day Overview Contract & Behavior Conformance

Whole-feature conformance proves:

```text
configured populations 0 / 1 / 7 / 50
exact MachineId + ProcessorId source identities
exact five metric key/version identities
canonical unpartitioned context
DateOnly production-day boundaries
complete opaque multi-page traversal
bounded traversal and cycle protection
later-page failure containment without partial results
mixed calculated / unavailable / insufficient-evidence / missing states
authoritative reason and source-revision preservation
deterministic configured group and machine order
authoritative OEE displayed without recomputation
expected lifecycle failures rendered through visible alerts
no current-machine-state labels or inference
no dependency on acquisition/current-state projection modules
no frontend production-day reconstruction or metric aggregation
```

The dependency conformance proof transitively inspects the Production-Day Overview application path using a closed exact dependency allowlist, including query orchestration, lifecycle, presentation, formatting, navigation, surface, and matrix modules.

### FC-029.2 closure evidence

Final independently verified frontend closure gate:

```text
npm run typecheck   PASS
npm test            173/173 PASS
npm run build       PASS
Vite                8.2.2, 40 modules transformed
git diff --check    clean
```

FC-029.2A through FC-029.2E are complete.

## FC-029.3 — Shift Performance Overview

FC-029.3 is the next dashboard slice because FC-028 already exposes authoritative shift metric reporting. Its goal is to present authoritative shift-occurrence evaluations for the configured factory population without calculating production-day values, averages, rankings, OEE, current shift, shift boundaries, or current machine state in the browser.

Planned slices:

```text
FC-029.3A  Shift Reporting Query Boundary
  3A.1     Authoritative production-day-to-shift selection
  3A.2     Shift query orchestration
  3A.3     Pagination/lifecycle conformance
FC-029.3B  Shift Presentation Model
FC-029.3C  Shift Performance UI
FC-029.3D  Shift Detail Interaction (optional)
FC-029.3E  Whole-Feature Conformance
```

### FC-029.3A.1 — unresolved architectural gate

No FC-029.3 React implementation may begin until authoritative production-day-to-shift selection is defined and approved.

A bare business date must not automatically be treated as the complete production-day identity. The FC-026 domain identity includes site ownership, so the inventory must determine whether the authoritative identity is at least:

```text
ProductionDayId
  SiteId
  BusinessDate
```

The reporting-domain inventory must determine:

1. whether durable shift evaluations already retain their owning production-day identity;
2. whether that ownership can be queried directly without recomputing calendar semantics;
3. whether one request may cover configured sources across multiple sites;
4. how site identity required for shift selection is obtained when no production-day metric evaluation exists;
5. whether site identity belongs in dashboard runtime configuration or an upstream reporting-selection contract.

The preferred FC-028 direction, if durable/reporting ownership supports it, is a distinct production-day shift reporting selector/operation rather than browser-derived UTC boundaries. Conceptually:

```text
ProductionDayShiftMetricQuery
  Sources
  ProductionDayIds[] = SiteId + BusinessDate
  Metrics
  Context
  Statuses
  Order
  Page
```

A separate operation is preferred over ambiguous optional interval and production-day fields. If the existing shift operation is extended instead, selection must be a closed mutually exclusive contract such as `UtcInterval | ProductionDays`, with mixed or empty selector shapes rejected.

Before 3A.2 begins, 3A.1 must prove:

```text
selected production-day identity is complete and authoritative
browser sends no derived UTC shift boundaries
browser contains no shift schedule definitions
configured sources across relevant sites are supported
returned shifts belong only to requested production days
overnight shift ownership is authoritative
DST transitions require no browser logic
missing metric evaluations do not prevent shift selection
existing UTC interval query behavior remains compatible
continuation identity binds to the production-day selector
```

Continuation tokens produced for one production-day/site/source/metric/context selection must not be reusable as compatible tokens for another selection.

### FC-029.3A.1 reporting-domain inventory

The existing durable pipeline establishes the requested production-day identity but does not yet preserve enough ownership metadata at the reporting projection to execute the preferred query.

| Boundary | Authoritative identity or selection | Production-day ownership retained for a shift? |
| --- | --- | --- |
| FC-025 metric input | `ShiftOccurrenceId` and `ProductionDayId` are persisted together on every positioned metric-input fact | Yes, for occurrences that produced facts |
| FC-026 aggregation | Separate `(MachineId, ShiftOccurrenceId, MetricInputKey)` and `(MachineId, ProductionDayId, MetricInputKey)` aggregates | No association in either aggregate identity |
| FC-026 revision change | Independent sets of affected `ShiftOccurrenceId` and `ProductionDayId` values | No pairing |
| FC-027 evaluation identity | `OperationalMetricPeriodId.Shift(ShiftOccurrenceId)` or `OperationalMetricPeriodId.ProductionDay(ProductionDayId)` | No production-day owner on a shift evaluation |
| FC-027 reporting projection | Projection summary contains processor, evaluation key, result, and source revision | No production-day owner on a shift projection |
| FC-028 shift query | Selects shift projections by `ShiftOccurrenceId.StartsAtUtc` in an absolute UTC start interval | No production-day selector |
| FC-028 production-day query | Selects production-day projections by `ProductionDayId.BusinessDate` | Does not return shift projections |

`ProductionDayId` is the complete existing production-day identity:

```text
ProductionDayId
  SiteId
  BusinessDate
```

`ShiftOccurrenceId` is independently complete for a resolved shift occurrence:

```text
ShiftOccurrenceId
  SiteId
  ShiftScheduleAssignmentId
  ShiftId
  StartsAtUtc
  EndsAtUtc
```

The shift identity deliberately contains no business date. Its UTC interval therefore cannot be converted to a `ProductionDayId` without consulting the authoritative schedule/calendar rules. Comparing its start timestamp with browser-derived day boundaries would reconstruct the very ownership that FC-025 already resolved and persisted.

The inventory decision is consequently:

> `ProductionDayId = SiteId + BusinessDate` is sufficient to express the desired reporting selection, but the current FC-027/028 shift result surface cannot execute that selection directly because the owning `ProductionDayId` does not survive into a shift evaluation or reporting projection.

FC-029.3A.1 must close this upstream gap before query orchestration begins. It must determine two independent authoritative facts:

```text
occurrence existence/applicability
  MachineId → ShiftOccurrenceId

production-day ownership
  ShiftOccurrenceId → ProductionDayId
```

These facts are related but not interchangeable. A site-level occurrence and production-day association does not establish which requested machines use that shift schedule. Line-specific schedule assignments also prohibit forming the Cartesian product of every requested machine and every site occurrence.

The persisted FC-025 ownership pair is authoritative evidence for a machine occurrence that produced a metric-input fact and must validate any roster representation for that occurrence. It is not necessarily the authority for occurrence existence: FC-025 facts are derived evidence, so an applicable machine/shift occurrence with no activity or quantity evidence may have no persisted pair at all. Metric-input existence must therefore not be required to establish the roster.

The authoritative roster source remains a 3A.1 design decision. Candidate sources include upstream shift-resolution/schedule semantics, a new durable resolved machine-occurrence projection, or another persisted allocation/read model. The reporting boundary must consume the resolved result; it must not independently reconstruct schedule applicability or calendar ownership. Copying ownership onto evaluation/projection rows may support filtering but is not sufficient because an occurrence with no requested metric evaluations must remain selectable.

Whichever representation is chosen must enforce:

```text
same machine/production-day selection → deterministic applicable occurrences
same ShiftOccurrenceId → exactly one ProductionDayId
same SiteId on both identities
line/site schedule applicability is resolved upstream
conflicting ownership → reject, never choose or repair
occurrence existence and ownership available independently of metric-input/evaluation existence
```

The selected reporting result therefore needs an authoritative machine-occurrence roster conceptually containing:

```text
MachineShiftOccurrenceOwnership
  MachineId
  ShiftOccurrenceId
  ProductionDayId
```

`ProcessorId` remains reporting/projection source identity; it is not promoted into factory scheduling identity. FC-028 correlates zero or more metric evaluations to each applicable roster item by `(MachineId, ProcessorId, ShiftOccurrenceId, Context, MetricKey, DefinitionVersion)`. Metric-input and metric-evaluation rows must not be used as the source of occurrence existence.

The desired selection flow is:

```text
requested Sources + requested ProductionDayIds
                    ↓
authoritative machine/occurrence roster
                    ↓
zero-or-more FC-027 metric evaluations
```

Once that roster reaches the FC-028 query boundary, FC-028 should expose a distinct production-day shift operation whose selector contains exact `ProductionDayId` values. `ProductionDayIds` must support more than one site so configured cross-site source populations do not require a browser-side site/calendar assumption. Source selection remains the exact `(MachineId, ProcessorId)` authority established by FC-028.

The operation must preserve the existing FC-028 reporting rules:

```text
exact source selection
exact metric key/version selection
canonical context and status filtering
deterministic authoritative ordering
opaque complete pagination
provider-window validation
```

Its continuation fingerprint must bind the canonical production-day identities, including `SiteId` and `BusinessDate`, in addition to sources, metrics, context, statuses, and ordering. Tokens from the existing UTC-interval shift operation and the new production-day shift operation are different query identities and must be mutually incompatible.

The existing UTC-interval shift query remains a valid FC-028 operation for callers whose intent is an absolute interval. It must not be used by FC-029.3 to approximate production-day membership.

FC-029.3A.1 is not closed by this inventory. It is closed only when the chosen authoritative roster representation and the production-day shift reporting selector are implemented and prove machine applicability, zero-evidence occurrence selection, overnight/DST ownership, cross-site selection, conflicting-ownership rejection, metric-independent selection, and continuation incompatibility. React remains blocked until then.

### FC-029.3A.1A — provider-neutral roster contracts and persistence

3A.1A is complete. It establishes durable resolved roster coverage without resolving schedules, validating FC-025 facts, or changing FC-028.

The durable coverage unit is one complete machine/production-day snapshot:

```text
MachineShiftOccurrenceRoster
  MachineId
  ProductionLineId
  ProductionDayId
  Revision
  Occurrences[0..N]
```

Each occurrence is authoritative applicability and ownership data:

```text
MachineShiftOccurrenceOwnership
  MachineId
  ProductionLineId
  ShiftOccurrenceId
  ProductionDayId
```

An existing roster with zero occurrences means the machine/day was authoritatively resolved with no applicable shifts. An absent roster means coverage has not been resolved. Those states are never interchangeable.

Roster commits publish a complete replacement snapshot atomically under an expected revision. The store first compares the expected revision with its authoritative current snapshot. Only after that CAS comparison succeeds does the store require initial revision one or exactly one revision of advancement. Commits reject stale revisions before invalid successor transitions, and reject duplicate occurrence identities, mismatched machine/line/day entries, cross-site shift/day ownership, and one `ShiftOccurrenceId` assigned to conflicting production days. Occurrence order is canonical and does not depend on caller enumeration order.

`ProcessorId` is deliberately absent. It remains FC-027/028 projection source identity rather than factory scheduling identity.

3A.1A does not decide when or how schedules are resolved. That belongs to 3A.1B. It also does not make metric-input evidence a prerequisite for roster coverage.

### FC-029.3A.1B — authoritative roster materialization and composition

3A.1B resolves one explicitly requested production-day identity for one configured machine scheduling scope:

```text
MachineShiftScheduleScope
  MachineId
  SiteId
  ProductionLineId
            +
ProductionDayId
            ↓
ShiftOccurrenceResolver
            ↓
complete MachineShiftOccurrenceRoster
            ↓
IMachineShiftOccurrenceRosterStore
```

The materializer calls the existing authoritative resolver with exact site, production line, and half-open business-date selection. It does not derive UTC day boundaries. Line assignments retain precedence over site assignments according to the existing resolver, while overnight and DST boundary behavior remain resolver-owned.

Every resolved occurrence must belong to the requested site, production day, and either the requested line or its authoritative site-level fallback. Resolver output is converted to complete `ShiftOccurrenceId` values and persisted with the requested machine applicability and exact `ProductionDayId` ownership.

Materialization semantics are:

```text
no existing coverage + resolved 0..N occurrences
  → publish complete revision 1

equivalent existing coverage
  → return existing snapshot without revision advancement

changed authoritative resolution
  → CAS-publish one complete successor revision

stale CAS / conflicting existing line / invalid resolver output
  → fail without repair or partial publication
```

Edge composition derives `MachineShiftScheduleScope` values from the same validated per-machine production-processing configuration already used by FC-025. It exposes a machine-keyed materialization runtime set. `ProcessorId` remains absent.

Production composition owns one explicit finite startup request:

```text
ProductionProcessing:RosterMaterialization
  FromProductionDayInclusive
  ToProductionDayExclusive
```

When the selected persistence provider exposes the roster capability, the startup worker materializes every configured machine for every business date in that half-open range before the production metric-input background worker starts. It never reads the clock, guesses today, or expands the range. A failure for any requested machine/day escapes startup and remains visible; incomplete coverage is not silently accepted. Existing CAS conflicts are not weakened or hidden. FC-025/026-only compositions whose provider does not expose roster persistence retain their existing processing lifecycle and do not advertise roster coverage.

The bounded startup command may be rerun safely because equivalent complete snapshots are idempotent. 3A.1B introduces no perpetual scheduler and no reporting-triggered writes; FC-028 remains read-only.

## FC-029.4 — Daily Report / Print Surface

FC-029.4 is a presentation-only composition over the authoritative Production-Day Overview and Shift Performance result surfaces. It introduces no new reporting truth, metric calculation authority, schedule/calendar reconstruction, current-state inference, acquisition dependency, or write-side persistence behavior.

### Final decomposition

```text
FC-029.4A    Contract / Repository Inventory          CLOSED / FROZEN

FC-029.4B    Daily Report Implementation              CLOSED / FROZEN
  4B.1       Composition / Presentation Model         CLOSED / FROZEN
  4B.2       Lifecycle / Query Orchestration          CLOSED / FROZEN
  4B.3       Surface / Navigation / Print             CLOSED / FROZEN

FC-029.4C    Whole-Feature Conformance                CLOSED / FROZEN
FC-029.4D    Final Closure / Documentation            CLOSED / FROZEN
```

### Closure invariants

The Daily Report composes the same selected production day and the same snapshotted configured reporting-source population through the existing authoritative production-day and roster-authoritative shift projection paths. Cross-input day/population disagreement is therefore not a public input-contract branch; defensive composer guards remain present against future projector drift.

The production-day and shift reporting reads are independent and are not claimed to share an atomic database snapshot. Publication is nevertheless all-or-nothing at the Daily Report model boundary: both authoritative traversals must complete before composition, a successful sibling result is never published independently after the other read fails, and a superseded generation cannot publish after its successor owns the lifecycle.

The presentation model preserves authoritative metric states and lineage without arithmetic:

```text
calculated
unavailable
insufficient-evidence
missing
```

Calculated zero remains calculated zero. OEE and the other metric values are projected as supplied rather than recomputed, normalized, averaged, ranked, or inferred from other cells. Production-day source revisions and roster-authoritative shift occurrence identity/source revision remain visible in the composed model where supplied.

Lifecycle publication and print authority are closed:

```text
current successful generation   → model visible and printable
same-day refresh                → previous model may remain visible, stale, non-printable
same-day expected failure       → previous model may remain visible, stale, non-printable
changed production day          → prior-day model is not carried into the new selection
loading/failure without prior   → no report model is exposed
superseded generation           → cannot publish
```

The canonical route remains:

```text
/production-days/{productionDay}/report
```

Print authority belongs only to the current successful generation. The print surface consumes the already-published Daily Report model; it does not perform a second reporting read or recalculate report content.

### Whole-feature dependency boundary

The executable Daily Report production boundary is frozen over:

```text
src/application/daily-report-lifecycle.ts
src/application/use-daily-report.ts
src/presentation/daily-report-composer.ts
src/presentation/daily-report-page-policy.ts
src/presentation/DailyReportPage.tsx
src/application/daily-report-navigation.ts
```

Whole-feature conformance compares the exact imports for each module, including normal imports, type-only imports, side-effect imports, and re-exports. The closed dependency direction excludes acquisition, current-state, persistence/SQL, schedule/calendar reconstruction, write-side processing, and metric-calculation authority.

### Final closure evidence

The final independently verified FC-029.4 evidence is:

```text
daily-report-composer.test.mjs                     9 / 9 PASS
daily-report-lifecycle.test.mjs                    9 / 9 PASS
daily-report-surface.test.mjs                      4 / 4 PASS
daily-report-react-surface.test.mjs                7 / 7 PASS
daily-report-whole-feature-conformance.test.mjs    1 / 1 PASS

npm run typecheck                                  PASS
npm test                                           348 / 348 PASS
npm run build                                      PASS
npm run contracts:check                            PASS
git diff --check                                   CLEAN
```

The earlier Medium dependency-proof finding is closed: the original global union allowlist was replaced by exact per-module dependency assertions using the established FC-029.3 import-extraction pattern. No production code change was required by that correction.

FC-029.4A through FC-029.4D are closed and frozen. Future work must not reopen Daily Report semantics implicitly; any new calculation authority, current-state behavior, schedule reconstruction, persistence coupling, or write-side interaction requires a separately authorized feature boundary.

## Deferred boundaries

The following remain outside the completed FC-029.2 feature and are not to be inferred from operational metric reporting:

```text
current machine status
Running / Idle / Fault / Alarm / Online / Offline inference
browser MTConnect acquisition
live/current-state reconstruction
frontend metric aggregation
production-day reconstruction from shift values
machine ranking
charts/trends
operator/downtime editing
incentive reporting
```

Machine Status requires an explicit authoritative current-state read model/API before dashboard implementation.

Daily Report / Print Surface follows Shift Performance so it can compose authoritative Production-Day Overview and Shift Performance result surfaces without introducing calculation authority.
