# Machine activity history report

The machine detail page now offers an explicit date-range read of closed persisted activity intervals. Reach it by selecting a machine on Fleet status. Dates default to the current IST civil date and are editable. Selection is 1–31 inclusive IST calendar days; these are not production-day identities.

## Evidence behavior

- The API resolves exactly one configured activity owner and pins its evaluated-through observation position. This is a state-processing checkpoint, not metric aggregation revision R3243.
- Historical activity is traversed in durable position order, filtered through that checkpoint, and clipped to the selected UTC half-open interval. Original interval bounds, durable position, instance and sequence remain in the response and interval details.
- Each recorded state retains its own duration. Unknown intervals are distinct from time without a stored interval. Open current-state intervals are not extended to now.
- Overlaps and inconsistent identities/positions fail the operation. Reads that fail or exceed limits return no report. The report does not certify continuous acquisition or historical coverage.
- Totals are durations of stored intervals, not OEE operands or evidence of planned eligibility. Freshness remains in the separate current-state panel.
- Limits: 31 selected days, 100,000 traversed history records, 5,000 selected intervals. The existing reader traverses the complete historical prefix; very large histories need a future indexed date reader. A smaller date range does not necessarily reduce prefix traversal.

## Table pagination

The interval table defaults to 50 rows per page, with 25/50/100 options, row ranges, page numbers and Previous/Next controls. Page changes do not perform retrieval or change the full-range totals and timeline. Successful loads and page-size changes reset the table to page 1. Empty results disable navigation. The 5,000-interval response limit remains unchanged.

Durations lead with seconds, minutes or hours, with exact seconds retained alongside the readable value. Source details retain exact UTC bounds and duration ticks.

## HTTP

`POST /api/machines/v1/{machineId}/activity/query`

```json
{"fromInclusive":"2026-10-05T18:30:00.000Z","toExclusive":"2026-10-06T18:30:00.000Z"}
```

This selects 6 October 2026 in IST. Response identity includes machine, state processor, observation stream and pinned checkpoint. Duration ticks are 100 ns units; all 31-day duration values fit within JavaScript's exact integer range. Position/instance/sequence values remain strings. The separate `activity-v1` OpenAPI document describes this endpoint; the existing reporting document is unchanged.

## Validation

Verified implementation revision: `859636edb282d5ac482d1fba62f125d28b140466` (11 October 2026).

- CI passed: 388 frontend tests, 1,707 ordinary .NET tests, Release/Vite builds, reporting-contract/type checks and the SQL integration job.
- Mounted tests cover table page sizes, navigation boundaries, full-range summary/timeline preservation, reload reset, empty results and request failure. Seven focused frontend tests also passed in the authoring workspace.
- The developer snapshot returned 1,416 intervals for 4–6 October, with exact state totals plus unrepresented time reconciling to 72 hours. The user subsequently confirmed local pagination works.
- Visual browser review, narrow-screen/keyboard checks and manual failure-path checks remain separate and outstanding. These results do not certify historical coverage or establish the R3243 stall.

The following commands are available for local verification:

Run on a developer machine:

```powershell
dotnet build FactoryConnect.sln --configuration Release
dotnet test FactoryConnect.sln --configuration Release --no-build --filter "Category!=SqlServerIntegration"
Push-Location src/FactoryConnect.Dashboard/ClientApp
npm ci
npm test
npm run typecheck
npm run contracts:check
npm run build
Pop-Location
```

Then run the API against the developer snapshot and the dashboard, open Fleet status → M01, select 04/10/2026 through 06/10/2026 and load activity. Verify timestamps, interval details, readable narrow-screen/table scrolling, failures, and unchanged metric checkpoints. No migration is needed. No Availability repair, quality default policy, quantity admission or schedule edits are part of this slice.
