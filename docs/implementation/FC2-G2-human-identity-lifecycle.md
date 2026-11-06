# G2 increment 5 — Human enrollment and permanent revocation

Baseline: `main` `5c24588`, following scoped-grant PR #9. This increment adds `EnrollHumanAsync` and `RevokeHumanAsync` to the server-side security authority. No HTTP route, sign-in adapter or automatic first-login enrollment is registered.

## Identity authority

Both commands require current **installation-scoped** `security.access.manage` use authority. A site/machine access administrator can issue bounded grants to existing humans but cannot create or revoke installation-global identities.

Enrollment binds a new stable human principal GUID to exact UTF-8 issuer and subject bytes. It rejects existing principal IDs or external bindings, including revoked bindings and runtime bindings; names, trimming, case folding and Unicode normalization never reconcile identities. Identity and reason encodings must be lossless and bounded. Enrollment records an intended external account; it does not prove account control, create a login session or assign any permission. Sign-in must still use the separately trusted authentication verifier, and business access requires a separate delegated grant.

Pilot identity-provider boundary: ordinary human enrollment accepts only the exact `allowedAdministratorIssuer` pinned in the immutable, committed bootstrap trust manifest. Missing bootstrap trust denies enrollment. A submitted issuer or changed host configuration cannot add a provider. This deliberately supports one installation-trusted issuer; multiple providers and issuer rotation require a separate versioned trust mechanism. It does not select or provision a real OIDC tenant.

## Revocation and continued administration

Human revocation is permanent and append-only. From its commit visibility, existing sessions, fresh sign-in, direct grants and every delegation path containing that human as grantee or grantor become ineligible. Evidence, grants, sessions, enrollments, receipts and audit remain retained. Revocation does not claim an external IdP account was disabled or erase responses already returned/in flight.

Ordinary revocation rejects self-revocation and requires a currently valid installation-management chain that **excludes the target principal at every ancestor**. This prevents an administrator from revoking its sole supporting root/ancestor and losing its own authority in the same operation. Independent eligible paths may authorize the change; invalid dependent paths cannot be combined with them. If all administrative paths depend on the departing person, controlled recovery must establish independent authority first; this increment does not offer recovery or root creation. Bounded authority still expires normally.

No ordinary command unrevokes or rebinds a principal. Re-enrolling the same issuer/subject under another GUID cannot bypass revocation. Runtime enrollment/revocation remains a distinct authority.

## Transaction and replay contract

Order remains operation-ID lock → exclusive installation security gate → current SQL UTC/state/session → installation management → exact completed-result check → expected epoch → action-specific checks → identity event/mapping + operation + audit + epoch → commit. All sign-in, protected-read, grant and revocation paths use the same gate. No lock upgrade or nested commit is introduced.

Enrollment stores an immutable mapping to its operation and actor; migration 007 ties that actor to the operation through a composite foreign key. Principal data, enrollment mapping, operation result, audit and epoch are atomic. Revocation likewise commits its existing `PrincipalRevocation` event, operation, audit and epoch together.

Requests bind operation, target, exact identity where applicable, expected epoch and reason in versioned immutable bytes. Concurrent exact retries return one receipt and append one audit/epoch change. Different operations competing for one binding cannot create duplicates. A completed receipt requires the original actor's current session and installation-management authority. Returning an old enrollment receipt never unrevokes the target. A distinct later revoke operation may acknowledge an already-revoked target, retaining the original revocation event and appending its own audited operation.

## Acceptance and remaining work

Disposable SQL fixtures begin with an actual signed two-custodian bootstrap, then use synthetic verifier output to issue fixture sessions. They exercise pinned issuer, exact case/whitespace/zero-byte/Unicode identities, unknown/runtime rejection, duplicate/concurrent binding, no implicit grants or sessions, scope separation, audit rollback, dependency-aware principal revocation, retries and protected-result retrieval.

Concurrency fixtures observe real SQL application-lock waits. A protected descendant read completes before revocation can commit. Conversely, a fresh sign-in waits behind actual uncommitted principal revocation and then rejects its newly committed result. Recorded timestamps do not prove commit visibility.

This increment does not complete production identity provisioning, OIDC/browser sign-in, human authority renewal, controlled recovery, runtime credentials, SQL application-role hardening or configuration workflow integration. Application code seeds no production principal, key or credential.
