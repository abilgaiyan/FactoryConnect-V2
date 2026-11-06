# FC2-003 — G1 Freeze Acceptance

**Disposition: ACCEPTED / FROZEN — design baseline only.**

**Frozen source:** [G1 v0.3, commit `5e170d1`](https://github.com/abilgaiyan/FactoryConnect-V2/blob/5e170d1/docs/architecture/FC2-003-G1-schema-package-boundaries-v0.1.md)

## Freeze scope

Technical acceptance resolves G1-01 through G1-07 and G1-10 through G1-11, including:
- Ownership for `master`, `configuration` and `platform`; separate future `acquisition` and `reporting`.
- Immutable submitted, machine-scoped configuration package revisions, versioned canonical hashing and exact pinned schedule, break-policy and timezone revisions.
- IANA zone ID, tzdb release, retained rules-content hash, resolver version and explicit DST gap/fold handling; runtime OS defaults cannot substitute for pinned rules.
- Exact source semantics with declared field/protocol encoding; binary identifiers retained as bytes and canonical Base64, textual identifiers preserved without trimming, normalization or case folding.
- Explicit, optionally open-ended half-open effective intervals, exact single-site/machine scope and transactionally serialized overlap/supersession.
- Append-only, explicitly classified safety-critical policy revocation; immediate ineligibility upon commit visibility or scheduled future eligibility effect; separate recorded/knowledge time and effective time.
- One server-generated UTC evaluation instant per eligibility transaction/read, authoritative revocation recheck independent of stale activation projection.
- Transaction-owned policy-revision locks keyed by installation, policy type and revision; deterministic acquisition before machine locks; rollback on failed lock; retries keep immutable operation identity.
- Last-successful acknowledged configuration remains eligible only while its effective interval, disablement/suspension/revocation and pinned-policy rules permit it.
- Runtime notification/acknowledgement of revocation is tracked separately; database ineligibility does **not** prove runtime cessation. Historic packages, acknowledgements and machine evidence remain unchanged.

## Mandatory implementation-review evidence

Implementation review **must prove**, rather than merely restate, the frozen invariants via:
1. SQL DDL constraints and key/FK/uniqueness/check rules for ownership, machine/site scope, pinned revision identity, effective-period shape, and immutable submitted content.
2. SQL integration tests for transaction-owned policy locks, cross-session activation/revocation races, deterministic lock ordering, rollback after lock failure, and same-operation retry/replay fingerprint checks.
3. Canonical content/hash fixtures covering package sorting, exact byte-preserving/base64 source IDs, case-sensitive textual source IDs, timezone rules provenance and version compatibility.
4. Time semantics fixtures for an immediate revocation where record timestamp precedes commit, a scheduled future revocation, historical as-of-known reads and consistent read evaluation instants. **A recorded timestamp is never proof that a revocation has committed or become visible.**
5. Roster/effective-configuration contract fixtures that refuse expired, disabled, suspended or revoked last-successful packages even if `MachineActivationState` is stale.
6. Explicit runtime notification/stop-verification evidence separate from database eligibility.

Implementation proposals that deviate from this frozen baseline require an explicit G1 change review before implementation.

## Outstanding gates

| Gate or action | Disposition |
| --- | --- |
| G1 — schema and package ownership | **ACCEPTED / FROZEN** |
| G2 — administrator bootstrap, delegation and revocation | **OPEN** |
| G3 — roster/capability edge semantics | **OPEN** |
| FC2-003A migration/DDL implementation | **ON HOLD pending separate authorization** |
| PR #3 | **DRAFT — do not merge/mark ready** |
| Database creation, DDL, deployment or machine cutover | **NOT AUTHORIZED** |

This acceptance is a **design-only freeze**, not authorization to execute migrations, create databases or touch production.
