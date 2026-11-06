# FC2-003 G2 — Administrator bootstrap, authorization and runtime trust

**Status: DESIGN PROPOSAL v0.1 — REVIEW REQUIRED; G2 OPEN.**  
G1 remains ACCEPTED/FROZEN at `5e170d1`. G3 remains OPEN. FC2-003A and all configuration writes remain ON HOLD. PR #3 remains draft. No DDL, credentials, database, deployment or production action authorized.

## 1. Security authority and scope

A shared `security` authority is **proposed** for human identities, assignments, sessions, role grants, runtime identities, credential generations, revocations and append-only security audit. Configuration lifecycle and acknowledgements remain owned by `configuration`, with authorization evaluated against `security` inside a coordinated operation. `master` supplies immutable Site/Line/Machine IDs. **The schema and tables below are proposals, not additions to frozen G1**; any change to G1-owned tables requires explicit change review.

Proposed records: `security.Principal`, `security.PrincipalCredentialBinding` (external IdP subject binding, no password material), `security.RoleDefinition`, `security.PermissionGrant`, `security.DelegationAuthority`, `security.PrincipalScopeAssignment`, `security.SessionAuthority`, `security.AccessRevocation`, `security.RuntimeIdentity`, `security.RuntimeCredentialGeneration`, `security.SecurityOperation`, `security.SecurityAudit`. A site/machine scope must reference authoritative master GUIDs, never display names.

## 2. Permission matrix

Legend: **Y** permitted only with matching scope and active grant; **N** prohibited; **C** controlled two-party/break-glass ceremony. Roles are starting profiles, not implicit global privileges.

| Action | Bootstrap security admin | Delegated access admin | Config editor | Config approver | Activation controller | Runtime principal | Auditor |
| --- | --- | --- | --- | --- | --- | --- | --- |
| View authorized machine/config | Y | Y | Y | Y | Y | machine only | Y |
| Edit draft config | N | N | Y | N | N | N | N |
| Submit package revision | N | N | Y | N | N | N | N |
| Approve/reject package | N | N | N | Y | N | N | N |
| Request activation | N | N | N | N | Y | N | N |
| Acknowledge exact activation request | N | N | N | N | N | Y | N |
| Grant/revoke ordinary scoped access | Y | Y within delegation ceiling | N | N | N | N | N |
| Delegate access-grant authority | Y | Y only if explicitly subdelegable and within ceiling | N | N | N | N | N |
| Enroll/rotate/revoke runtime credential | C | N | N | N | N | N | N |
| Bootstrap/recover top-level administrator | C | N | N | N | N | N | N |
| View immutable security audit | Y | limited scope | N | N | N | N | Y |

**Separation of duties:** no actor may approve a revision they authored or submitted; no grant may exceed grantor's active delegable permissions, site/machine scope or expiry. Granting access is separate from editing, approving, activating or runtime acknowledgement. Runtime identity cannot act as a human principal. Delegated administrators cannot bootstrap a root administrator or expand their own authority.

## 3. Administrator bootstrap and recovery

- New installation starts **sealed**, with no default user, password, token or implicit first-login administrator. Installer identity is validated independently before any security bootstrap.
- Authorized initial bootstrap requires an explicit out-of-band ceremony: documented operators, controlled bootstrap capability with short expiry, verified installation ID, intended initial principal and audit witness. Proposed **two distinct authorized people** for approval/execution, with no self-approval.
- Ceremony is single-use, bound to installation identity, and atomically creates first security administrator, initial scoped authority, operation result and audit; replay with identical operation identity returns the original result only to an authorized requester. Different payload under same operation ID is rejected.
- Recovery uses a separate, time-bounded break-glass ceremony with independent approval, reason, evidence of lost access and mandatory post-recovery audit/credential rotation. Never silently reset credentials or bypass the sealed state.
- **Open:** concrete identity provider and bootstrap key custody (e.g., enterprise OIDC + offline operator-held recovery material) must be selected before implementation.

## 4. Sessions, grants and revocation ordering

- Human authentication: external OIDC identity provider proposed; validate issuer, audience, signature, nonce/state (where applicable), expiry and token binding. Server-side authorization must **not** rely solely on long-lived JWT role claims; evaluate authoritative grant/revocation generation and exact resource scope.
- Session record binds principal, external subject, installation, authentication context, expiry and current authorization generation. Every protected mutation rechecks active grant, scope and session revocation inside a coordinated authorization gate.
- **Ordering proposal:** use a shared/exclusive authorization gate keyed by affected principal/scope (or a demonstrably equivalent serializable transaction). An operation holds a shared gate from final authorization validation through its database commit; grant revocation acquires the exclusive gate, commits the revocation and generation increment before release. If revocation commits first, a pending operation must fail. If an authorized operation commits first, its immutable result remains but future retrieval is denied when access is revoked.
- A registered but not yet completed idempotent operation does **not** retain permission after revocation. Replayed completed results require authorization to read. Stale tokens, cached grants and stale sessions cannot override committed revocation.
- Revoke session and grant independently; both changes append immutable audit and increment a security authority generation. UI refresh and downstream notification are not evidence of revocation completion.

## 5. Assigned-runtime credentials and acknowledgement authority

- Each machine activation request identifies one assigned `RuntimeIdentityId` and credential generation, exact machine ID, immutable package revision/hash, activation request ID and operation identity. Runtime can acknowledge **only its assigned machine/request**.
- Prefer per-runtime asymmetric credentials (mutual TLS/client certificate or workload identity), with explicit enrollment, expiry, rotation, revocation, machine scope and installation binding. Store public key/certificate thumbprints and secret references only; **never store private keys in SQL or source control**.
- Acknowledgement verifies authenticated runtime identity, current credential generation/revocation, exact request + machine + package hash, monotonic activation generation and effective-time eligibility in one serialized transaction. Stale, duplicate-conflicting, cross-machine and revoked-runtime acknowledgements are rejected.
- Credential rotation creates a new generation; old generations are revoked according to an explicit overlap policy. An acknowledgement authenticated with a revoked generation must not be accepted even if the activation request was issued earlier.
- Acknowledgement means **runtime reported application**, not proof of physical machine behavior. Safety-critical policy revocation makes DB configuration ineligible independently of runtime notification and stop acknowledgement.
- **Open:** runtime credential mechanism, trust anchors, rotation overlap, provisioning operator and runtime stop-verification contract require explicit review.

## 6. Auditable, idempotent access changes

- Every grant, revoke, delegation, session revoke, runtime enrollment/rotation and break-glass operation requires immutable `OperationId`, canonical request bytes/fingerprint, actor, installation, target principal, exact resource scope, expected version, reason, authorization decision and recorded result.
- Register and execute with a single durable transaction boundary: acquire authorization gate, lock operation ID, return authorized completed result if exact fingerprint matches, otherwise reject conflicting reuse; validate expected grant/revocation generation; apply append-only change; update current projection; write audit and stored operation result; commit.
- Access changes are **append-only authority events** with read projections. Do not delete/rewrite past grants or audit. Explicit revocation supersedes earlier grants; regrant is a new event/identity and cannot resurrect a revoked session.
- Audit should include server timestamps and transaction/commit identity; **recorded timestamp is not proof of commit visibility**. Consumers distinguish committed authority from attempted/uncommitted records.
- Enforce unique `(InstallationId, OperationId)`, unique scope-aware grant identities, expected-version compare-and-swap, FK to principals/scopes, and SQL isolation/locking tests for simultaneous grant/revoke/replay.

## 7. Acceptance fixtures required before write endpoints

1. Sealed install cannot authenticate as administrator; single-use authorized bootstrap and break-glass recovery including rejected self-approval.
2. Permission-matrix allow/deny tests for each action and machine/site scope; no editor self-approval; no delegated privilege escalation or grant beyond ceiling.
3. Concurrent mutation-vs-revocation test in both commit orders; registered-but-incomplete operation rejected after revocation; completed result cannot be retrieved by revoked principal.
4. Expired/invalid/revoked session and stale-token claims rejected despite cached permissions.
5. Runtime acknowledgement tests for correct assigned identity, wrong machine/request/hash, revoked credential generation, stale activation and conflicting idempotency key.
6. Durable audit/retry tests: exact replay same result when authorized, conflicting fingerprint rejected, audit/operation/result atomic and no partial effects.
7. Compatibility review with frozen G1 policy locks: define global lock ordering when security gate, policy locks and machine locks are used together; deadlock and failure-rollback tests required.

## 8. Decision register

| ID | Decision | Proposal | Status |
| --- | --- | --- | --- |
| G2-01 | Security authority | Separate `security` schema with immutable events and current projections | PROPOSED |
| G2-02 | Bootstrap/recovery | Sealed installation, single-use two-person ceremony, audited break-glass | PROPOSED |
| G2-03 | Human authentication | OIDC identity with server-side session/grant authority | PROPOSED — IdP selection OPEN |
| G2-04 | Permissions/delegation | Action matrix, scope ceilings, no editor self-approval | PROPOSED |
| G2-05 | Revocation ordering | Shared/exclusive gate through operation commit; revocation increments authority generation | PROPOSED |
| G2-06 | Runtime identity | Per-runtime asymmetric credential generation and exact machine/request binding | PROPOSED — mechanism OPEN |
| G2-07 | Idempotency/audit | Immutable request fingerprint, exact replay, append-only authority events | PROPOSED |
| G2-08 | Cross-authority lock order | Authorization gate before G1 policy locks before machine locks; prove transaction behavior | PROPOSED |
| G2-09 | Key custody and break-glass operators | Explicit installation-scoped recovery custody and approver selection | OPEN |

**Freeze condition:** explicit technical acceptance of G2-01 through G2-09, including concrete IdP/runtime credential and recovery custody choices. G2 freeze does not itself authorize configuration write implementation, SQL DDL, deployment or machine cutover.
