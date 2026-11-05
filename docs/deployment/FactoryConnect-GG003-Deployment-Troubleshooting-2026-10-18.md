# GG-003 local reporting deployment and troubleshooting — 18 October 2026

**Scope:** Developer PC smoke test of the restored Gajra GG-003 SQL database and PR #123 Shift Performance UI (draft at the time of the smoke test; subsequently squash-merged into main). This is **not** evidence of deployment to the live factory server. Preserve live CNC acquisition; do not apply local settings to production without validating that environment.

## Verified environment and identities

| Property | Verified value |
| --- | --- |
| Restored SQL database | `FactoryConnect_GG003_RCA_20261016` |
| SSMS server observed | `DESKTOP-1PJK2OB\\LOCALDB#405C3A03` (transient LocalDB instance; verify each session) |
| MachineId | `677b030b-1ede-4a67-82ae-6a494791357f` |
| Reporting ProcessorId | `operational-metrics:677b030b-1ede-4a67-82ae-6a494791357f:builtins-v1` |
| SiteId | `c697c1a0-43b1-4aa7-9233-64344788cdf7` |
| ProductionLineId | `9b6fd59e-8e0f-40c7-9c9a-b64c90405bb1` |
| Dashboard display name | `GG-003` |
| Group name | `Gajra Gear` |
| API | `http://localhost:5080` |
| Dashboard backend | `http://localhost:5090` |
| Vite frontend | `http://localhost:5173` |
| PR branch used | `feat/gajra-r1-equipment-performance`, head `cb058bc047cbd746a2e5842143fcaab855c0e220` at testing |

**Identity rule:** SiteId and ProductionLineId must be exact persisted IDs, **not** human-readable labels. The initial local values `gajra-dewas-station-road` and `GG-003` were wrong. Reporting ProcessorId is not `machine-state-activity`.

## Local startup and verification

1. Confirm the API points to the intended restored database, not just that SSMS does. The database was reported under a transient LocalDB instance. Avoid applying migrations to the live Gajra SQL server.
2. Start the API on 5080 and verify `http://localhost:5080/health`.
3. Start the Dashboard backend with the correct environment overrides in its own PowerShell session:

```powershell
cd F:\Development\FactoryConnect
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5090"
$env:Dashboard__ReportingApiBaseAddress = "http://localhost:5080"
$env:Dashboard__Sources__0__MachineId = "677b030b-1ede-4a67-82ae-6a494791357f"
$env:Dashboard__Sources__0__ProcessorId = "operational-metrics:677b030b-1ede-4a67-82ae-6a494791357f:builtins-v1"
$env:Dashboard__Sources__0__SiteId = "c697c1a0-43b1-4aa7-9233-64344788cdf7"
$env:Dashboard__Sources__0__ProductionLineId = "9b6fd59e-8e0f-40c7-9c9a-b64c90405bb1"
$env:Dashboard__Sources__0__DisplayName = "GG-003"
$env:Dashboard__Sources__0__GroupName = "Gajra Gear"
dotnet run --no-build --project src\FactoryConnect.Dashboard
```

Use `--no-build` only after a successful build of that exact checkout. Confirm `http://localhost:5090/dashboard/config` reflects all identities.

4. Start Vite **last** in another PowerShell terminal:

```powershell
cd F:\Development\FactoryConnect\src\FactoryConnect.Dashboard\ClientApp
npm run dev
```

5. Open `http://localhost:5173/production-days/2026-10-15` and `http://localhost:5173/production-days/2026-10-15/shifts`.

## Troubleshooting encountered

| Symptom | Cause / evidence | Resolution |
| --- | --- | --- |
| `npm ci` fails with `EPERM unlink ...rolldown-binding.win32-x64-msvc.node`; MSB3073 exit -4048 | Windows native binary locked, likely by active Vite/Node process while Dashboard build invokes `npm ci` | Stop Vite first; inspect `Get-CimInstance Win32_Process -Filter "name = 'node.exe'"`; stop only relevant Node processes; run `npm ci`, build Dashboard, run Dashboard with `--no-build`, then start Vite |
| Shift roster coverage required for site `gajra-dewas-station-road` | Dashboard configured a display label rather than persisted site GUID | Set `Dashboard__Sources__0__SiteId=c697c1a0-43b1-4aa7-9233-64344788cdf7` |
| Shift Performance: “results could not be presented because they violated the Shift Performance contract” | Dashboard `ProductionLineId=GG-003` did not match authoritative roster's `9b6fd59e-8e0f-40c7-9c9a-b64c90405bb1` | Set the exact production-line GUID, restart only Dashboard, verify `/dashboard/config`, refresh page |
| Date-range report: reporting identity mismatch | Observed with provisional identity settings | Retest after correcting both IDs; **not yet separately confirmed PASS** |
| Machine current state unavailable; activity/counter HTTP 503 | Observed during offline restored-DB testing | Separate diagnostics required; historical Production Day PASS does not prove live machine/current-state endpoints |

Do not weaken the strict Shift Performance validator or fabricate missing operational metrics to hide configuration problems.

### Read-only SQL checks

```sql
SELECT @@SERVERNAME AS SqlServer, DB_NAME() AS DatabaseName, SYSDATETIMEOFFSET() AS CapturedAt;
SELECT COUNT(*) AS ProjectionCount FROM dbo.OperationalMetricProjection;

SELECT MachineId, ProductionDaySiteId, ProductionBusinessDate, ProductionLineId, Revision
FROM dbo.MachineShiftOccurrenceRoster
WHERE ProductionBusinessDate = '2026-10-15';

SELECT o.ShiftId, o.ShiftScheduleAssignmentId, o.ShiftStartsAtUtc, o.ShiftEndsAtUtc
FROM dbo.MachineShiftOccurrenceRosterOccurrence AS o
JOIN dbo.MachineShiftOccurrenceRoster AS r
  ON r.MachineShiftOccurrenceRosterRowId = o.MachineShiftOccurrenceRosterRowId
WHERE r.ProductionBusinessDate = '2026-10-15'
ORDER BY o.ShiftStartsAtUtc;
```

## Validated GG-003 smoke-test results

**Production Day, 15 October 2026:** API returned HTTP 200 through Dashboard proxy; Availability `25.353023%`; Utilization `6.569781%`; Performance `MissingReferenceTime / IdealProductionDuration`; Quality `MissingOperand / GoodQuantity`; OEE `DependencyInsufficientEvidence / Performance`.

**Shift Performance, 15 October 2026:** All three shift intervals rendered:

| Shift | IST interval | Availability | Utilization |
| --- | --- | --- | --- |
| SHIFT-1 | 06:00–14:00 | No metric displayed | No metric displayed |
| SHIFT-2 | 14:00–22:00 | **25.353023%** | **15.084595%** |
| SHIFT-3 | 22:00–06:00 next day | Insufficient evidence: ActualProductionTime | Insufficient evidence: ActualProductionTime |

**Selected GG-003 / SHIFT-2:** Five KPI cards rendered with exact metrics and reasons; evidence and scope drill-down displayed:
- Machine `677b030b-1ede-4a67-82ae-6a494791357f`
- Processor `operational-metrics:677b030b-1ede-4a67-82ae-6a494791357f:builtins-v1`
- UTC interval `2026-10-15T08:30:00+00:00` to `2026-10-15T16:30:00+00:00`
- Source stream `metric-inputs`; **source position 370**
- Assignment `DEWAS-SR-GG003-SCHEDULE-SHIFT-2`
- Performance: `MissingReferenceTime / IdealProductionDuration`
- Quality: `MissingOperand / GoodQuantity`
- OEE: `DependencyInsufficientEvidence / Performance`

**Acceptance status:** Local identity, roster, selected SHIFT-2 KPI and evidence presentation **PASS**. Reset-filters behavior, other filter interactions, historical aggregation revision 534 Availability >100% conflict, and full PR #123 acceptance are **not yet verified**. Source position 370 is **not** aggregation revision 370 or revision 534.

## Operational guardrails

- This validation used a **restored local database**, not the live Gajra server.
- Do not edit persisted observations, metric projections, shift roster, or migrations to force a UI pass.
- PR #123 was subsequently squash-merged into main; its merge does not constitute full R1 milestone acceptance or containment recovery.
- If troubleshooting new environments, verify SQL instance/database, machine, processor, site and production-line IDs **before** debugging UI logic.

## 18 October 2026 RCA and containment-publication boundary

The Gajra GG-003 operational projection worker repeatedly logged `InvalidDataException` from `OperationalMetricEvaluator.ValidateDomain`: Availability `1.0944533199738628666645251612` (109.4453%) exceeded the validated domain. This matches the reconstructed historical revision-534 operands. The aggregation checkpoint reached position 741 while the legacy operational projection checkpoint remained at position 524. The factory log did not print the attempted revision, so the revision-534 binding comes from separate restored SQL and evaluator evidence.

The read-only restored historical evaluator acceptance test demonstrated Availability **Unavailable / `RunningOutsidePlannedProduction`**, preserving the original position-380 witness. This is not evidence of successful projection commit, checkpoint advancement, API retrieval, or deployment. Production composition still uses the legacy evaluator; the V1 projection commit and dependency codec reject containment policy/witness payloads. Stage-B versioned persistence, atomic publication/replay, distinct containment processor composition, and disposable-restore runtime/API/Dashboard acceptance must precede recovery activation. Do not restart a writer against the original restored evidence or alter the Gajra production server for this local documentation.

The `builtins-v1` processor identity in the commands above identifies the historical legacy reporting path; it must not be reused as the distinct containment processor identity. Historical smoke-test figures above are observations from that test session, not claims of current reporting health.
