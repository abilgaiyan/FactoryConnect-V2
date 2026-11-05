# PR #162: reporting-only validated cut evidence reuse

Base: `df1bbac1c0bb5ab26d43af6d71d92c78d28bb9d2` (PR #161).
The accepted Phase A/Phase B amendment is implemented on the existing PR #162 branch. Runtime correctness and performance acceptance remain pending. The earlier non-null period-duration cache is replaced, not layered beneath this implementation.

## Implemented contract

`SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence` has a private constructor. Its full phase-A loader accepts the complete aggregation checkpoint and owner but NO requested period. All existing source reconstruction, units, transition-chain, R-prefix, delta/admission, standard inventory and canonical-outcome checks finish before it returns a proof. The cut-wide validation body and SQL/transition helper logic were preserved during extraction; a source comparison confirmed the validation body is unchanged apart from whitespace, and the SQL/helper tail is byte-identical to the earlier implementation.

Proofs hold the exact cut and owner, completed R, complete canonical sources and validated resolutions required by period derivation. Inventories stay private. Source contributing positions and outcome conflicting-standard identifiers are copied into owned read-only collections. Canonical source evidence contains immutable scalar/value members. No lazy SQL or deferred query escapes.

`SqlContainmentReportingReferenceTimeScope` is created only in `SqlContainmentVerifiedRead`. Its dictionary contains validated CUT EVIDENCE, keyed by full MetricAggregationCheckpoint (aggregation processor, machine/stream, position), fixed containment policy and `reference-time-cut-evidence-v1`. There is no period-result dictionary. Connection/transaction identity, active Serializable state, exact returned-proof identity and cancellation are checked on admission and hits. Overlap is rejected. Failures, partial loads and cancellation are not admitted. The transaction must not write any authority dependency while the scope exists; production reporting performs no such writes.

A complete empty or canonically unresolved history can now be reused. Empty query results alone are insufficient: every existing chain, prefix, delta and consistency check must pass. This caches a validated authority-evidence object, never an unavailable/insufficient metric result.

Phase B always runs independently. It selects exact period sources/outcomes, constructs a fresh binding and applies the unchanged completeness/duration rules. Null durations, resolved-subset rejection, period identity, source ordering, standard revisions, units and timestamp bounds remain as before. Every retained root still reconstructs and every canonical envelope is independently compared. Archive/evidence/witness/index/manifest verification remains unchanged.

Callers without a reporting scope execute uncached phase A followed by phase B. Publication/preparation and acquisition production paths do not allocate the reuse scope. No schema, SQL, isolation, gates, lock modes or timeout changes are included.

## Diagnostics: new evidence contract

On completed `containment.publication.verify` logs:

| Field | Definition |
|---|---|
| RootReconstructionInvocations | Every root reconstruction in this reporting verification |
| ReferenceTimeAuthorityValidationExecutions | Actual phase-A evidence-loader attempts, including failures when observable; excludes hits |
| ReferenceTimeAuthorityCacheHits | Complete cut-evidence reuse, including validated empty histories |
| ReferenceTimePeriodEvaluations | Every phase-B invocation after successful evidence access |
| ReferenceTimeEvidenceContract | `reference-time-cut-evidence-v1` |

The old `historical.reference-time.validate` span is replaced with `historical.reference-time.evidence.validate` and `historical.reference-time.period.evaluate`. Existing nested transition/outcome/semantic spans remain in phase A. Compare like stages explicitly; do not relabel the earlier period-duration-cache counts. Failed requests may have no final successful verification/counter record; retain errors and span traces.

From supplied PR161-profile-api.log: four requests, reporting.query median 27.17044165 s, ASP.NET median 27.17585265 s, 195 roots/request, 78 reference-time validations/request and 20 contribution-SQL executions/request. All 312 stopped outcome-read spans reported zero rows. The amended contract permits reuse only after independently proving those empty histories complete and consistent. No particular reduction count or latency is guaranteed; distinct reference-time cuts must be measured. Started reconstruction tags may inherit the publication cut before local tags are set; use stopped-span exact-cut tags. Nested durations must not be added.

## Added/adapted validation

- Calculated and unavailable OEE roots retain canonical equivalence with paired Performance/OEE authority reuse; forged envelopes reject after warming.
- Different requested periods share only cut evidence. A populated period, a no-source period and a repeated populated period each derive independent bindings/durations.
- Different complete source cuts and historical revisions remain separate; repeated numerical positions cannot grant cross-source authority.
- Empty, missing-identity, missing-standard and ambiguous canonical histories reuse validated evidence but independently return null duration. Canonical Performance/OEE envelopes remain equivalent for empty/unresolved evidence.
- Incomplete/pending empty histories, missing publication cuts, missing outcomes and missing standard history reject rather than become cached empty evidence; unrelated warm entries grant no authority.
- Corruption, failure and cancellation after loading do not populate entries. Wrong-cut proof admission and overlapping loads reject.
- Cancelled hits, foreign owner, committed/rolled-back/disposed transactions and stale scopes reject.
- An actual five-metric reporting verifier fixture asserts five roots, one evidence validation, one hit and two independent period evaluations.

These tests are written, NOT claimed passed. The editing environment has no dotnet executable or Windows LocalDB. The attempted `dotnet build FactoryConnect.sln -c Release` failed before compilation with exit 127 (command not found). Whitespace checks pass.

## Developer validation commands

Use SDK 10.0.402 and an isolated checkout of this PR branch. Record actual commit, clean status, configuration and executable paths. Do not use stale binaries.

```powershell
dotnet build FactoryConnect.sln -c Release
if ($LASTEXITCODE) { throw 'Build failed' }
dotnet test tests/FactoryConnect.Core.Tests -c Release --no-build --logger 'trx;LogFileName=PR162-Core.trx'
if ($LASTEXITCODE) { throw 'Core tests failed' }
$env:FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true'
dotnet test tests/FactoryConnect.Integration.Tests -c Release --no-build --filter 'FullyQualifiedName~SqlContainment|FullyQualifiedName~SqlServerOperationalMetricProjection|FullyQualifiedName~ProductionDayShiftOperationalMetricEndpointTests|FullyQualifiedName~SqlServerProductionDayShiftOperationalMetricConformanceIntegrationTests' --logger 'trx;LogFileName=PR162-SQL.trx'
if ($LASTEXITCODE) { throw 'SQL regressions failed' }
dotnet test tests/FactoryConnect.Edge.Tests -c Release --no-build --logger 'trx;LogFileName=PR162-Edge.trx'
if ($LASTEXITCODE) { throw 'Edge regressions failed' }
```

The selected SQL fixtures create/drop uniquely named disposable databases. Use only the developer SQL instance. Existing reporting and acquisition/publication regression suites remain gates.

## Controlled clone benchmark, still pending

Follow target/schema/payload guards in `gg003-multi-shift-reporting.md`: confirmed disposable developer restores only, actual current migration ledger/checksums through 022, exact executable and effective SQL target, same verified 16 October request and complete parsed JSON comparison. Never use Gajra, a protected reference database or `GG003_ACQ_A2_20261026`. This editing session cannot access or validate the developer clone; do not assume the historical PERF clone is suitable.

Use the same four-request protocol for PR #161 main and this candidate, preserving warm-up labels and every raw server duration. Enable `ReportingDiagnostics:Enabled=true` with Information timing logs. Record root reconstructions, actual evidence validations, cache hits, period evaluations, distinct complete cuts, evidence-contract version, verification/transaction/query/ASP.NET durations, complete response equivalence and peak process memory. Do not use contribution SQL count as a substitute for distinct reference-time cuts. Capture failures/retries/cancellation without changing timeout values.

Expect necessary root reconstruction and independent period evaluation to remain. Evidence validation may decline when complete cuts repeat. Acceptance requires measured lower median and semantic equivalence; no improvement is claimed here. Concurrent acquisition/reporting blocking and completion are a separate disposable-clone validation phase and remain pending.

Keep PR draft until compilation, SQL regressions, semantic equivalence and controlled measurements are reviewed. Do not merge, deploy or poll CI.
