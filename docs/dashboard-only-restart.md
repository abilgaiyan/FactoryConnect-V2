# Dashboard-only restart

`Restart-FactoryConnectDashboard.ps1` is an explicit configuration reload for the recorded Dashboard instance. It never calls the Edge restart, startup reconciliation or deployment script, never modifies configuration or SQL, and never stops Edge/API. It uses the existing shared `deployment.lock` and `runtime-start.intent.json` barriers.

Preflight requires a successfully published runtime, no unresolved intent/restart, a `current` junction to the recorded immutable release, and exact PID/executable/start-time ownership for all three roles. API health and Edge/API ownership are checked before termination and again throughout completion. Dashboard configuration is validated and projected from the validated object into the replacement environment, with Production environment selection. The requested machine must have exactly the explicitly expected reporting processor identity; every configured source must reference an operational metric processor for its own machine. No database or semantic acceptance is inferred from this configuration check.

The operation publishes `DashboardRestarting`, stops only the recorded Dashboard with the shared pinned-instance termination helper, publishes absence, then records a launch intent before creating one replacement. The replacement must survive stabilization and pass `/health/live` and `/health/ready` with verified ownership. Ownership is atomically published before clearing the matching intent; final checks precede `Succeeded`. Edge/API records and deployment evidence are preserved, including extension properties. Failures record phase, termination observations and any replacement observation, retain unresolved intent, and never automatically launch another child or stop an unpublished child. If failure occurs after intent clearance, the durable failed runtime and published ownership remain the recovery evidence. An interrupted operation requires explicit review/reconciliation, not a retry or whole-stack restart.

## Validation and packaging

Windows PowerShell 5.1 tests:

```powershell
.\tests\deployment\Invoke-DashboardRestartConformance.ps1 -Executable
```

The fault suite covers ownership/configuration mismatch, lock exclusion, foreign shared intent, termination timeout, failed/unknown launches, replacement exit, both Dashboard health endpoints, preserved-role failure, atomic publication/intent-clear faults, interrupted transitions and explicit repeat restart. Executable fixtures use actual Windows processes, junctions, HTTP listeners, shared termination and atomic files, including a killed restart host after child launch. They verify the replacement receives the new processor configuration while Edge/API retain their instances.

The release builder stages the script, declares `dashboardRestartScript` in `release.json`, and includes its SHA-256 through normal complete payload manifest enumeration. The Windows CI workflow runs the lifecycle tests, builds an exact-source release and verifies packaged bytes, metadata and manifest coverage. Its retained `p0-sql-readiness-release` artifact is the release candidate; passing script tests alone is not a completed release-package acceptance.

## Controlled use after acceptance

This command is a reference for a separately authorized production operation; it has not been executed on Gajra by this change. First verify the exact-source release package, stage its script with the matching `FactoryConnect.ProcessTermination.ps1`, preserve the configuration backup and runtime evidence, confirm the expected SQL/API reports and review CI. Do not use a whole-stack deployment merely to install a maintenance script. Do not change the running release junction or terminate PID 13684 manually.

```powershell
# Run only after the separately reviewed installation and Dashboard cutover authorization.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File <verified-script-directory>\Restart-FactoryConnectDashboard.ps1 `
  -InstallRoot D:\FactoryConnect `
  -MachineId 677b030b-1ede-4a67-82ae-6a494791357f `
  -ExpectedProcessorId operational-metrics:677b030b-1ede-4a67-82ae-6a494791357f:builtins-v1-availability-containment-v1
```

After execution, verify Edge/API PID/path/start-time are unchanged, Dashboard has the published replacement identity, intent is cleared, health checks pass, and October 18–19 reports match the accepted SQL/API results. This validates reload and reporting presentation; historical source semantics remain a separate acceptance boundary.
