# FC-028 — Reporting API

FC-028 exposes FactoryConnect's durable operational-metric reporting projections through a stable, query-oriented HTTP API. The API reads persisted FC-027 projections and never reconstructs factory history or recalculates metrics.

## Architecture

The reporting path is:

1. FC-026 produces durable aggregation revisions.
2. FC-027 evaluates exact versioned metric definitions and commits durable projections.
3. An `IOperationalMetricReportingQueryProvider` reads the selected provider's projection snapshot.
4. `OperationalMetricReportingQueryReader` applies canonical filtering, ordering, and seek pagination.
5. `OperationalMetricQueryReader` maps scalar summaries into the HTTP-neutral query model.
6. The v1 reporting endpoints bind requests and serialize response DTOs.

Controllers and endpoints do not calculate metric values.

## HTTP surface

The initial versioned operations are:

- `POST /api/reporting/v1/operational-metrics/shifts/query`
- `POST /api/reporting/v1/operational-metrics/production-days/query`

Both operations require exact machine/projection-processor source identities. Optional metric selection uses exact metric key and definition version pairs. Period ranges are half-open, continuation tokens remain opaque at the transport boundary, and successful empty queries return an empty page rather than a not-found response.

Invalid request shapes and typed cursor failures are returned as stable RFC Problem Details responses. Unexpected provider or runtime failures are not reclassified as client errors.

## Composition

`AddFactoryConnectOperationalMetricReporting()` registers the provider-neutral reporting reader chain. The selected persistence provider must expose the `OperationalMetricReportingQuery` capability; persistence activation owns the provider binding.

The API and Edge hosts use the same known-provider registration helper before selecting the configured provider. The API defaults to `InMemory`, then maps the reporting endpoints only after persistence selection and reader-chain registration. The in-memory provider is process-local, so it is intended for conformance, development, and single-process composition.

The SQL Server provider is registered and selectable, but remains core-only: it does not yet expose FC-027 projection durability or the FC-028 reporting-query capability. Selecting SQL Server for the reporting API therefore reaches explicit capability validation and fails for the missing `OperationalMetricReportingQuery` capability. It never falls back to an in-memory reporting store.

## End-to-end conformance

The FC-028.6 composition fixture hosts the actual API `Program`, commits `OperationalMetricProjection` instances through the selected provider's durable projection-store contract, traverses the real reader chain, calls both real ASP.NET endpoints, and verifies the JSON response retains:

- machine and projection-processor identity;
- typed period identity;
- exact metric definition version;
- status, value, and unit; and
- the FC-026 source revision.

The fixture also proves two-machine source isolation and opaque seek-pagination traversal without duplicates or omissions. A separate composition case proves that selecting the registered SQL Server provider fails specifically because its reporting capability is absent.

Provider-specific query implementations must continue to pass the shared FC-028.2 reporting-query conformance suite.

## Explicitly deferred

FC-028 does not add dashboards, charts, exports, authentication policy design, live streaming, supervisor editing, downtime-reason workflows, AI analysis, cross-machine aggregation, metric recalculation, or SQL-backed FC-027/028 persistence.
