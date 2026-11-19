-- Read-only local identity and grant mapping for delegated permission setup.
SET NOCOUNT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51000, 'Restricted to FactoryConnect_V2_Dev.', 1;

-- Human issuer/subject are UTF-8 bytes. Display locally; do not upload raw tokens.
SELECT PrincipalId, Kind,
       TRY_CONVERT(varchar(2048),IssuerBytes) AS Issuer,
       TRY_CONVERT(varchar(2048),SubjectBytes) AS Subject
FROM security.Principal
WHERE Kind='H'
ORDER BY PrincipalId;

SELECT g.GranteeId AS DelegatorPrincipalId, g.GrantId AS ParentGrantId,
       g.Permission, g.ScopeKind, g.SiteId, g.MachineId,
       g.UseAllowed, g.CanDelegate, g.EffectiveFrom, g.ExpiresAt
FROM security.PermissionGrant AS g
WHERE g.CanDelegate=1 AND g.ExpiresAt>SYSUTCDATETIME()
  AND NOT EXISTS(SELECT 1 FROM security.GrantRevocation r WHERE r.GrantId=g.GrantId)
ORDER BY g.GranteeId,g.Permission;

SELECT AuthorityEpoch, IsSealed FROM security.InstallationState WHERE Singleton=1;
