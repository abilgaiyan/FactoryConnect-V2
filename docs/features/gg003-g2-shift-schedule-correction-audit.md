# GG-003 G2 — Shift schedule correction and audit record

Status: **Recorded for G2 preflight; not deployed**  
Plant: Gajra Gear — Dewas Station Road  
Machine: GG-003  
MachineId: `677b030b-1ede-4a67-82ae-6a494791357f`  
Activity stream: `mtconnect:DEWAS-SR-GG-003`  
Timezone: `India Standard Time` (Windows identifier)  
Change classification: **Correction of commissioning data-entry error**, not evidence of an actual change in Gajra's working shifts.

## Authority and source evidence

Gajra confirmed the corrected 07:30 production-day convention and shift timings, as reported by the commissioning operator on 2026-10-22. The operator clarified that the previously configured shift times were entered incorrectly. Seven-day recurrence is confirmed.

Read-only inspection of `D:\FactoryConnect\config\edge.production.json` on 2026-10-22 established the old on-disk assignments, identifiers, timezone, recurrence, and original `EffectiveFrom=2026-10-15`. The file has no planned-production or break-window configuration paths. This inspection **does not** establish effective runtime composition, whether sample settings were inherited, or the actual shift hours worked historically.

The precise Gajra-approved **business effective date** for the correction is **not yet recorded in this evidence**. Do not substitute the old configuration's `2026-10-15` date. The approval record, approving person, and documentary reference should be attached when available.

## Previous on-disk configuration (incorrect commissioning entry)

| AssignmentId | ShiftId | StartsAtLocal | EndsAtLocal | StartDayOffset | ActiveDays | EffectiveFrom |
| --- | --- | --- | --- | --- | --- | --- |
| `DEWAS-SR-GG003-SCHEDULE-SHIFT-1` | SHIFT-1 | 06:00:00 | 14:00:00 | omitted (default 0) | Sunday–Saturday | 2026-10-15 |
| `DEWAS-SR-GG003-SCHEDULE-SHIFT-2` | SHIFT-2 | 14:00:00 | 22:00:00 | omitted (default 0) | Sunday–Saturday | 2026-10-15 |
| `DEWAS-SR-GG003-SCHEDULE-SHIFT-3` | SHIFT-3 | 22:00:00 | 06:00:00 next day | omitted (default 0) | Sunday–Saturday | 2026-10-15 |

All previous assignments are open-ended (`EffectiveTo` omitted).

## Gajra-confirmed corrected business schedule — candidate, not deployed

Business production day D: **07:30 on D through 07:30 on D+1**. All shifts run on all seven business weekdays. Preserve the three existing `AssignmentId` values.

| AssignmentId suffix | ShiftId | StartsAtLocal | EndsAtLocal | StartDayOffset | Business-day ownership |
| --- | --- | --- | --- | --- | --- |
| `SHIFT-1` | SHIFT-1 | 07:30:00 | 16:00:00 | 0 | D |
| `SHIFT-2` | SHIFT-2 | 16:00:00 | 00:00:00 | 0 | D, ending on D+1 |
| `SHIFT-3` | SHIFT-3 | 00:00:00 | 07:30:00 | 1 | D, physically on D+1 |

`StartDayOffset` is a business-date-relative placement per merged G1 contract, not a change to SQL production-day identity. The corrected shift times are confirmed; proposed break windows (12:00–12:30, 20:00–20:30, 03:30–04:00 on D+1) and planned-production windows remain **candidate assumptions requiring independent confirmation**. Do not silently treat these break times as approved.

## Change record — fields to complete before cutover

| Field | Value / requirement |
| --- | --- |
| Record ID | GG003-G2-SHIFT-CORRECTION-20261022 |
| Classification | Commissioning entry correction |
| Reason | Operator entered incorrect shift timings; Gajra subsequently confirmed actual shift schedule |
| Business approval | Reported confirmed by Gajra; attach approver, evidence and approval timestamp |
| Business effective date | **TBD — record exact Gajra-approved date** |
| RecordedAt | Set by authoritative audit system at record creation; do not backdate |
| AppliedAt | **Not applied**; set only after actual authorized deployment |
| AppliedBy | **Not applied** |
| Previous config version/hash | Capture from archived on-disk file/release before deployment |
| Candidate config version/hash | Capture from validated G2 candidate |
| Deployment/restart evidence | **None; deployment held** |
| Historical report treatment | Explicit decision required; no silent re-interpretation or recomputation |

This repository document is a **human-readable preflight audit artifact**, not a substitute for immutable database-backed audit publication. Repository commit history records changes to this document, not production activation timestamps.

## G2 validation and deployment gates

1. Preserve the previous on-disk config and a verified pre-change backup, with cryptographic hashes and machine/shift identities.
2. Resolve the actual approved effective date and break/planned-production policy; determine whether any retrospective correction is required.
3. Build a separate G2 candidate; verify production host's effective configuration is sample-free and that all required contexts, schedules and windows are explicit.
4. Validate seven-day shift ownership, midnight adjacency, offset-1 third shift, transitions across effective dates, collision checks, breaks and historical business-date interpretation.
5. Record review and explicit authorization before any fresh V2 SQL initialization, configuration replacement, restart or cutover.
6. Capture actual applied timestamp, actor, release/config hashes, post-deployment verification and rollback evidence only after execution.

**Current safety boundary:** G2 preflight only. Do not change the running GG-003 acquisition, Edge, production JSON, SQL or Dashboard as a consequence of this record.

## Future feature: audited schedule administration

Provide an authorized entry screen for company/site/line shift schedules and planned-production/break windows. Capture the actor, approver, reason, old/new values, effective business date, recording time, publication time and configuration revision. Use immutable revisions and an explicit approval/publication workflow; validate recurrence, timezone, overlap, day offsets, physical intervals and break ownership before activation. Support future-dated changes separately from corrections to historical configuration. Historical re-evaluation must be a separately approved, traceable operation with a report revision, never a silent rewrite of stored observations or previously published reports.
