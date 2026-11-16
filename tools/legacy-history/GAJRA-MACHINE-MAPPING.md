# FC2-132 — Gajra historical machine mapping

Source: user-provided V1 production.edge.json (2026-11-16). This file records identifiers and historical configuration only; it does not authorize production deployment or writes.

| V1 MachineId | DeviceKey | V1 activity stream | V1 normalization address |
|---|---|---|---|
| 677b030b-1ede-4a67-82ae-6a494791357f | DEWAS-SR-GG-003 | mtconnect:DEWAS-SR-GG-003 | DEWAS-SR-GG-003_p1_ctl_exec |
| bfa0dc97-9a68-45ed-b189-1dacb7d863f0 | DEWAS-SR-MOXA-001 | mtconnect:DEWAS-SR-MOXA-001 | moxa001_status |

Both machines use V1 SiteId c697c1a0-43b1-4aa7-9233-64344788cdf7 and company 85beab0d-29fe-4964-9112-679d72d77d69. V2 site and machine IDs must be explicitly reconciled; do not assume V1 GUIDs can be reused.

## Time-sensitive production context

- GG-003 shift schedules effective 2026-10-15 through 2026-10-22: 06:00–14:00, 14:00–22:00, 22:00–06:00 (India Standard Time).
- GG-003 replacement shift schedules effective 2026-10-22: 07:30–16:00, 16:00–00:00, 00:00–07:30.
- MOXA shift schedules effective 2026-10-25: 07:30–16:00, 16:00–00:00, 00:00–07:30.
- GG-003 planned production effective 2026-10-22 has **no BreakWindows** in supplied configuration.
- MOXA planned production effective 2026-10-25 excludes breaks 11:30–12:00, 20:00–20:30 and 03:30–04:00. Do not silently apply these exclusions to GG-003 history.
- GG-003 context effective 2026-10-22 07:30 IST; MOXA context effective 2026-10-25 07:30 IST.

## Implementation consequences

1. Persist source MachineId, DeviceKey, source interval identity and V1 report values for reconciliation.
2. Preserve historical schedule versions and source-specific planned-production rules; V2 current shift masters alone cannot recreate all V1 historical reports.
3. Treat missing or unsupported OEE components as unavailable, not zero or estimated.
4. Keep imported history outside V2 acquisition/publication authority and do not mark historical machines live/commissioned based on imported data.
5. Before writing target rows, add explicit mapping to a selected V2 site and machine identities, a dry run, and row-count reconciliation.
