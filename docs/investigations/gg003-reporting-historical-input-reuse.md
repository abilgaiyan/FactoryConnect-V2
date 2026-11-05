# GG-003 reporting recovery: transaction-scoped historical inputs

Status: source correction prepared for review; SQL validation and release are pending.

Base: `cada09dc056f6f9cfaa422d0af077140d053ca91` (`main`). This recovery branch
is independent of draft diagnostic PR #151. R3.1B remains paused.

## Problem and correction

The accepted offline evidence showed five historical-contribution executions taking
126.95 seconds during an advancing publisher's Exclusive gate ownership. A concurrent
reporting read timed out. That establishes harmful repetition during gate ownership;
it does not establish why an individual SQL execution is slow.

Source inspection identifies repeated contribution reads across retained-result archive
verification, result reconstruction, and reference-time verification. Reporting's initial
checkpoint read also verifies the complete retained format-2 publication.

`SqlContainmentHistoricalReadScope` reuses only fully loaded, independently validated
historical inputs. Its first access calls the unchanged
`SqlContainmentHistoricalContributionReader.ReadAsync`, including processor/stream
binding, exact revision, membership, fact materialization and aggregate integrity checks.
Only successful completion is admitted; SQL exceptions, integrity failures and cancellation
retain their identities and do not populate the cache. The collection is copied into an
owned read-only wrapper before admission. Fact and identity objects are immutable records.

The key is the value-equal `MetricAggregationCheckpoint`: aggregation processor plus
stream (machine and exact stream key) plus position. Case-distinct strings remain distinct.
There is no latest-revision substitution or prefix reuse between revisions 534 and 537.

## Ownership and affected callers

| Caller | Lifetime and use |
| --- | --- |
| `SqlContainmentVerifiedRead.TryReadAsync` | A fresh scope per verification attempt, shared across retained archive verification and subsequent independent result reconstruction. |
| `SqlServerOperationalMetricContainmentPublisher.PublishAsync` | One scope in its preparation transaction, shared across proposed projections. |
| `SqlContainmentPublicationTransaction.PublishAsync` | A different scope created after Exclusive acquisition, shared across retained and incoming archive verification. |
| `SqlContainmentArchiveTransaction.PrepareAsync` | Accepts the owner's scope; direct callers receive a fresh scope. All existing archive locks, comparisons and DML ordering remain. |
| `SqlContainmentHistoricalResultValidator` | Re-evaluates every key independently; nested component reads and reference-time verification use the same input scope. |
| `SqlContainmentReferenceTimeAuthorityValidator` | Reuses source membership only. Period-specific A/R/S authority and reference-time decisions are reconstructed on every invocation. |

The availability-only authority seam has no production caller in the SQL persistence
assembly and is unchanged. Public interfaces and the legacy format-1 path are unchanged.

The scope checks reference identity of the SQL connection and transaction at entry, and
requires the same open Serializable transaction on cache hits as well as misses. A completed
transaction cannot serve cached data. It does not own or dispose the caller's SQL resources.
Overlapping scope reads are rejected rather than issuing concurrent SQL on that transaction.

These scoped callers write containment publication/archive tables only. They must never
write source facts, contribution membership or aggregation revisions while using a scope.
Serializable locks retained by the first load protect the validated range from concurrent
writers; transaction-local writes to that range are outside this scope's contract.

## Preserved invariants and limitations

- Every result is independently evaluated and canonically compared. No metric result,
  witness conclusion, reference-time authority decision or period aggregate is cached.
- Preparation and atomic publication have separate connections, transactions and scopes.
  Reporting attempts and requests also have separate scopes. No static or ambient cache exists.
- Exclusive/Shared gate modes, acquisition order, transaction isolation, 15-second gate timeout,
  SQL query text, indexes, migrations, checkpoint publication and rollback are unchanged.
- Exact replay still checks the complete immutable archive and retained publication. Incoming
  claims never supply trusted historical inputs. There is no legacy fallback or repair.
- The scope retains one collection per distinct requested cut until the operation ends.
  Large retained publications can therefore increase peak memory; measure this during validation.
- A single historical SQL query can still be slow. Reduced execution count is not proof of
  recovered report latency or of the SQL latency's underlying cause.

## Validation

The database-free `ContainmentHistoricalInputReuseTests` cover exact-cut reuse, all four
identity dimensions, owned/read-only membership, separate caches, failure retry,
cancellation before access and after loading, and overlapping-use rejection.

The prepared SQL tests in `SqlContainmentHistoricalReadScopeIntegrationTests.cs` extend
the existing historical-result fixture. They cover canonical equivalence across every built-in
definition and both period types, forged claims after warming, transaction/connection mismatch,
completed owners, weak isolation rejection and corrupt first-load evidence.

After separate SQL authorization, run those tests and existing historical authority/result,
archive, publication and verified-read integration suites. Those existing suites exercise
exact replay, corruption rejection, every publication rollback stage, concurrent publication,
source advancement and coherent reads through the modified production callers.

Build and database-free validation only:

```powershell
dotnet build tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed; tests stopped.' }
dotnet test tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj `
  -c Release --no-build `
  --filter '(FullyQualifiedName~ContainmentHistoricalInputReuseTests|FullyQualifiedName~ContainmentHistoricalSqlCounterTests)&Category!=SqlServerIntegration'
if ($LASTEXITCODE -ne 0) { throw 'Database-free tests failed.' }
```

Review commits use `[skip ci]`: the repository's ordinary PR workflow also starts SQL
integration jobs, which are not authorized for this correction yet. This does not waive
those checks before merge/release; execute them only after explicit approval.

### Preparation results

- .NET SDK 10.0.402, Linux: integration-test project Release build passed with analyzers enabled.
- Database-free input-reuse and synthetic counter tests: 17/17 passed (14 cache-policy cases and 3 counter cases).
- SQL integration tests: prepared, not executed. Their compilation is covered by the build.
- Independent code review, fresh-restore measurements and release acceptance: pending.

## Review follow-up: assert real historical SQL execution counts

`SqlContainmentHistoricalLoadCountIntegrationTests` adds two SQL regression tests:

- A five-result batch through the actual containment publisher must execute one contribution
  query in preparation and another in atomic publication, on two distinct transactions.
  Exact replay must execute two more queries on two new transactions despite verifying
  retained and incoming results. Every (transaction, processor row, stream row, revision)
  group must contain exactly one execution.
- An advancing atomic publication with retained older-cut evidence and incoming newer-cut
  evidence must execute exactly two contribution queries in its one transaction. Detail
  reporting and checkpoint reporting must each independently execute those two queries
  again in separate transactions. Zero observations and extra loads fail the assertions.

`ContainmentHistoricalSqlCounter` is test-only SqlClient DiagnosticListener instrumentation.
It observes Before/After/Error events for the existing contribution query, filtered by a
unique test application name. Fixture setup uses another application and cannot satisfy
counts. It records transaction object identity and numeric processor/stream/revision identity;
no SQL, connection strings, credentials or fact payloads are emitted. It does not add a
production seam, replace the historical reader or change the SQL query. Test connections
use pooling disabled for observation isolation; this is not a latency benchmark.

Completed ExecuteReader calls are counted separately from pending/failed calls. The SQL
regressions await the whole publisher/read operation, assert no pending or failed observed
commands, and check returned reporting revisions. Thus a command event alone cannot certify
successful reconstruction. Three database-free synthetic-event tests cover completion/error,
pending counts, application/query filtering and disposal without opening SQL connections.

These SQL assertions are compiled but unexecuted. They establish the intended regression
contract, not passing SQL evidence. Fresh-restore command counts and performance must still
be measured independently after authorization. No production source changed in this follow-up.

## Fresh-restore acceptance (not authorized for execution)

1. Use a newly named disposable revision-534 clone; leave all preserved revision-537 clones unchanged.
2. Establish complete legitimate advancing-batch provenance and record exact processor/stream/cuts.
3. Observe no more than one contribution-reader load per distinct cut per scope. Preparation
   and publication must each perform their own first load; reporting must perform its own load.
4. Compare canonical output, witness/reference bindings and complete manifests against accepted
   evidence. Confirm coherent advancement, exact replay without DML and failure rollback.
5. Measure whole reporting latency, historical command count/duration, Exclusive acquisition-to-commit
   duration, concurrent Shared acquisition, and peak memory. Keep workload success, latency acceptance
   and diagnostic coverage outcomes separate.
6. If latency remains unacceptable after repetition is removed, investigate the individual SQL
   access path separately. Do not infer an index or gate-scope correction from this change.

No database execution, service restart, production deployment or Gajra change is part of
this implementation. Reports remain unrecovered until separately validated and released.

## Developer SQL evidence follow-up

Ajay supplied `sql-load-count.trx` and `sql-scope.trx` from local developer execution.
The execution-count regressions passed 2/2, with no failed or skipped tests.
The scope regressions passed 2/3: owner/lifetime rejection and corrupt-input/weak-isolation
rejection passed. The canonical-equivalence/forged-claim test reached its forgery setup
but failed while encoding a changed ratio value with unchanged operands, before the
historical-verification rejection assertion. This is a test-construction defect; it
does not establish a historical-verification failure or complete that regression.

The correction preserves the ratio and changes only the claimed source-authority binding.
An explicit codec decode check ensures the forged claim is internally canonical before
the warmed-scope historical verifier is invoked. This follows the existing canonical
forged-authority test pattern. The corrected test has not yet been built or rerun.
The supplied console output also reports six build warnings; warning details are pending.

These synthetic fixture results do not measure GG-003 report latency, Exclusive gate
duration or peak memory. Broader SQL conformance, independent review, fresh revision-534
restore validation and release acceptance remain pending. No assistant SQL execution
or production change was performed.


## Authorized CI and corrected local result — 24 October 2026

Ajay reported the corrected scope regression run passed 3/3, with zero failed or
skipped tests, and a successful build. Together with the inspected execution-count
TRX (2/2 PASS), the focused synthetic SQL regressions now pass locally.
The corrected run is user-reported console evidence; its TRX has not been inspected.

Ajay explicitly authorized ordinary repository CI, including disposable SQL integration
tests, and squash merge of PR #152 only if all checks pass. Earlier statements above
that SQL tests were unexecuted or CI was not yet authorized describe prior checkpoints.
This documentation commit intentionally omits the CI skip marker to start ordinary PR CI.
Full CI results are pending at this commit.

Fresh revision-534 restore performance validation and production deployment remain
separate and unapproved. Reports remain unrecovered; individual-query latency and
peak-memory impact remain unproven. Local validation artifacts belong under
`artifacts/TestResults`.
