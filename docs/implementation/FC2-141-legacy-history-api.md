# FC2-141 — Read-only imported V1 history reporting API

This API uses existing V2 HTTPS operator-cookie authentication and SQL human-session validation, but it **never** promotes imported data into the V2 published activity stream. It reads only existing `legacy_reporting` tables created by the local development import under PR #134.

Endpoints (query dates use the historical Gajra factory calendar, Asia/Kolkata, including both dates):

- `GET /api/reporting/v2/legacy/machines` — imported machine identities and optional explicit V2 mapping
- `GET /api/reporting/v2/legacy/machines/{machineId}/metrics?from=2026-10-22&through=2026-10-25` — unchanged legacy metric statuses, numeric text, reason codes, shift identity and times
- `GET /api/reporting/v2/legacy/machines/{machineId}/activity?from=2026-10-22&through=2026-10-25` — raw completed overlapping activity intervals for client-side midnight/shift splitting

Responses are labeled `source=imported-v1-legacy`, `evidenceKind=historical-reporting-only`, `liveStatus=not-established`, and `isSample=false`. Missing Performance, Quality and OEE remain missing or unavailable, not zero. Raw state codes are preserved; do not infer Running/Idle/Stop/Fault labels without validating V1 enumeration.

**Bounds:** maximum 31 calendar days and 2,000 records per collection. Longer/larger requests are explicitly rejected, never silently truncated. No SQL writes occur. No new SQL migration is required because the API consumes the isolated imported development tables. Without them the endpoint returns a 503 unavailability response.

**Important:** Historical GG-003 shift changes effective 2026-10-22 and differing break rules on MOXA require source-specific historical definitions before calculating shift production time or new OEE. For now existing V1 metric projections are preserved without reinterpretation. Interval API selects by overlap at Asia/Kolkata midnight; the consumer must split each returned interval at calendar and historical shift boundaries instead of assigning it wholly to its start date.

**Security caveat:** SQL session validation is site-agnostic here, matching the existing broad planning screens. Before exposing this API to multiple sites or a production server, restrict each source machine to the session's explicitly authorized site scope. This is only a development-data reporting integration and does not authorize production deployment.
