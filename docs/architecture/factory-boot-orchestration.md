# RBS-13 — Factory boot orchestration

Repository implementation candidate; Windows executable acceptance and factory
commissioning remain open. Entry baseline: `b72b190a71d11c327554625e0b1fe9e480d5fefa`.

One boot task remains the authority. Acquisition retains ownership of FANUC,
SHDR readiness, MTConnect and `Verify-Acquisition.ps1`. The repository does not
rewrite or package those existing factory scripts. It relies on their established
contract: `Start-Acquisition.bat` exits zero only after acquisition verification.

`Start-FactoryConnectSystem.ps1` invokes that batch from the install root, waits
for its exit, and invokes no FactoryConnect runtime on nonzero exit. Only after
success does it validate `current` as one direct, 40-character release junction
inside `releases`, require `current/Start-FactoryConnectRuntime.ps1`, and invoke
that script with the install root. Runtime startup retains its existing ownership,
health, intent and deployment-lock authority. The orchestrator neither terminates
children nor interprets runtime ownership. It has its own exclusion lock across
the complete acquisition/runtime chain; it never holds the deployment lock while
calling RBS. Nonzero child exits propagate to the caller; selection/invocation
errors return nonzero. Unique `deployment/logs/system-<attempt>` folders preserve
acquisition output, runtime output and `system-start.json` phase/exit evidence.
These logs do not establish machine lifecycle or quantity authority.

The release builder requires and packages the orchestrator and its installer,
declares both in `release.json`, and includes both in the existing full manifest.
`Install-FactoryConnectSystemStartup.ps1` is an explicit commissioning operation:
under `deployment.lock`, it rejects unresolved runtime intent, resolves the current
release, validates metadata and the orchestrator's manifest hash, then atomically
copies those exact bytes to `<InstallRoot>/Start-FactoryConnectSystem.ps1`.
Identical bytes are an installation no-op. It does not invoke acquisition/runtime,
change selection, commission application configuration, migrate, or create tasks.
Normal deployment remains unchanged. On later orchestration upgrades, run the
reviewed installer again; moving `current` alone does not replace the root script.
Application releases can change without changing the task's action.

After review and factory authorization, the installation operation is:

```powershell
& D:\FactoryConnect\current\Install-FactoryConnectSystemStartup.ps1 -InstallRoot D:\FactoryConnect
```

The proposed stable task action is Windows PowerShell with arguments:

```text
-NoProfile -ExecutionPolicy Bypass -File D:\FactoryConnect\Start-FactoryConnectSystem.ps1
```

Keep the existing SYSTEM/Highest, boot delay and IgnoreNew policy. Task timeout
must be reviewed for the entire chain during commissioning; no task setting is
changed by this implementation. The installer is not called at boot. Do not add a
second delayed task as a substitute for acquisition success. First commissioning
should invoke the stable script on the running factory, verify acquisition success
and genuine RBS `AlreadyRunning`, then review a separately authorized reboot.
No factory action or reboot is included in this repository change.

Focused proof:

```powershell
.\tests\deployment\Invoke-FactoryBootOrchestrationConformance.ps1
```

The disposable Windows proof uses an acquisition batch and packaged RBS stub;
it proves orchestration ordering, exit propagation, missing/invalid selection,
missing runtime authority, runtime/config/acquisition preservation, exact stable
installation and replacement, manifest failure, unresolved-intent exclusion and
installer lock contention. The stub's `AlreadyRunning` output is delegation
proof only, not real-machine commissioning or a rerun of B01–B12. `-ContractOnly`
checks parse/package/scope contracts without claiming executable acceptance.
Factory acquisition, task commissioning and a natural reboot remain separate gates.
A2 and authoritative ProducedQuantity remain unchanged and blocked.

## P0: bounded SQL readiness before runtime launch

The SQL readiness change is isolated from reporting and machine processing.
After successful acquisition and selected-release validation, the orchestrator
loads `FactoryConnect.SqlReadiness.ps1` from the selected release. Both commissioned
files under `<InstallRoot>/config` (`edge.production.json` and
`api.production.json`) must specify SqlServer, a parseable non-placeholder
connection string, a server and a database. Both configurations are validated
before any SQL contact. The runtime launcher still performs its complete existing
commissioning, ownership and health validation; this gate does not replace it.

Each readiness pass opens a fresh authenticated connection for each configured
consumer and executes only `SELECT 1`. Authentication and TLS settings are preserved.
Pooling is disabled and connection/command timeouts are bounded by the remaining
shared deadline. The PowerShell gate invokes the selected release's self-contained .NET 10
`apps/sql-readiness/FactoryConnect.SqlReadiness.exe`. It uses the centrally pinned
Microsoft.Data.SqlClient version shared with runtime persistence. Dependencies,
including native SQL client support, are published and manifest-covered; no factory
NuGet installation or developer assembly is used. Connection strings travel only
through redirected stdin, never process arguments. The parent enforces cancellation
and the attempt deadline by terminating an unfinished probe. Success requires both targets to succeed in the same pass.

The default overall deadline is 180 seconds, configurable through
`-SqlReadinessTimeoutSeconds` (1–600). Attempts have a maximum five-second budget;
retry delay is two seconds, capped by remaining time. Function-level cancellation
interrupts retries and cancels SQL operations. Configuration failure, cancellation
or timeout fails closed before runtime invocation. Provider exception messages and
connection strings are not logged. `sql-readiness.json` records outcome, attempts,
elapsed time and separate Edge/API outcomes; `system-start.json` records the
SqlReadiness failure phase. Existing orchestration/deployment locks and runtime
process ownership remain unchanged. The orchestration lock remains held throughout
readiness; the runtime continues to obtain its own deployment lock.

The release builder requires, copies and manifest-covers the readiness helper and
records `sqlReadinessScript` in release metadata. Existing root orchestrator bytes
must be updated through the reviewed installer when this release is commissioned.
Existing releases without the helper fail closed if used with the new orchestrator.
No installer, deployment, scheduled-task change or factory reboot is part of this
source change. Review the scheduled task execution limit against the full chain
before separately authorizing commissioning.

Conformance commands (Windows PowerShell 5.1):

```powershell
.\tests\deployment\Invoke-SqlReadinessConformance.ps1
.\tests\deployment\Invoke-FactoryBootOrchestrationConformance.ps1
.\tests\deployment\Invoke-RebootStartupConformance.ps1
.\tests\deployment\Invoke-RebootStartupReleasePackageConformance.ps1
```

The helper suite injects function-level probes for delayed availability, permanent
failure, two-target consistency, bounded retries, cancellation, invalid/missing
configuration and secret redaction. It does not claim real SQL proof. For a real
non-mutating SQL proof, set `FACTORYCONNECT_SQL_READINESS_TEST_CONNECTION_STRING`
to an authorized test database and run the helper suite with `-LiveSql`. It verifies
authenticated SELECT and denial of a nonexistent database without creating tables
or changing schema. The boot suite uses a packaged helper stub to prove gate ordering
and fail-closed runtime delegation. Factory reboot acceptance remains separate.
