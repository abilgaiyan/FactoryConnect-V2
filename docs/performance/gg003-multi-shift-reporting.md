# GG-003 source-scoped multi-shift reporting validation

## Status and consistency boundary

Base: `a45c546be7b1de892918c551cf9056745ae56dff` (current main fetched before implementation).
Source inspection confirms production-day roster -> per-shift report -> summary -> stable transaction -> complete containment verification. The former loop verified the retained publication separately for every shift.

The new path resolves all authoritative rosters first, groups by exact reporting source (processor and machine), and calls a multi-period summary read. SQL opens one connection and the existing stable transaction per source, invokes the unchanged complete verifier, filters all requested periods/context and materializes summaries before commit. Report mapping uses those immutable summaries and performs no further SQL. Per-period revision coherence and duplicate-definition rejection remain in the shared report materializer. Distinct historical revisions across different shifts remain valid.

This deliberately changes the consistency boundary: all shifts/days for one source within one request see one stable publication instead of successive per-shift publications. Different sources still have independent transactions; this does not promise a cross-source snapshot. Roster authority is read before metric authority, as before, without claiming roster/metric transactional coupling. A source failure fails the entire request; earlier source results are never returned as a successful partial page.

Existing single-period/detail APIs and HTTP DTOs remain. The interface batch defaults preserve compatibility for existing custom providers/readers by sequential delegation; SQL overrides with one stable transaction and the in-memory provider overrides with one lock. The one-verification guarantee applies to the SQL provider, not arbitrary external implementations of the defaults. Defensive stable-read retries remain intact: a non-participating writer can still cause a retry; ordinary participating publication requires one verification per source.

No changes to archive/evidence/witness/index/manifest validation, codecs, isolation, locks, schema, timeout, acquisition production logic, or cross-request caching.

## Developer build and regressions (PowerShell)

Run from an isolated checkout of the PR branch, not PR #159/#160's working tree. Confirm SDK `10.0.402` with `dotnet --version`. Do not accept a stale build. Record `git rev-parse HEAD`, `git status --short`, build log and TRX under `artifacts/TestResults`.

```powershell
dotnet build FactoryConnect.sln -c Release
if ($LASTEXITCODE) { throw 'Build failed' }
dotnet test tests/FactoryConnect.Core.Tests -c Release --no-build --logger 'trx;LogFileName=reporting-core.trx'
if ($LASTEXITCODE) { throw 'Core regression failed' }
# These selected SQL fixtures CREATE and DROP uniquely named disposable databases.
# Use the developer LocalDB instance only; the catalog below is master, not a reference DB.
$env:FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true'
dotnet test tests/FactoryConnect.Integration.Tests -c Release --no-build --filter 'FullyQualifiedName~SqlContainmentHistoricalLoadCountIntegrationTests|FullyQualifiedName~SqlContainmentVerifiedReadIntegrationTests|FullyQualifiedName~SqlServerOperationalMetricProjectionSummaryReaderIntegrationTests|FullyQualifiedName~SqlServerOperationalMetricProjectionStableReadIntegrationTests|FullyQualifiedName~ProductionDayShiftOperationalMetricEndpointTests|FullyQualifiedName~SqlServerProductionDayShiftOperationalMetricConformanceIntegrationTests' --logger 'trx;LogFileName=reporting-sql.trx'
if ($LASTEXITCODE) { throw 'Reporting SQL regression failed' }
```

New coverage includes source grouping across three shifts and multiple days, separation of different sources, batch dispatch without single-shift/detail calls, missing reports, distinct historical revisions, duplicate definitions, foreign processor rejection, and cancellation. Real SQL coverage checks one verification and one historical load per exact retained cut in one transaction; its three-period selection includes two empty shifts. Existing corruption cases now exercise the batch SQL path as well. The partial-publication cancellation test also exercises the batch path. Existing endpoint/query suites retain filtering, ordering, pagination and roster conformance coverage.

## Controlled restored-clone comparison: required preflight

No restored-clone benchmark has been run for this change. This Linux editing environment has no `dotnet` executable or Windows LocalDB; the attempted test command failed before compilation. Whitespace checking is not compilation acceptance.

Do not connect to Gajra. Never target `GG003_ACQ_A2_20261026` (advanced ordinal 40,546), a protected reference restore, or an unknown database. `FactoryConnect_GG003_PERF_20261025` is only a historical candidate; its current state and ownership have NOT been verified.

1. On the developer machine, confirm the candidate is disposable and idle. Preserve the reference backup. Restore two new disposable copies from the same verified backup, with new MDF/LDF paths and explicit names for baseline/candidate. Record backup identity and restore commands. Do not overwrite any existing database.
2. Read `@@SERVERNAME`, `DB_NAME()`, database state and `dbo.FactoryConnectMigrationHistory` (MigrationId, Name, CanonicalChecksum, AppliedAtUtc). Record publication processor bindings/checkpoints, aggregation checkpoint, roster coverage and source revisions. Refuse any target mismatch.
3. Both binaries must validate the same schema, including migration 022. A maximum migration ID alone is insufficient: verify the full repository ledger/checksums using the existing startup validation. If an old backup needs migration, migrate ONLY the newly created disposable copies with the repository migration executable and repeat schema validation. Never migrate a reference database. Record all migration results before HTTP requests.
4. Build baseline `a45c546...` and the candidate in separate worktrees. Record exact commits, SDK, Release build output, actual executable paths, effective configuration, SQL instance/catalog, and API port. Keep acquisition stopped for the fixed-state response comparison. Do not benchmark a build that failed or a server whose executable/connection target is unconfirmed.

Read-only inventory queries (execute only after confirming the developer SQL instance/catalog):

```sql
SELECT @@SERVERNAME AS SqlServer, DB_NAME() AS DatabaseName, SYSDATETIMEOFFSET() AS CapturedAt;
SELECT MigrationId, Name, CanonicalChecksum, AppliedAtUtc
FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;
SELECT pp.ProcessorKey, pp.PublicationFormatVersion, pp.EvaluationPolicyId,
       s.MachineId, s.StreamKey, ap.ProcessorKey AS AggregationProcessorKey
FROM dbo.OperationalMetricProjectionProcessor pp
JOIN dbo.MetricAggregationProcessor ap ON ap.MetricAggregationProcessorRowId=pp.MetricAggregationProcessorRowId
JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=pp.MetricInputStreamRowId;
SELECT * FROM dbo.OperationalMetricProjectionCheckpoint;
SELECT * FROM dbo.MachineShiftOccurrenceRoster;
SELECT * FROM dbo.MachineShiftOccurrenceRosterOccurrence;
```

Use the inventory and actual commissioned configuration to select the exact containment processor, machine, site, business date and context. Do not select a processor by approximate name or invent one. `<preserved-16-Oct-request.json>` was a placeholder. Search the developer's saved request captures first. If absent, reconstruct a real JSON payload against `ProductionDayShiftOperationalMetricQueryRequest` in `src/FactoryConnect.Api/Reporting/OperationalMetricHttpContracts.cs`:

- `sources`: exact `machineId`, `processorId`, `siteId`, `businessDate` (`2026-10-16`) verified above.
- `context`, `metrics`, `statuses`, `pageSize`, `continuationToken`: recover the original values from configuration/captures; record any reconstruction assumption. Do not assume null context equals a previously partitioned request.
- Endpoint: POST `/api/reporting/production-day-shifts/query`.

Save the actual payload and full baseline response before the candidate run. Until identity/context and the original complete response are recovered, the rounded historical values below are only a partial cross-check, not full response-equivalence acceptance.

## Measurement protocol

Use identical effective configuration except executable and disposable database name; no timeout/isolation changes. Select SQL persistence with `Persistence:Provider=SqlServer` and set `PersistenceProviders:SqlServer:ConnectionString` to the confirmed clone. Enable `ReportingDiagnostics:Enabled=true`, Information-level `FactoryConnect.Reporting.Timing` and ASP.NET request logging, with trace IDs/scopes visible. Run APIs only on loopback ports and verify process executable/configuration before requests.

1. Run one warm-up and three serial measured identical requests per build, alternating build order when practicable. Do not clear shared server caches. Preserve JSON responses, HTTP status, server logs and client elapsed time separately.
2. For each request trace, count **started** `containment.publication.verify` spans per exact processor (not both started/stopped records). Expected ordinary case: baseline three for three shifts, candidate one per source. Count `historical.read.validate`, `historical.sql.execute`, and `historical.result.reconstruct` separately. Historical loads remain per distinct exact retained cut, not one total load for all revisions. Record retries rather than concealing them.
3. Capture stopped span durations for verification, historical reads/reconstruction, `reporting.transaction.lifetime`, gate wait and commit, plus `reporting.query` and ASP.NET `Request finished`. Retain trace/span IDs. Report each run and median; do not sum nested spans as request duration.
4. Parse responses and recursively compare every field/value, preserving array order and numeric precision. Object property order can be ignored. Compare all shift/source identities, context, metric versions/status/value/unit/reasons, publication metadata and revision identities, roster/line ownership, missing metrics, pagination and continuation token. Byte comparisons are supplementary only. Test filtered and paged queries as separate identical paired requests.
5. Historical acceptance cross-check: all three shifts, revisions 749/1086/1274; Utilization approximately 17.03%/18.09%/13.29%; SHIFT-1/2 Availability unavailable with RunningOutsidePlannedProduction; Performance missing reference time; Quality missing good quantity; OEE unavailable/insufficient evidence as recorded; continuationToken null. Investigate mismatches against the complete saved response rather than accepting rounded KPIs.
6. Concurrent comparison is a SEPARATE disposable restored-clone phase: restore equivalent starting states again. Replay the same bounded, recorded acquisition input into each clone using an already validated local harness and confirmed target guard. Never use a live CNC or the advanced A2 database. Start one reporting request at the same controlled publication stage in each run. Observe SQL wait/blocking sessions, request/transaction ages, acquisition commit/checkpoint progress and eventual completion; save errors, deadlocks and cancellation results. Do not infer overlap from simultaneous launch alone. If no safe deterministic harness/input is available, leave this phase pending instead of borrowing PR #160's unresolved experiment as acceptance.
7. After overlap, verify publication integrity, writer completion and reporting completion with no partial success. Record source cuts at request boundaries; responses during advancement may legitimately differ between runs. Use the fixed-state phase for strict response equivalence.

Historical figures (NOT current post-fix measurements): reporting.query 97.893 s; ASP.NET 98.924 s; PowerShell 127.685 s. Use fresh paired server durations for improvement claims. The historical client/server gap remains unexplained.

## Acceptance record

Record build/tests, clone identity/schema/configuration, recovered payload and complete response comparison, verification/load/reconstruction counts, server durations and overlap results independently. Keep this PR draft until compilation/regressions and developer-clone evidence are reviewed. CI should not be polled; the user will supply results. No merge or production deployment is authorized.
