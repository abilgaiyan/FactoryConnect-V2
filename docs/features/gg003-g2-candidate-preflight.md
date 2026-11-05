# GG-003 G2 candidate — configuration and validation preflight

**Status:** design candidate only, not a deployable production configuration. PR #147 audit record is pending merge. No changes to GG-003 production host or database.

## Source and authority

- Merged G1 `src/FactoryConnect.Edge/appsettings.json` contains only operational defaults and no sample machine assignments.
- `src/FactoryConnect.Edge/appsettings.Demo.json` contains demo machine `11111111-1111-1111-1111-111111111111`, context `CTX-1`, planned assignment `POT-1`, UTC and demo shifts. Production must not inherit these.
- Read-only on-disk inspection of `D:\FactoryConnect\config\edge.production.json` confirmed GG-003 machine identity, three shift assignment identities and incorrect commissioning times. It did **not** prove effective runtime composition.
- Gajra confirmed 07:30–07:30 production-day and three shift timings; active days all seven. Corrected break windows and exact business effective date remain unrecorded in available evidence.

## Preserved machine authority

| Field | Value |
| --- | --- |
| MachineId | `677b030b-1ede-4a67-82ae-6a494791357f` |
| ActivityStreamKey | `mtconnect:DEWAS-SR-GG-003` |
| QuantityStreamKey | `production-quantity` |
| CompanyId | `85beab0d-29fe-4964-9112-679d72d77d69` |
| SiteId | `c697c1a0-43b1-4aa7-9233-64344788cdf7` |
| ProductionLineId | `9b6fd59e-8e0f-40c7-9c9a-b64c90405bb1` |

## Proposed replacement shift values

The following is a **partial design specification**, not a complete JSON config. Preserve all other validated machine, acquisition, persistence and context settings from the production file. Preserve old configuration separately; do not overwrite it.

| AssignmentId | ShiftId | StartsAtLocal | EndsAtLocal | StartDayOffset | TimeZoneId |
| --- | --- | --- | --- | --- | --- |
| `DEWAS-SR-GG003-SCHEDULE-SHIFT-1` | SHIFT-1 | 07:30:00 | 16:00:00 | 0 | India Standard Time |
| `DEWAS-SR-GG003-SCHEDULE-SHIFT-2` | SHIFT-2 | 16:00:00 | 00:00:00 | 0 | India Standard Time |
| `DEWAS-SR-GG003-SCHEDULE-SHIFT-3` | SHIFT-3 | 00:00:00 | 07:30:00 | 1 | India Standard Time |

Each assignment retains its CompanyId, SiteId, ProductionLineId, Name and Sunday–Saturday ActiveDays. `EffectiveFrom` is **TBD**, pending capture of the exact approved business date. Do not use the old 2026-10-15 date by default.

G1 `StartDayOffset` is physical placement relative to the owning factory date; it does not change the owning business date. In particular, SHIFT-3 physically occurs on D+1 and belongs to production day D.

## Planned production and breaks: blocked candidate inputs

The inspected on-disk `ProductionProcessing` section has no planned-production or break paths. A production-ready candidate must explicitly establish these assignments and their context authority, using the G1 `PlannedWindows[]` or legacy scalar contract but not both. Confirm breaks independently with Gajra; do not infer that illustrative 12:00–12:30, 20:00–20:30 and D+1 03:30–04:00 are approved. Verify the proposed policy's gross/net minutes only after confirmation.

## Technical validation matrix

| Gate | Test | Expected evidence | Status |
| --- | --- | --- | --- |
| V1 | Inspect production host configuration composition and environment | No `CTX-1`, `POT-1`, sample machine GUID, UTC/demo shifts; production authority explicit | Not run |
| V2 | Parse complete candidate and validate option binding | All three shift identities, offsets 0/0/1, seven weekdays and timezone bind correctly | Not run |
| V3 | Resolve one complete business date | Three contiguous intervals cover D 07:30 through D+1 07:30 with no overlap/gap | Not run |
| V4 | Resolve midnight and neighboring business dates | SHIFT-3 belongs to D, no collision with next day's SHIFT-1 | Not run |
| V5 | Validate effective-date transition | Old/new adjacent assignments do not physically collide; no retroactive report reinterpretation | Blocked on effective date |
| V6 | Validate planned-production windows/breaks | No overlap/ambiguous break placement, planned-time semantics correct | Blocked on confirmed policy |
| V7 | Run Edge/Core regression and SQL integration suites | Tests pass for exact candidate/release | Not run |
| V8 | Preflight fresh V2 database, backups and rollback | Reviewed runbook, no production mutation | Not run |

### Development-side focused commands (from merged G1 review)

```powershell
dotnet test tests/FactoryConnect.Core.Tests --filter FullyQualifiedName~ProductionDayOffsetTests
dotnet test tests/FactoryConnect.Edge.Tests --filter "FullyQualifiedName~EdgeConfigurationContractTests|FullyQualifiedName~ProductionConfigurationCompositionTests"
dotnet test FactoryConnect.sln
```

These are **development machine / CI commands**, not instructions to run tests on the live Gajra server. Existing CI for PR #147 does not by itself validate the G2 candidate.

## Go/no-go

**NO-GO** until the exact business effective date, break/planned-production policy, effective host composition and candidate test results are evidenced. Keep acquisition, SQL, Edge, Dashboard and on-disk production configuration unchanged. A future cutover requires a separate explicit authorization and recorded applied timestamp.
