# Local administrator repair (development only)

This is an exceptional, reversible **local-only** repair for `FactoryConnect_V2_Dev`, not a supported production bootstrap or delegation mechanism.

**Before execution**, confirm that principal `AB2D17FC-2630-4991-8533-2D83316CC82A` belongs to the signed-in `fc2-admin` account. The preflight shows it owns the bootstrap roots, but does not prove the Keycloak username binding. The repair does **not** infer this binding.

The SQL changes only `UseAllowed` on 11 existing bootstrap grants (all 14 remain delegable). It does not insert grants, forge issuance records, modify issuer/subject identity bindings, or disable authorization. It increments `security.InstallationState.AuthorityEpoch` under the same authority application lock used by the SQL kernel.

This is an out-of-band local repair, not a normal audited security operation. Take a local DB backup first. Keep the SQL output and Git commit as the maintenance record. Do not deploy this script to a customer environment.

After verifying the principal binding, run:

```powershell
sqlcmd -S ".\FCINVENTORY" -d "FactoryConnect_V2_Dev" -E -b -i ".\scripts\dev\Admin-Enable-Local-Use.sql"
```

Expected output: `EnabledPermissions = 11` on first run, `0` if already applied. Any other prior state aborts. Sign out/restart the browser session, sign in as `fc2-admin`, and verify activation readiness. Restore from the pre-change backup to reverse this out-of-band repair; do not hand-edit authority records in production.
