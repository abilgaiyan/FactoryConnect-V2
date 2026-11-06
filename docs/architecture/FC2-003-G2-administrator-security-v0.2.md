# FC2-003 G2 — Authorization dependencies, revocation and trust v0.2

**Status: DESIGN PROPOSAL v0.2 — ACCEPTED WORKING DIRECTION, NOT FROZEN.**  
Supersedes/qualifies [G2 v0.1](FC2-003-G2-administrator-security-v0.1.md) where inconsistent. G1 is frozen at `5e170d1`; G3 open. PR #3 stays draft. No DDL, writes, credentials, deployments, machine cutover or production changes authorized.

## 1. Permission-dependency authorization gates

An authorization decision is not a single role check. Define an **authorization dependency closure** for every protected operation: human principal and authenticated session; active direct grants; every delegation ancestor; scoped site/machine assignments; role-definition and action permission; any required separation-of-duty constraint; runtime identity and credential generation when applicable; and applicable disablement/suspension/revocation. A missing or revoked dependency denies execution.

**Gate contract:** all mutations and protected reads acquire compatible shared gates on the entire dependency closure in canonical order, then revalidate authoritative committed versions within the protected operation. Grant/revoke, delegation changes, session revocation, scope reassignment, runtime credential revocation and role permission changes take exclusive gates for affected dependencies and commit revocation/version changes before releasing them. For broad role/scope changes, include a stable role/scope authority key so unknown future dependents cannot evade gating. Cache is advisory only. Gate membership is revalidated after acquisition; if the closure changed while gates were acquired, release and restart with the same operation identity.

Use installation-scoped lock resources. A permission decision must never be based only on a stale token, a copied grant, a pre-transaction query or an unprotected cache.

## 2. Delegation ancestry and descendant grants

Each delegated grant stores `ParentGrantId`, grantor/grantee, explicit action set, site/machine scope, expiry, `CanDelegate`, delegation depth/ceiling and immutable grant-operation identity. A root grant has an explicit bootstrap authority origin. A child may not exceed the **intersection** of all ancestor actions, scopes and expiry; deny self-escalation and cycles. Cross-branch delegation does not silently combine privileges.

Revoking an ancestor grant **immediately invalidates the entire descendant subtree** for new authorization decisions when the revocation commits. Historical grants and audit remain immutable. Descendant grants may remain stored for audit but cannot be reactivated by an ancestor regrant; new authority requires new grants. Query-time eligibility traverses ancestry or a transactionally maintained closure projection validated against authority versions. Parent revocation uses an exclusive ancestor gate; descendants' operations must hold shared ancestor gates, so revocation cannot commit between dependency validation and operation commit.

## 3. Protected-read ordering

Protected reads include configuration details, machine assignments, commissioning discovery, operation results, security audit and any sensitive roster view. Public/unauthenticated health endpoints are outside this scope only if explicitly declared non-sensitive.

**Read boundary:** authenticate session → derive complete authorization closure → acquire shared dependency gates → revalidate grants/session/scope against committed authority → execute a consistent SQL read under the held gates → release. A read that starts **after** a revocation commits is denied. A read already authorized and holding its shared gate may finish before the revocation can commit; its response may arrive later due to transport latency. Do not claim post-revocation erasure of data already returned or in flight. Long-lived streams/pagination reauthorize each page or bounded chunk, with no persistent authorization lease across revocation.

Completed idempotent operation results are not disclosed without fresh read authorization, even when the operation was previously successful.

## 4. Pending activations and runtime credential rotation

Activation requests bind `ActivationRequestId`, exact package hash, machine ID, assigned `RuntimeIdentityId`, **credential generation/authorization epoch** and immutable request fingerprint. Credential rotation does not silently transfer pending requests to the new generation.

Proposed policy:
- **Routine planned rotation:** create a new generation; pending requests bound to old generation remain pending only during an explicitly bounded, approved overlap interval, if the old credential remains eligible. After expiry/revocation, old-generation acknowledgements are rejected. New generation cannot acknowledge old-bound requests.
- **Compromise/emergency revocation:** old generation immediately ineligible at committed revocation; pending old-bound requests are blocked and surfaced for commissioning. No grace overlap.
- **Rebinding:** create an explicit audited superseding activation request bound to the new generation, preserving predecessor link, machine scope and package hash. Do not mutate the old request or treat rotation as acknowledgement. Recheck current policy revocations, machine disablement and effective interval at rebinding and acknowledgement.
- Exact runtime identity is still required, and a valid credential alone does not prove configuration was physically applied.

Credential rotation and activation acknowledgement acquire the same runtime-credential-generation dependency gate; their commit order determines eligibility. Notifications and stop verification remain separate.

## 5. Global lock and operation-ID ordering

**Proposed total order for any transaction spanning authorities:**
1. Validate caller authentication enough to identify intended scope; begin transaction.
2. Acquire installation-scoped **operation-ID lock** (unique operation ID + immutable fingerprint) first. Operation registration/result lookup must not bypass current read authorization.
3. Discover authorization dependency closure and acquire all **security dependency gates** in canonical order (type then binary stable ID); revalidate closure and session/grants under gates.
4. Acquire **G1 policy-revision locks** in canonical (installation, policy type, revision ID) order.
5. Acquire **machine locks** in canonical MachineId order.
6. Acquire any per-entity row locks needed for append-only writes, revalidate final state, append authority/audit/result, and commit. Never acquire a lower-ranked lock while holding a higher-ranked lock; on newly discovered dependencies, abort and retry the same operation ID.

Revocation transactions acquire their operation lock first, then exclusive security dependency locks. Policy safety revocation transactions additionally acquire the frozen G1 policy locks; do not hold a machine lock while waiting for policy lock. Every transaction uses a bounded lock timeout and rolls back on failure; retries retain operation identity/fingerprint. No nested independent commits may create an authorization gap.

**Open proof obligation:** verify this ordering against every relevant code path, including an operation with multiple principals, multiple ancestor grants and several machines, and the SQL Server transaction/application-lock behavior under concurrent retries.

## 6. Extended action permissions

| Action | Authorized principal (proposed) | Mandatory additional conditions |
| --- | --- | --- |
| Disable/suspend machine configuration | Scoped safety operator | Explicit machine scope, reason, audit; separate from package edit/approval |
| Lift suspension/re-enable | Scoped safety operator plus independent approval for safety-critical cases | No automatic restoration; current package/policy/time eligibility rechecked |
| Safety-critical shared-policy revocation | Dedicated safety revoker | Exact policy revision, classification, reason, effective instant, append-only authority; two-person approval proposed |
| Assign runtime identity to machine | Runtime identity administrator | Explicit machine scope, credential eligibility, audit; distinct from activation controller |
| Rotate/revoke runtime credentials | Runtime credential administrator with controlled approval | Exact generation, scope, pending-request impact and audit |
| Discover commissioning candidates | Scoped commissioning viewer | Approved pending/failed/partial assignments visible without active-roster membership; sensitive details scope-filtered |
| Read runtime/activation diagnostics | Scoped commissioning viewer or auditor | Protected-read gates; no implicit write authority |
| Submit/approve/activate configuration | Existing editor/approver/activation roles | Distinct permissions, self-approval denied, exact machine/package scope |

Runtime principals cannot grant human permissions, approve packages, revoke policies or assign themselves to machines. Safety revocation cannot be bypassed by ordinary reactivation.

## 7. Concrete identity and custody proposal

**Human IdP:** Microsoft Entra ID using OIDC authorization code + PKCE for browser sign-in, with server-side session authority, issuer/audience/tenant allowlist, token validation and fresh grant checks. A dedicated installation-specific application registration is proposed. If Gajra cannot provision Entra ID, this is a **dependency requiring an explicit alternative review**, not permission to silently fall back to local passwords.

**Runtime credentials:** installation-scoped mutual TLS with per-runtime X.509 client certificates, pinned trust anchor, certificate thumbprint and credential generation stored in security authority; private keys remain in OS certificate store or controlled secure vault, never SQL/repo. Reverse-proxy/client-certificate forwarding must be explicitly trusted or avoided; no user-supplied header may impersonate a runtime.

**Bootstrap/recovery custody:** offline, independently held bootstrap/recovery authorization material with two distinct named custodians, separate from the application runtime and SQL service account. Proposed threshold ceremony requires two custodians to authorize a short-lived, installation-bound, single-use operation, with out-of-band verification, dual signoff and append-only audit. Recovery uses a separately sealed capability, reason, expiry and mandatory post-recovery rotation. No default administrator or embedded break-glass password.

**Open before freeze:** confirm Entra tenant/app owner and offline login constraints; certificate issuing CA/trust anchor and rotation windows; custody holders, secure storage medium, lost-custodian replacement and threshold enforcement mechanism. These cannot be inferred from factory infrastructure.

## 8. Required adversarial acceptance tests

- Revoking an ancestor while a descendant mutation is pending; no descendant authorization after revocation commit; regrant does not resurrect descendants.
- Revoking site assignment, role grant or session while a protected read is pending; both commit orders and paginated read boundaries.
- Multi-principal/multi-machine operations acquire stable ordered locks; no partial commits, deadlock avoidance and bounded retry.
- Operation-ID exact replay, conflicting fingerprint rejection, revoked-caller completed-result denial and race with grant revocation.
- Routine rotation overlap, emergency revocation, old/new generation acknowledgement and explicit superseding activation request.
- Unauthorized commissioning discovery and audit access; scope-filtering across sites and machines.
- Safety revocation vs activation acknowledgement ordering under G1 locks and security gates.
- Bootstrap two-custodian approval, independent custody, expiry, replay, recovery and revoked custodian tests.

## 9. G2 decision register

| ID | Decision | v0.2 proposed resolution | Status |
| --- | --- | --- | --- |
| G2-01 | Authority | Separate security authority and dependency-closure gates | PROPOSED |
| G2-02 | Bootstrap/recovery | Sealed installation, independent two-custodian threshold ceremony | PROPOSED; custody mechanism OPEN |
| G2-03 | Human IdP | Entra ID OIDC + PKCE, server-side session authority | PROPOSED; tenant/availability confirmation OPEN |
| G2-04 | Delegation | Ancestor-bound immutable grants, descendant invalidation on parent revocation | PROPOSED |
| G2-05 | Revocation and reads | Shared/exclusive dependency gates across mutations and protected reads | PROPOSED |
| G2-06 | Runtime trust | Per-runtime mTLS X.509 and generation-bound activation requests | PROPOSED; CA/rotation confirmation OPEN |
| G2-07 | Audit/idempotency | Operation lock first, canonical fingerprint and append-only audit | PROPOSED |
| G2-08 | Lock order | Operation ID → security dependency gates → G1 policy locks → machine locks | PROPOSED; proof pending |
| G2-09 | Safety/commissioning permissions | Explicit distinct actions and machine/policy scope | PROPOSED |

**Disposition:** G2 v0.2 is a review proposal, **not frozen**. G1 remains frozen; G3 open. PR #3 draft. No implementation, DDL, database creation, credential issuance or production action authorized.
