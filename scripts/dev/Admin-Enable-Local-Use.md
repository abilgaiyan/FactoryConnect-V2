# Local administrator grants — immutable authority

**The direct SQL repair has been retired.** `TR_Grant_Immutable` correctly rejects updates to `security.PermissionGrant` (SQL Msg 51030). Do not disable triggers, modify bootstrap roots, or bypass authority checks.

The existing bootstrap administrator has installation-scope `CanDelegate=1` roots for the permissions but `UseAllowed=0` for most of them. The supported path is `POST /api/admin/v2/security/permission-grants`, which appends a child grant, issuance, operation, audit record and advances the epoch atomically. **Self-grants are prohibited** by `SqlSecurityAuthority.GrantPermissionAsync`.

For a local development workflow:

1. Confirm which Keycloak account maps to each `security.Principal.PrincipalId` (do not infer identity from the two GUIDs).
2. Sign in as the bootstrap administrator that holds `security.access.manage` and `CanDelegate` root grants.
3. Use the audited API to grant the **other** enrolled human principal the required site/installation permissions. Provide a fresh `ExpectedEpoch`, a unique `OperationId` and `GrantId` per request, and `ParentGrantId` matching the exact live delegable permission. Grant times must fall within the parent's interval. Refresh epoch after each successful grant.
4. Sign in as the grantee and verify `activation-readiness`. If the user needs full privileges on the original account, use the second principal as the authorized grantor after its own delegated management and permission grants have been established, respecting self-grant restrictions.

No direct SQL changes are required or supported. Keep `FactoryConnect_V2_Dev` backed up and do not use production identities or deployments.
