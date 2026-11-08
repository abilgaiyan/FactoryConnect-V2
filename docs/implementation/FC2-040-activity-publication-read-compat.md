# FC2-040 — Activity publication read compatibility

Status: draft implementation with passing CI at 9af1b99; not merge-qualified. No production enablement.

## Implemented scope and explicit deferrals

- The SQL history reader verifies version-4 publication receipt identity, ancestry and retained witness hashes, while preserving legacy receipt reads and fail-closed malformed-receipt handling.
- The HTTP contract retains the pre-existing `same-batch-only-not-physical-completeness` continuity literal for compatibility. This is a conservative legacy-level label, **not** an assertion that version-4 cross-batch proofs are absent; per-publication/per-period profile and verification metadata are **deferred** and must not be inferred from the page-level literal.
- **Partially implemented, not qualified:** SQL pre-read budgets now count request and result bytes for page operations and head receipts for series discovery. The verifier still reloads those bytes, so aggregate transfer/memory is not yet bounded by the 8 MiB SQL check. Budget-overflow regression fixtures, bounded batched receipt verification, HTTP version-4 end-to-end fixtures and measured Gate 5 comparison against the 17-roundtrip baseline remain outstanding. The PR remains draft until these are implemented or explicitly accepted as separate follow-up gates.
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
