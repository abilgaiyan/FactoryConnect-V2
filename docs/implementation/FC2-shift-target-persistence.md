# Shift planning persistence — first implementation slice

Migration 024 creates **planning.ShiftDefinition**, **planning.ShiftBreak**, and append-only **planning.ShiftTargetRevision**. The target is scoped to a real machine, site, shift and business date; revisions are individually attributable and cannot be silently overwritten by the intended API. This is schema only: there is **no live target-entry API or Supervisor Desk wiring yet**.

## Gajra calendar to enter after site approval
- Shift 1 07:30–16:00, break 11:30–12:00; offset 240 min, duration 30
- Shift 2 16:00–00:00, break 20:00–20:30; offset 240 min, duration 30
- Shift 3 00:00–07:30, break 03:30–04:00; offset 210 min, duration 30

All three breaks should have ExcludeFromPlannedProduction=1. Do not seed Gajra-specific records into every installation. Shift business-date attribution (especially midnight) must be explicitly defined in the API.

## Next vertical slice
Implement authenticated SQL-backed shift/break administration and shift-target create/list, using existing session identity and site/machine scope; validate overlapping shifts/breaks and monotonic revisions under concurrency. Replace preview-only target editor with live real-data mode. Then add actual production quantities and OEE achievement comparisons without confusing targets with OEE Performance.

## Safety
Do not run on production until separately approved. No real factory records are inserted by this migration.
