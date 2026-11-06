# G2 increment 1 — Sealed security authority kernel

Baseline: `main` `6fa1a1f2b07eab8b9c15cdb0207a7d27b2c69436`, after PR #5. Follows the user's autonomous implement → validate → squash-merge → new branch workflow.

## Delivered boundary

Migration 003 adds the separate `security` authority: sealed installation state, exact external identity bindings, immutable session authority, scoped immutable grant ancestry, committed revocation records, durable operation results and append-only audit. The migration seeds only the sealed state, with no administrator, credentials, grants or sessions.

`SqlSecurityAuthority` provides trusted server-side protected-read, grant/session revocation and stored-result retrieval primitives. It is not registered by the HTTP host. It does not authenticate a browser token, accept caller-asserted identity, issue a session, bootstrap an administrator, create grants or enroll a runtime. OIDC validation/session issuance, two-custodian bootstrap/recovery, grant creation/delegation commands, mTLS enrollment and SQL application-role hardening remain subsequent increments. No G2 completion or production readiness is claimed.

In particular, an application must not accept the kernel's session hash from an arbitrary request body. The future authentication/session middleware must resolve a server-managed opaque credential to that hash; SQL stores only the hash. No runtime test login, fixture seed or sealed-state override is exposed by application code.

## Authorization contract

- Every kernel call checks the explicitly supplied installation UUID against `platform.InstallationIdentity`, then checks sealed state and one server-generated UTC evaluation instant.
- Human sessions must exist, be within their validity interval, belong to a human principal and have no committed session/principal revocation. Runtime principals cannot enter the human session path.
- Permission candidates are read with every delegation ancestor. Authorization requires exact permission, resource scope, valid intervals and no grant/principal revocation along the whole path. Each parent must permit delegation, belong to the child's grantor, and contain the child's whole declared scope and interval. No partial acceptance of a grant exceeding its ceiling.
- Machine/site GUIDs must identify a real, correctly scoped resource. Access to historical retired-machine records is not implicitly removed by mutable display metadata; lifecycle eligibility for active configuration remains a separate authority.
- Any independently valid grant path can authorize. A new grant cannot revive descendants of a revoked grant; their immutable parent link remains revoked.
- Root-origin records are reserved for the future controlled bootstrap authority. This increment offers no public grant insertion method. Cycles/excessive depth fail closed via SQL recursion limit (an integrity failure, not successful authorization).
- The generic read handler is trusted server code and must scope its SQL to the authorized resource and fully materialize its response before returning. This is an authorization transaction boundary, not automatic SQL row filtering.

## Locking and committed authority

Installation-wide authorization gate is deliberately broad. Reads hold shared mode through committed transaction completion; security mutations take exclusive mode at the outset. Mutation order is installation-scoped operation-ID lock → security gate → authoritative checks → immutable operation/revocation/audit and epoch update → commit. No shared-to-exclusive upgrade occurs. Future configuration handlers must add G1 policy and machine locks only after these ranks.

The kernel uses READ COMMITTED, including when the database uses READ_COMMITTED_SNAPSHOT. It reads authority only after the application lock is acquired; it does not establish a transaction snapshot before waiting. Security mutations using this kernel cannot race protected reads inside that gate. Direct privileged database writes are outside this guarantee and must not be an operational workflow.

Tests observe actual `sys.dm_tran_locks` waits, not only elapsed delays. They exercise both orders against READ_COMMITTED_SNAPSHOT enabled SQL Server: an existing protected read delays revocation commit; an already waiting read sees the fresh committed revocation once the gate opens. A response already authorized may arrive later over the network. Each page/chunk of a future stream requires a fresh boundary.

## Idempotency and audit

A change requires a durable operation UUID, target, expected authority epoch and reason. Request bytes bind exact target, expected epoch, reason and operation kind; storage binds actor and installation. Completed retries reauthorize before returning bytes. Conflicting payload or expected-epoch preconditions reject; exact completed replay does not fail merely because later operations advanced the epoch.

Operation, revocation, audit and epoch update share one transaction. A deliberately failing audit trigger proves rollback of all effects. Concurrent exact retries append one audit/result. A revoked session cannot retrieve or replay a completed result. New revocations advance epoch; replay does not. Session revocation remains distinct from grant revocation.

The audit here records committed successful authority mutations. Durable logs of rejected authentication/authorization attempts are a separate operational logging concern; this increment does not claim those attempts are persisted by a rolled-back transaction. Recorded timestamps remain observation metadata, not proof of commit visibility.

## Acceptance and deployment limits

Disposable SQL fixtures seed synthetic principals/grants/sessions with privileged test SQL and explicitly unseal only that generated test database. This is test setup, not an implementation of bootstrap or grant issuance. The fixture-only commit barrier and failure triggers are created/removed by the acceptance executable; they are absent from shipped migrations and API registration.

Acceptance covers default sealing, scoped access, delegation ceilings, human/runtime separation, expiry, both revocation/read orders, regrant behavior, session/grant independence, concurrent retries, fingerprint/epoch conflicts, immutable audit and atomic rollback. Existing installation, package and dashboard checks continue to run.

No existing database, production principal, credential or machine is used. A green kernel test does not authorize enabling configuration writes before the remaining G2 pieces pass their own acceptance fixtures.
