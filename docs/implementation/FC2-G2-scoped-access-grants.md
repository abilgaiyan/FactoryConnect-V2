# G2 increment 4 — Scoped access-grant issuance

Baseline: `main` `586c66a`, following PR #8. This increment exposes `SqlSecurityAuthority.GrantPermissionAsync` as a server-side command for **existing human principals**. It adds no HTTP endpoint, user enrollment, root-grant creation, sign-in adapter or runtime enrollment.

## Authority contract

The issuer needs both an active `security.access.manage` use grant covering the requested scope and an explicitly named active `CanDelegate` parent for the exact requested action. The parent must belong to that issuer. Both complete ancestor chains are validated under the same installation security gate. These authorities are independent: access management alone cannot create a business grant, and business delegation alone cannot manage access.

The child has one exact action, stable site/machine scope and explicit effective interval. Its scope and interval cannot exceed its parent; every ancestor is already checked for the same constraints. Delegation-only parents may issue use grants, as required by the initial administrator profile. The issuer cannot grant themselves authority, use an unknown or revoked recipient, grant human permissions to a runtime, create a root or select another issuer's parent. A child must permit use or delegation and must not already be expired. Existing SQL permission constraints and exact action comparison apply. Chains are capped at 64 grant nodes including the root; new issuance cannot append beyond that bound.

Future-dated children may be recorded but cannot authorize until their effective start. The parent must be active at issuance. A start before issuance does not backdate a historical grant or audit event: issuance is committed only now, with the parent interval as its lower bound. No historical authorization claim is made.

## Transaction, retries and revocation

Order: operation-ID lock → exclusive installation security gate → fresh SQL UTC/state/session → access management → exact completed-result check → expected epoch → delegation closure/recipient/interval checks → append grant + operation + issuance mapping + audit → increment authority epoch → commit. No nested authorization transaction or lock upgrade is used. Policy/machine locks, if later needed, remain after the security gate.

Caller-owned session hash is copied before asynchronous work. The versioned immutable request fingerprint includes exact parent, recipient, grant ID, action, scope, flags, UTC interval, expected epoch and reason. Exact concurrent retries return the stored receipt and append one audit/epoch change. Changed payload or reused grant identity rejects. Equivalent offset representations of the same instants have the same fingerprint.

Completed receipts require the original actor's current session and access-management permission for their scope. Returning an old receipt does not reapply its grant and does not require the old delegation parent to remain eligible. The current grant's usability still depends on every ancestor; later revocation denies use and new issuance. Regrant creates a new branch and does not resurrect revoked descendants.

All issuance and revocation writers take the exclusive security gate. If issuance commits first, the child is recorded and later parent revocation invalidates it. If revocation commits first, waiting issuance reads the newly committed authority and rejects. Expected epochs reject conflicting concurrent access changes; a registered request is not a permission lease. Recorded times are not proof of commit visibility.

## SQL and acceptance

Migration 006 adds immutable `security.GrantIssuance`. Composite foreign keys bind its actor to both the grant's grantor and operation's actor, and unique operation/grant IDs ensure one mapping. Existing grants are preserved; bootstrap/history fixtures are not relabelled as new issuance. Grant, mapping, receipt, audit and epoch roll back together on failure.

Disposable SQL fixtures prove permission/scope/expiry/depth ceilings, exact identities/actions, use versus delegation, no self-escalation, unknown/runtime/revoked recipient denial, exact/concurrent retries, fingerprint conflicts, durable operation mapping, UTC equivalence, audit rollback and current authorization for result retrieval. Audit-barrier fixtures observe actual SQL application-lock waits in both issuance/revocation orders; no fixed sleep is used as ordering proof.

This closes the grant-issuance kernel increment only. Human identity enrollment/lifecycle, real OIDC/browser sign-in, recovery, runtime credentials, least-privilege SQL roles and configuration workflows remain separate roadmap items. No production authority or data is seeded by the application.
