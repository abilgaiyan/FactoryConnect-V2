# Factory Site Commissioning and Recovery Runbook

## Purpose and scope

Reusable operator guidance for a Windows factory server running FANUC adapter, MTConnect Agent, FactoryConnect Edge, API, Dashboard, and SQL Server. Replace placeholders with approved site values before use. This document contains no customer-specific commissioning identifiers.

Follow the governing [factory demo deployment contract](../features/FC-032.4-factory-demo-deployment-contract.md), [Dashboard LAN deployment guide](factoryconnect-dashboard-lan.md), and the deployment scripts supplied in the exact release. This runbook does not authorize an unscheduled restart, SQL repair, reboot, or service/task installation.

## Before deployment

1. Verify the exact target host, machine identity, CNC/MTConnect endpoints, SQL instance/database, and network bindings from the approved site manifest. Do not confuse servers belonging to different plants.
2. Preserve permanent machine identity, existing production configuration under the install root's `config` directory, SQL history, and deployment evidence. Never regenerate a commissioned MachineId to fix a mapping problem.
3. Validate release ZIP byte length and SHA-256 on source and destination. A source commit alone does not imply identical ZIP bytes. The official deployer must independently validate its internal manifest.
4. The packaged release may have one top-level directory containing `release.json` and `MANIFEST.sha256`. Do not require `release.json` at the ZIP root or transfer a separate manifest unless the exact release contract requires it.
5. Distinguish repository paths such as `scripts/deployment/Deploy-FactoryConnect.ps1` from the deployed script at `<InstallRoot>\current\Deploy-FactoryConnect.ps1`. Confirm paths in the selected release.
6. If Windows PowerShell blocks a reviewed deployment script, inspect `Get-ExecutionPolicy -List`. A process-only exception (`Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force`) may be used **only if permitted by site security policy**; do not change machine-wide policy as a shortcut.

## Safe deployment boundary

The official deployment operation can validate artifacts, stop verified-owned application processes, run schema migrations, switch the selected release, and start Edge/API/Dashboard. It is **not** a nondisruptive file copy. Schedule an approved maintenance window, verify SQL readiness, and preserve acquisition continuity where possible. On a partial or failed deployment, retain logs and state; do not delete ownership/intent files, kill unidentified processes, or repeatedly rerun without diagnosis.

A `BootSafe` runtime startup mode is distinct from a normal deployment/reconciliation operation. Never assume it will repair arbitrary partial states. Automatic Windows startup and acquisition bootstrap require their own separately tested and authorized commissioning.

## Incident lessons and diagnosis

| Symptom | Lesson / verification |
| --- | --- |
| Edge exited after SQL connection errors | Capture exact Edge and SQL Server logs. SQL error 18461 can indicate rejection under single-user mode, but the originating event and process-shutdown cause must be investigated rather than assumed. |
| Persisted runtime state says `Owned` or `runtimeRunning` while Edge is missing | Treat runtime metadata as historical ownership evidence; independently inspect live processes, executable paths, PIDs and current logs. |
| API health fails on loopback | Inspect the configured bind address and actual listener. A healthy API bound to a specific LAN IP may not answer `127.0.0.1`. |
| Dashboard serves HTTP 200 but operational metrics are missing | Verify effective `Dashboard.Sources[n].MachineId` and `ProcessorId`, not only process health. The operational metrics identity follows `operational-metrics:<MachineId>:builtins-v1`; it is not the machine-state activity processor ID. Confirm the report's exact scope and evidence. |
| SQL service is running but Edge cannot persist | Check database state, authentication context, permissions, schema readiness, connectivity and application errors separately. An operator's successful SQL login does not prove a service identity can connect. |
| Processes and HTTP health are green but production data is absent | Query raw observations twice at a meaningful interval; verify new persisted records and fresh source timestamps. Do not infer good-part counts or OEE from raw counter changes. |
| App stops after logoff/reboot | A console-launched executable is not a supervised Windows Service. Design and validate acquisition bootstrap, runtime process ownership, SYSTEM SQL permissions, and reboot-safe startup before enabling a scheduled task. |

## Read-only health checklist

Run in Windows PowerShell on the **approved target server**. Supply actual site configuration values; the examples below deliberately do not contain real customer hostnames, machine IDs, or addresses.

~~~powershell
# Confirm host identity before continuing.
$expectedHost = '<approved-server-name>'
if ($env:COMPUTERNAME -ne $expectedHost) { throw 'Wrong target server' }

Get-CimInstance Win32_Process |
  Where-Object { $_.Name -in @('fanuc.exe','agent.exe','FactoryConnect.Edge.exe','FactoryConnect.Api.exe','FactoryConnect.Dashboard.exe') } |
  Select-Object Name,ProcessId,ExecutablePath

# Set the exact approved SQL service name.
Get-Service -Name '<sql-service-name>'

# Confirm actual bind addresses and owning processes.
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $_.LocalPort -in @(7888,5001,5080,3000) } |
  Select-Object LocalAddress,LocalPort,OwningProcess

# Test URLs from the approved site manifest, not assumed loopback endpoints.
$healthUrls = @('<mtconnect-probe-url>','<api-health-url>','<dashboard-live-url>','<dashboard-ready-url>')
foreach ($url in $healthUrls) {
  try { "$url : $((Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5).StatusCode)" }
  catch { "$url : FAIL $($_.Exception.Message)" }
}
~~~

Read-only SQL observation sample (replace placeholders with exact commissioned values):

~~~sql
SELECT COUNT_BIG(*) AS TotalObservations,
       MAX(ObservedAt) AS LatestObservedAt,
       MAX(Position) AS HighestPosition
FROM dbo.MachineObservation
WHERE MachineId = '<approved-machine-guid>';
~~~

Take two samples while new observations are expected. Compare timestamps and count/position. A stationary value alone does not prove a fault if the source was inactive or disconnected; correlate with agent and Edge logs.

## Acceptance after recovery

- Correct selected release and deployment status, corroborated by **live** Edge/API/Dashboard processes.
- MTConnect adapter and Agent have expected listeners and valid probe data.
- API and Dashboard respond at their **configured** addresses; dashboard report resolves its exact operational-metrics processor identity.
- SQL application database is available to the actual runtime identity and raw observations are durably advancing.
- Unavailable Performance, Quality, or OEE inputs remain explicitly unavailable; no fabricated zero, good quantity or ideal cycle time.
- Preserve incident logs and document the confirmed cause separately from hypotheses.
- No unattended restart or reboot-safety claim until a separately approved end-to-end reboot and recovery test passes.
