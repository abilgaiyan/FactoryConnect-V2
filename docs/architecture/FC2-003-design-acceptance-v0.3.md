# FC2-003 — Design acceptance and remaining gates v0.3

**Status: accepted technical direction; not authorization for DDL, write endpoints, deployment or cutover.**  
Applies to the [v0.1 proposal](FC2-003-sql-foundation-design.md) and [v0.2 qualifications](FC2-003-review-qualifications-v0.2.md). PR #3 remains draft.

## Accepted design direction

The six v0.2 proposals are technically accepted as the controlling direction: environment-configured database names; immutable machine-scoped configuration packages pinning exact shared schedule/break-policy revisions; assigned-runtime, exact-request/revision-hash/machine acknowledgement with stale-response rejection; commissioning discovery independently of active roster; per-machine retain-last-successful effective configuration; and explicit installation identity with fail-closed rejection of an unrecognized nonempty database before DDL.

Acceptance does **not** resolve implementation details, schema ownership, security, or roster semantics, and does not authorize migrations.

## Effective configuration eligibility — mandatory qualification

Retain-last-successful is **bounded**, not an indefinite fallback. For each machine and evaluation instant, the last successfully acknowledged configuration may remain active during a pending/failed replacement **only if**:

1. Its effective-from/effective-to interval includes the evaluation instant, with explicit boundary semantics.
2. The machine and configuration have not been explicitly disabled, suspended, revoked, or retired under the controlling authority.
3. Its pinned shared policy revisions and machine assignment remain eligible under the approved lifecycle rules.
4. No newer successfully acknowledged, effective replacement supersedes it.

Expiration or explicit disablement/revocation takes precedence over retention. If no eligible active configuration remains, the machine must not appear as active; commissioning must expose its pending/failed/disabled state independently. Do not silently re-enable on restart or when a later acknowledgement arrives out of order.

## G1 — Schema boundaries and package ownership (before FC2-003A DDL)

Review and freeze:
- Schema ownership for stable Site/ProductionLine/Machine identities, external aliases, package revisions, machine-scoped assignments, pinned schedule/break-policy revisions, approval/activation records, installation identity and migration ledger.
- Package aggregate boundary, immutable canonical content/hash, exact machine scope, shared policy revision lifecycle, effective interval/overlap rules and cross-site consistency.
- Foreign-key/deletion policy, uniqueness/concurrency keys and the boundary between configuration authority and future acquisition/reporting projections.
- Installer identity/bootstrap behavior for empty vs recognized V2 databases.

**No DDL until G1 is explicitly approved.**

## G2 — Administrator bootstrap, delegation and revocation (before write operations)

Review and freeze:
- Initial administrator bootstrap and recovery: explicit out-of-band authorized ceremony, no default admin credentials or silent self-provisioning.
- Separate edit, submit, approve, activate, acknowledge, access-grant and audit permissions, with grant scope and delegation ceilings.
- Self-approval restrictions, machine/site assignment scoping and privilege escalation prevention.
- Revocation ordering against in-flight operations, authenticated session validity, durable operation retries and append-only access-change audit.
- Assigned runtime identity provisioning, rotation, compromise/revocation and acknowledgement trust.

**No configuration write endpoints or activation mutations until G2 is explicitly approved.**

## G3 — Roster edge cases and capabilities (before FC2-003C)

Review and freeze:
- Active roster excludes never-activated, expired, disabled, suspended or revoked configuration; replacement pending/failed does not remove an otherwise eligible last-successful configuration.
- Separate commissioning discovery for approved pending assignments, partial activation, failed activation and reasons.
- Exact dashboard `sources` and optional capability-profile shape, source identity, supported signal/counter/activity features and unsupported/unavailable status.
- Explicit distinction between healthy empty roster, no active configuration, SQL authority unavailable (503), and integrity conflict.
- Conformance fixtures against the preserved React decoder and mock-disabled real-backend mode.

**No SQL-backed roster API implementation until G3 is explicitly approved.**

## Gate status

| Gate | Status |
| --- | --- |
| Six v0.2 design proposals | **TECHNICALLY ACCEPTED** |
| G1 schema/package ownership | **OPEN — blocks FC2-003A DDL** |
| G2 admin bootstrap/delegation/revocation | **OPEN — blocks configuration writes** |
| G3 roster/capability semantics | **OPEN — blocks FC2-003C** |
| Production deployment and machine cutover | **NOT AUTHORIZED** |

This record is a design-review artifact only. It does not create migrations, databases, credentials, runtime configuration, API implementations or production changes.
