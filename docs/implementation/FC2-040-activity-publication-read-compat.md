# FC2-040 — Activity publication read compatibility

Status: draft implementation; CI SUCCESS at fdf495d (run #42669023369: backend, dashboard, SQL acceptance). NOT merge-qualified. No production enablement.

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

CI uses disposable SQL. The user's `FactoryConnect_V2_Dev` on `DESKTOP-1PJK2OB\\FCINVENTORY` is reserved for separately authorized local smoke tests after checking schema identity and version. Do not auto-migrate it, use V1/Gajra databases, or deploy to production.

## Merge qualification register — 2026-11-09

- **PASS:** SQL lock-order acceptance after resource-and-session attribution; SQL Server exposes `xp_userlock` during waits. Three stable snapshots, timeout/release checks and fail-closed behavior remain in acceptance.
- **PASS:** CI run [#42647985475](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42647985475) at `615d895`: backend, dashboard and SQL acceptance.\n- **PASS (genuine 100-witness fixture):** the normal publisher retained exactly 100 source witnesses for one operation; the SQL reader verified that operation and rejected a transaction-local deletion of one witness. The 101 quiet batches in this acceptance are drained across two bounded publication cuts. This does not prove a legitimate 101-witness single operation.
- **PASS (small fixture only):** authenticated series discovery 15 and history page 17 SQL roundtrips, cap 17 each.
- **PASS (defensive SQL guard):** CI run [#42654842983](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42654842983) at `94e0e84` passed the 102-witness corruption fixture. It transaction-locally rebinds two retained witness records to a genuine 100-witness receipt, requires SQL error 51091, and rolls back. This is not legitimate publisher provenance.\n- **PASS (normal worker discovery limit):** CI run [#42669023369](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42669023369) at `fdf495d` verifies a 101-row discovery request is rejected with `ArgumentException` by the explicit bounded worker scope. The normal 100-row cut produced a genuine 100-witness receipt, verified by the reader. A hypothetical 101-witness receipt is not claimed as a legitimate writer output. The independent 102-witness corruption pre-read test (SQL 51091) also passed. Receipt-selection count tests remain distinct.
- **OPEN:** genuine version-4 published history operation with corrupted retained witness must produce fail-closed authenticated HTTP response. Transaction-local SQL tamper tests do not substitute.
- **OPEN:** authenticated 100-distinct-operation history page and series discovery, with measured command starts, roundtrips, bytes and budget, each <=17 roundtrips.
- **OPEN:** final full-diff review, fresh SQL installation confirmation and acceptance register refresh at final head.

No merge or deployment is authorized by this CI result alone.

## Demo decision boundary (2026-11-09)

The current green CI qualifies the implemented compatibility behavior and bounded small-fixture checks, **not** the three outstanding merge gates. For a management demonstration, prefer a local disposable SQL environment with seeded non-production machine data, an authenticated operator session, and the existing dashboard; clearly label simulated or seeded data. Do not present this PR as production-ready, assert live Gajra connectivity, or apply migrations to the reserved developer database without separate verification and authorization. PR #41 remains draft until its outstanding qualification is resolved; a demo does not waive the 101-witness writer-bound question, authenticated v4 HTTP corruption test, or 100-distinct-operation benchmark.
