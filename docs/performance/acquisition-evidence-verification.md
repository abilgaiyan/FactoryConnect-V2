# Acquisition evidence verification: A1/A2 comparison

This change keeps the existing acquisition transaction, lock hints, publication order,
V1 evidence format and full predecessor-prefix verification. No production action is included.

## A1 instrumentation

The `FactoryConnect.Acquisition` ActivitySource is opt-in. Enable its Edge log sink
before launching the development Edge process:

```powershell
$env:AcquisitionDiagnostics__Enabled = 'true'
[Environment]::SetEnvironmentVariable('Logging__LogLevel__FactoryConnect.Acquisition.Timing', 'Information', 'Process')
```

Each acquisition commit has a trace ID. Nested spans record transaction open/lifetime,
reconciliation, raw-state writes, evidence append, prefix reading/decoding/hash checks,
historical source verification, commit and rollback. Source-verification spans record
publication count, observation-occurrence count and actual attempted SQL command count.
A missing `Completed=True` means that stage did not reach its success boundary.
No SQL text, connection strings, observation values or evidence payloads are logged.

Transaction lifetime is not exclusive-lock duration: raw-row X locks start at inserts
and remain through commit/rollback. The raw-write start through transaction end is an
upper bound on the first raw-row X-lock interval, not a direct lock measurement.
Do not add nested span durations to their parent duration.

## Repeatable comparison

Run the same SQL integration workload on the A1 commit and on A2, with the same
SDK, SQL instance, machine resources, configuration and logging settings. Restore
NuGet dependencies first. The tests use disposable migrated databases and output
command counts and timings through the test runner; they do not use the factory DB.

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter 'FullyQualifiedName~SqlServerAcquisitionPersistenceDiagnosticsTests' --logger 'trx;LogFileName=acquisition-diagnostics.trx'
```

Record full request/transaction timings and source SQL counts independently. Compare
matched cold/warm runs and report memory pressure alongside durations. No runtime
performance result is established merely by source inspection or passing assertions.

A3 clone validation must reproduce the preserved historical acquisition workload on
an explicitly disposable restored GG-003 database, preserving hashes, identities,
checkpoints, contact authority, replay and corruption rejection. Capture concurrent
observation-reader completion, SQL waits and lock duration as well as transaction time.
No clone or factory database is changed by these instructions automatically.

## A2 bounded source reads

For each historical publication, fetch distinct sequence identities in chunks of
128, with exact MachineId + binary StreamKey + InstanceId predicates. Each row
contains position and all content-equivalence fields. Compare every original
occurrence after fetching, including duplicates; only deduplicate the SQL lookup
keys. Keep boundary-existence checks and the accepted-position coverage/order rules.
Every publication in the requested prefix is still read and verified.

Source-verification command counts for successful history are:

- A1: `2 * observation occurrences + non-null publication boundaries`.
- A2: `sum(ceil(distinct sequences per publication / 128)) + non-null publication boundaries`.

Both exclude the single prefix SQL command and normal acquisition reconciliation/DML.
The deterministic 3-publication / 257-observation-per-publication workload therefore
expects 1547 source commands on A1 and 14 on A2. These are expected algorithmic counts,
not measured results until SqlClient instrumentation executes against SQL Server.
The diagnostics test includes the same synthetic workload on both commits and emits
actual command counts, source-stage time, raw-write time and transaction lifetime.
The A2-specific test asserts actual captured commands and parameter bounds.

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter 'FullyQualifiedName~SqlServerAcquisitionBatchedVerificationTests|FullyQualifiedName~SqlServerAcquisitionPersistenceDiagnosticsTests|FullyQualifiedName~SqlServerAcquisitionEvidenceIntegrationTests|FullyQualifiedName~SqlServerAcquisitionContactAuthorityIntegrationTests|FullyQualifiedName~SqlServerObservationIngestionStoreConformanceTests' --logger 'trx;LogFileName=acquisition-verification.trx'
```

Existing rollback-at-each-table, concurrent replay/CAS, legacy opening, contact-only,
recovery and predecessor-corruption tests remain acceptance requirements. New cases
exercise chunk boundaries, very large unsigned identity values, older-prefix damage
on extension, exact stream/case/instance identity, duplicate/replayed occurrences,
post-write cancellation/rollback/restart and competing writers with larger histories.
Clone/production validation is a separate acceptance gate; no factory fix or duration
improvement is claimed by this PR before that gate is measured.

## Corruption classification contract

The original per-observation reader also throws `InvalidDataException` when a
persisted SignalType or Quality is not defined. Preserve its caller-specific behavior:

| Defect | Exact/recovery evidence read | Ingestion replay | New append from damaged predecessor |
| --- | --- | --- | --- |
| Missing/content-mismatched source (Boolean failure) | Corrupt | InvalidOperationException | InvalidDataException; whole transaction rolls back |
| Invalid persisted enum (materialization exception) | Corrupt | InvalidDataException | InvalidDataException; whole transaction rolls back |

The exact reader catches the enum exception; ingestion deliberately does not translate
it into a Boolean result. Tests must not normalize all commit failures to one exception
class. Targeted enum tests damage an older chunk in a disposable database, check all
five structures remain unchanged, then restore the row and trusted check constraint.

Duplicate exact `(MachineId, StreamKeyBinary, InstanceId, Sequence)` rows cannot exist
under the existing `PK_MachineObservation`. A separate SQL regression checks rejection
of such an INSERT without changing schema. The new dictionary's duplicate-row guard
is defensive fail-closed behavior for an invalid schema/result set; the old SingleRow
point reader did not establish equivalent behavior for a database with that primary
key removed. Do not claim that impossible-state guard has been SQL-exercised under the
valid schema, or weaken constraints in production to manufacture it.
