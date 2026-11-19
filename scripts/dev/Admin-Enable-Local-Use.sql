-- ONE-TIME LOCAL DEVELOPMENT REPAIR ONLY. Never run on production.
-- Changes UseAllowed on the existing bootstrap roots; does not create or impersonate grants.
-- The administrator principal MUST be verified against the current fc2-admin login first.
-- Intended for the isolated FactoryConnect_V2_Dev database only.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51000, 'Refusing to change grants outside FactoryConnect_V2_Dev.', 1;

DECLARE @PrincipalId uniqueidentifier = 'AB2D17FC-2630-4991-8533-2D83316CC82A';
DECLARE @InstallationId uniqueidentifier = 'AAE0C57A-BEA6-427F-8046-4E22DDFD63E9';
DECLARE @LockResult int, @Changed int;
-- Match security.PermissionGrant.Permission (binary collation), not the database default.
DECLARE @Expected TABLE(Permission varchar(100) COLLATE Latin1_General_100_BIN2 PRIMARY KEY);
INSERT @Expected(Permission) VALUES
 ('security.access.manage'),('configuration.read'),('configuration.edit'),
 ('configuration.submit'),('configuration.review'),('configuration.activate'),
 ('configuration.master.manage'),('commissioning.read'),('audit.read'),
 ('machine.suspend'),('machine.enable'),('policy.revoke'),
 ('runtime.assign'),('runtime.credential.manage');

BEGIN TRY
 BEGIN TRANSACTION;
 EXEC @LockResult=sys.sp_getapplock
   @Resource='fc2:security:aae0c57abea6427f80464e22ddfd63e9',
   @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
 IF @LockResult < 0 THROW 51001,'Could not acquire authority lock.',1;
 IF NOT EXISTS (SELECT 1 FROM platform.InstallationIdentity
                WHERE Singleton=1 AND InstallationId=@InstallationId)
    THROW 51002,'Unexpected installation identity.',1;
 IF NOT EXISTS (SELECT 1 FROM security.Principal
                WHERE PrincipalId=@PrincipalId AND Kind='H')
    THROW 51003,'Expected human administrator principal is missing.',1;
 IF (SELECT COUNT(*) FROM security.PermissionGrant
     WHERE GranteeId=@PrincipalId AND GrantorId=@PrincipalId
       AND ParentGrantId IS NULL AND RootOrigin='bootstrap'
       AND ScopeKind='I' AND SiteId IS NULL AND MachineId IS NULL
       AND CanDelegate=1 AND Permission IN (SELECT Permission FROM @Expected)) <> 14
    THROW 51004,'Expected 14 bootstrap administrator grants were not found.',1;
 IF EXISTS (SELECT 1 FROM security.PermissionGrant g
            JOIN @Expected e ON e.Permission=g.Permission
            WHERE g.GranteeId=@PrincipalId AND g.ParentGrantId IS NULL
              AND (g.ExpiresAt<=SYSDATETIMEOFFSET() OR
                   EXISTS(SELECT 1 FROM security.GrantRevocation r WHERE r.GrantId=g.GrantId)))
    THROW 51005,'An administrator root grant is expired or revoked.',1;

 UPDATE g SET UseAllowed=1
 FROM security.PermissionGrant g
 JOIN @Expected e ON e.Permission=g.Permission
 WHERE g.GranteeId=@PrincipalId AND g.GrantorId=@PrincipalId
   AND g.ParentGrantId IS NULL AND g.RootOrigin='bootstrap'
   AND g.ScopeKind='I' AND g.CanDelegate=1 AND g.UseAllowed=0;
 SET @Changed=@@ROWCOUNT;
 IF @Changed NOT IN (0,11)
    THROW 51006,'Unexpected grant state; rolling back.',1;
 IF @Changed>0
    UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1
    WHERE Singleton=1 AND IsSealed=0;
 IF @Changed>0 AND @@ROWCOUNT<>1
    THROW 51007,'Could not advance authority epoch.',1;
 COMMIT TRANSACTION;
 SELECT @PrincipalId AS PrincipalId,@Changed AS EnabledPermissions,
        N'Local development repair completed; sign out and sign in again' AS Result;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
