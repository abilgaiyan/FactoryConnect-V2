# Retained planned-production assignments — GG-003 future-only correction

Status: development candidate; production activation is not authorized. No migration, SQL schedule authority or admission recovery is introduced. This extends the startup in-memory reader only.

## Configuration contract

Each `ProductionProcessing:Machines` entry (or the existing single-machine root form) uses exactly one of:

- Legacy `PlannedProduction: { ... }`, unchanged assignment fields.
- `PlannedProductionAssignments: [ { ... }, { ... } ]`, a nonempty indexed array. Each item uses the legacy fields. Company/site/line are inherited from the owning machine, not overridden by items.

Both forms together fail, including legacy children retained by another configuration provider. Configuration providers merge children: deleting an object from an overlay does not remove it from a lower provider. Inspect the complete effective composition, not only the edited JSON. Scalar, empty and named-object collections fail. Entries sort by effective date then ordinal ID. Duplicate IDs within a machine fail. Matching assignments shared across machines are deduplicated only when all existing contract fields agree; conflicting identities or overlapping assignments within a site/line fail at startup.

Retained arrays describe replacement chains. Each predecessor's exclusive `EffectiveTo` must equal its successor's `EffectiveFrom`, and timezone must remain the same. This continuity requirement concerns effective-date coverage, not continuous production: inactive weekdays and intentional unplanned hours remain valid. A finite final end remains permitted. Legacy single assignments retain their existing coverage semantics.

Startup invokes the existing resolver around starts/ends (a weekday cycle on either side plus resolver lookaround). Invalid break placement and physical overnight collisions fail before host startup. This is not exhaustive future timezone-rule certification; isolated candidate-specific date checks remain mandatory.

## GG-003 template and boundary

`docs/templates/gg003-retained-planned-production.json.template` is an incomplete machine excerpt, intentionally not deployable. Array placeholders are strings so accidental activation fails. Copy exact old timezone, weekdays and planned windows from the protected effective configuration. If old windows use legacy scalar times, preserve their equivalent fields instead of inventing new geometry. Remove the singular object from every contributing provider. Do not replace the entire machine object with this excerpt: preserve contexts, streams, IDs and all other settings.

Preserve original ID, `EffectiveFrom`, timezone, weekdays, window geometry and break-free behavior. Adding the approved termination boundary is a separately recorded configuration revision; the original file/capture must be retained. Omit old BreakWindows rather than supplying an empty array. New windows are identical except for the three approved breaks. The third break belongs physically to D+1. Confirm containment against actual old planned windows before use.

D is chosen during a separately approved activation, never by the application clock during code development. Choose the next eligible 07:30 IST boundary after deployment, startup validation and loaded-configuration verification, with an explicitly recorded verification margin. At 06:45 that day's boundary may qualify; at 08:00 use the next day or later. If verification misses the margin, stop and prepare/revalidate a later D. Do not edit the running file expecting hot reload or dynamically substitute D. The candidate must be fully resolved before startup, then its loaded values verified before D. The example dates used in tests are fixtures, not production approval.

## Loaded configuration evidence and acceptance

Edge startup emits event 731, `Loaded planned-production assignments`, from the validated objects passed to the reader. Capture the process/release identity and this log payload. It includes each machine, assignment ID, scope, timezone, effective dates, active days, windows and breaks. Compare every value against the approved resolved candidate; a file hash or successful restart alone is insufficient. The log proves loading only, not downstream convergence or publication. If logging is filtered or evidence missing, activation acceptance remains blocked. Commissioning's protected file projection also understands the collection, but remains a file capture and does not prove runtime activation.

Before Gajra deployment/activation:

1. Build, then run Edge/Core regression and isolated candidate boundary tests; CI must pass.
2. Inventory all machines sharing GG-003's site/line. Schedules are line-scoped. No other machine may unintentionally inherit the revision; shared lines require explicit scope approval.
3. Resolve D-1, D, D+1, all three shifts and midnight. For a fully planned 24-hour day the three 30-minute breaks leave 1,350 minutes: shift nets 480, 450 and 420 minutes. Compare exact original schedules before D, not just totals. Prove independent one-day and adjacent-day queries agree.
4. On a disposable production-derived database, compare historical reports, committed output identities and quantities before/after; verify GG-003 admission/checkpoints and MOXA configuration remain unchanged. In-memory tests do not establish this factory-data acceptance. No reset, replay or historical correction is authorized.
5. Recompose/restart the isolated candidate twice and inspect event 731. Verify exact IDs, effective dates, windows, break offsets and release identity. Resolve actual production-date candidate again, including timezone behavior.
6. Preserve configuration backups and the approved termination record. Configuration rollback cannot undo committed evidence. After D, do not reactivate a break-free old configuration as an evidence rollback; reconcile through a separately approved procedure.

No production configuration or database is edited by this development PR. No approval of target quantities, quantity qualification or OEE completion follows from schedule loading.

## Development validation for this candidate

Build completed before each test suite, with no warnings/errors: Edge tests 242/242, Core tests 896/896, and commissioning visibility tests 14/14 passed. Local SDK: .NET 10.0.100 on Linux. The scratch environment lacked the `Asia/Calcutta` timezone alias used by the Windows timezone mapping; the tests used a temporary TZDIR alias to its installed `Asia/Kolkata` data. No repository timezone fallback was added. Integration project build included the API/OpenAPI generator. No SQL-backed suite or production-derived database rehearsal was run locally. CI and the factory acceptance gates above remain pending.
