# FC2-040 — Activity publication read compatibility

Status: merged to main as b67935a (PR #41). Read-side verification of version-4 publication receipts is implemented and covered by disposable-SQL acceptance. No production enablement; corruption fixtures are disposable-SQL only.

## Implemented scope and explicit deferrals

- The SQL history reader verifies version-4 publication receipt identity, ancestry and retained witness hashes, while preserving legacy receipt reads and fail-closed malformed-receipt handling.
- The HTTP contract retains the pre-existing `same-batch-only-not-physical-completeness` continuity literal for compatibility. This is a conservative legacy-level label, **not** an assertion that version-4 cross-batch proofs are absent; per-publication/per-period profile and verification metadata are **deferred** and must not be inferred from the page-level literal.
- The current authenticated small-fixture measurements are 15 SQL roundtrips for series discovery and 17 for a history page, both within the 17-roundtrip cap. These measurements do not qualify the full 100-distinct-operation benchmark. The 8 MiB SQL pre-read budget is not a claim that all in-memory verifier work is bounded to 8 MiB. The PR remains draft until the remaining gates below are evidenced or explicitly dispositioned.
- Corruption fixtures are disposable SQL only; they do not authorize production deployment.


## Ownership

FC2-035 (merged PR #35) already supplies authenticated HTTPS history APIs, bounded keyset reads and the React operator screen. FC2-039 (merged PR #39) introduces version-4 cross-batch publication proofs and migration 021. This increment extends the existing reader; it does not create a parallel operator screen or API.

## Confirmed code-level gaps on main

1. `ActivityHistoryApi` hardcodes `continuity=same-batch-only-not-physical-completeness` for both discovery and pages. The TypeScript client rejects other continuity strings and the screen repeats the legacy-only interpretation.
2. `SqlSecurityAuthority.ActivityHistory` validates checkpoint SHA-256, receipt content and period effects but does not verify `activity.PublicationReceiptAncestry` or `activity.PublicationSourceWitness` for version-4 receipts.
3. The reader must distinguish historical legacy and version-4 publications without claiming physical completeness, live connectivity or OEE.

## Required implementation

- Derive the profile from the **verified receipt request**, not from untrusted UI input or a guessed generation. Reject version-4/profile disagreement and malformed proof/checkpoint bindings.
- Verify the exact version-4 receipt result hash, ancestry ledger identity and predecessor linkage, plus source witness coverage and exact immutable identities. Foreign keys protect identities, not original-document parsing or semantic verification; do not label a page as reverified physical completeness.
- Preserve legacy read compatibility. If a page contains multiple receipt profiles, communicate verification and continuity per publication/period rather than attributing the newest checkpoint's policy to all rows.
- Retain existing machine/session authorization, post-read revocation check, HTTPS, bounded SQL work, keyset cursor and 8 MiB payload protection. No full retained raw-history scan.
- Render accurate user-facing provenance and qualified continuity in the existing component; keep unknown/missing integrity unavailable rather than silently downgrading.

## Acceptance gates

1. Fresh disposable SQL migration 021 plus legacy and version-4 fixture publications.
2. Current and historical pages, mixed-profile pages if possible, oldest cursor, no-state frontier, restart/replay, current permission and expiry/revocation races.
3. Adversarial tests: missing ancestry, altered result/proof hashes, missing or substituted witness, inconsistent parent, and checksum-consistent corruption. Verify failure is fail-closed without a partial page.
4. Real HTTPS/OIDC/SQL/Chromium acceptance and exact TypeScript contract tests.
5. Bounded page profile compared with FC2-035 baseline (22 period rows across profiled requests; at most 17 roundtrips before this increment). Any extra work must be measured and justified.
6. Full backend, dashboard and SQL CI green at final head before squash merge.

## Local/production boundary

CI uses disposable SQL. A developer-local database may be used for separately authorized smoke tests after checking schema identity and version. Do not auto-migrate it, use V1 or unrelated databases, or deploy to production.

## Qualification register (final at merge, 2026-11-10)

Results below describe what the acceptance suite asserts. Exact measured values are emitted as `MEASURE:` lines in the SQL acceptance job log of the PR's final run; they are not duplicated here so this register cannot drift from CI.

- **Lock order and contention:** the machine and processor applocks are taken in one ordered command with the original per-lock 10 s timeout and 51020 failure. Acceptance observes SQL lock state by exact resource and session (three stable snapshots), the timeout mapping to the authority-outage response, release after failure and recovery. SQL Server exposes `xp_userlock` during waits, so attribution is by blocked resource, not outer command text.
- **Witness bounds:** the normal publisher retained exactly 100 source witnesses for one operation; the reader verifies that publication and rejects deletion of one witness. A 102-witness corruption fixture (transaction-local rebind, rolled back) is rejected by the SQL pre-read guard with error 51091; it is not legitimate publisher output. A 101-row worker discovery request is rejected by the bounded worker scope. A single operation with 101 witnesses is not claimed as writer output.
- **Authenticated version-4 HTTP tamper:** after genuine version-4 publication, flipping one retained witness hash makes both series discovery and the 100-row page return a bounded HTTP 503 whose problem title is "Activity history integrity verification failed"; the exact hash is restored and reads recover.
- **Gate 5 (100 distinct operations):** acceptance requires at least 100 committed publication operations for the mapping, then measures authenticated series discovery and a `take=100` history page, recording SQL command starts and server roundtrips. Each must be at most 17 roundtrips; the small-fixture measurements were 15 (discovery) and 17 (page). The page has no headroom: any added SQL command in authorization, the wrapper or the reader fails this gate by design.
- **Retired machine or site:** the retirement guard runs inside the checkpoint read. Acceptance retires the machine, then the site, and requires discovery and both page forms to be denied (HTTP 401) without history in the body, and to resume once restored.
- **Known limits:** the 8 MiB SQL pre-read budget does not bound all in-memory verifier work. The `same-batch-only-not-physical-completeness` literal is a compatibility label; per-publication verification and continuity metadata are deferred (see above).
