# FC2-035 — Authorized operator activity history

Implementation slice following FC2-034, merged through PR #34 at `f81837b1`.
Status: implemented; disposable SQL / real HTTPS Chromium qualification pending.

## Product boundary

The operator evidence page shows previously committed closed activity periods for
an explicitly selected, currently authorized machine. This is historical derived
evidence, not current connectivity, open running time, physical observation
completeness, Availability or OEE. No HTTP request publishes activity or starts a
processor. Production worker hosting and commissioning remain separate increments.

## Read contract

`GET /api/commissioning/v2/machines/{machine}/activity` discovers up to 100 persisted
mapping histories for the configured site and exact stream. Empty history is an
explicit `no-published-history` result. Missing/invalid authority is unavailable;
there is no mock or in-memory fallback.

`GET /api/commissioning/v2/machines/{machine}/activity/{mapping}` returns the newest
20 periods by default (1–100 with `take`) and an optional next cursor. The cursor
pins machine, stream, mapping, qualification and the full ordered tail key. It is
an unsigned position, not permission or proof of source completeness. Every page
rechecks human authority. Pages are independently current reads, not a frozen
multi-page snapshot. Existing periods are immutable; newer publications may appear
when the operator refreshes the latest page.

Source instance, sequence, processor generation and duration ticks are decimal
strings. Raw values remain exact. The UI renders browser-local times and exact
duration, with publication operation, package revision, acknowledgement and
qualification SHA-256 available as provenance. Older-page navigation replaces the
page rather than accumulating a historical prefix.

## Authorization and integrity

The API requires HTTPS and the existing opaque human session. SQL uses the shared
installation security gate, machine gate and (for a page) processor gate, followed
by current session and machine-scoped `commissioning.read` checks. It rechecks SQL
time/session/permission after the protected read before commit. Revocations use
the existing exclusive security gate. No activity-specific privilege bypass is
introduced. Session/grant denial is distinct from unavailable publication integrity.

Current human access authorizes reading historical immutable publications. A later
runtime mapping expiry does not reinterpret or delete earlier periods. The API
validates checkpoint SHA-256, exact qualification and producing receipt ancestry;
each returned period must match its deterministic effect identity and its immutable
operation receipt. It does not reconstruct retained raw history or claim new
physical-source verification during a read. Its verification label is explicitly
`immutable-publication-receipts`.

A forged cursor scope, malformed checkpoint, missing receipt, mismatched period or
oversized page fails closed. SQL preflights aggregate checkpoint/receipt and page
payloads at 8 MiB before materializing them. Pages use the existing scope/keyset
index; selected period IDs and distinct receipt IDs drive bounded seek joins.
Discovery never loads all historical periods. No migration is needed.

## Acceptance and limits

Local validation includes build, lint, frontend contract/mounted tests and pure
backend checks. New disposable-SQL HTTP acceptance exercises authorized pages,
keyset continuation, wrong-machine denial, cursor scope rejection, exact values,
period/ancestry corruption, aggregate byte rejection, revocation and unchanged
checkpoint/receipt counts. It also runs after the large-history fixture.

The Chromium fixture reuses the test-only signed OIDC code/PKCE provider over real
HTTPS and actual SQL. It renders an accepted-source period, source values and
publication provenance, then proves session revocation, durable logout and grant
revocation denial. Existing invalid identity tests remain intact. Final measured
CI evidence and merge disposition must be recorded before accepting this slice.

This fixture is controlled qualification, not customer IdP/PKI provisioning or
production enablement. Sustained cross-batch continuity remains unqualified and
all unproven boundaries remain breaks. Production quantities, incremental shifts,
Availability, Performance and Quality/OEE follow their own input contracts.
