-- DEV-ONLY, explicitly invoked repair for the already-bootstrapped local installation.
-- Run in FactoryConnect_V2_Dev after migration 023; never run automatically or in production.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51200, 'Refusing repair outside FactoryConnect_V2_Dev.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @lockResult int;
    EXEC @lockResult = sys.sp_getapplock
        @Resource=N'FC2:dev-bootstrap-master-grant', @LockMode='Exclusive',
        @LockOwner='Transaction', @LockTimeout=10000;
    IF @lockResult < 0 THROW 51201, 'Could not obtain exclusive repair lock.', 1;

    DECLARE @installation uniqueidentifier='AAE0C57A-BEA6-427F-8046-4E22DDFD63E9';
    DECLARE @principal uniqueidentifier;
    DECLARE @from datetimeoffset(7), @until datetimeoffset(7);
    DECLARE @op uniqueidentifier=NEWID(), @grant uniqueidentifier=NEWID();
    DECLARE @epoch bigint, @request varbinary(max), @result varbinary(max);

    IF (SELECT COUNT(*) FROM platform.InstallationIdentity WHERE InstallationId=@installation)<>1
        THROW 51202, 'Unexpected installation identity.', 1;
    IF NOT EXISTS(SELECT 1 FROM platform.SchemaMigration WHERE MigrationId='023-master-administration-permission')
        THROW 51203, 'Required permission migration 023 has not been applied.', 1;
    IF (SELECT COUNT(*) FROM security.InitialBootstrap)<>1
        THROW 51204, 'Expected exactly one initial bootstrap.', 1;
    SELECT @principal=b.PrincipalId FROM security.InitialBootstrap b;
    IF @principal IS NULL OR NOT EXISTS(
        SELECT 1 FROM security.Principal WHERE PrincipalId=@principal AND Kind='H')
        THROW 51205, 'Bootstrap administrator is missing.', 1;
    IF EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=@principal)
        THROW 51206, 'Bootstrap administrator was revoked.', 1;
    IF EXISTS(SELECT 1 FROM security.PermissionGrant WHERE Permission='configuration.master.manage')
        THROW 51207, 'Master administration grant already exists; no repair applied.', 1;
    IF (SELECT COUNT(*) FROM security.PermissionGrant
        WHERE GranteeId=@principal AND GrantorId=@principal AND RootOrigin='bootstrap'
          AND ParentGrantId IS NULL AND ScopeKind='I')<>13
        THROW 51208, 'Unexpected bootstrap grant baseline; manual review required.', 1;
    IF NOT EXISTS(SELECT 1 FROM security.PermissionGrant
        WHERE GranteeId=@principal AND GrantorId=@principal AND RootOrigin='bootstrap'
          AND Permission='security.access.manage' AND ScopeKind='I' AND UseAllowed=1 AND CanDelegate=1
          AND NOT EXISTS(SELECT 1 FROM security.GrantRevocation WHERE GrantId=security.PermissionGrant.GrantId))
        THROW 51209, 'Usable bootstrap management authority is missing.', 1;
    SELECT @from=MIN(EffectiveFrom),@until=MAX(ExpiresAt)
      FROM security.PermissionGrant WHERE GranteeId=@principal AND RootOrigin='bootstrap';
    IF EXISTS(SELECT 1 FROM security.PermissionGrant
        WHERE GranteeId=@principal AND RootOrigin='bootstrap'
          AND (EffectiveFrom<>@from OR ExpiresAt<>@until))
        THROW 51210, 'Bootstrap grant windows differ; manual review required.', 1;
    IF @until<=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')
        THROW 51211, 'Bootstrap authority expired; cannot repair.', 1;
    SELECT @epoch=AuthorityEpoch FROM security.InstallationState WITH (UPDLOCK,HOLDLOCK)
        WHERE Singleton=1 AND IsSealed=0;
    IF @epoch IS NULL THROW 51212, 'Installation is sealed or missing.', 1;

    SET @request=CONVERT(varbinary(max),N'FC2 dev-only existing-bootstrap master grant repair v1');
    SET @result=CONVERT(varbinary(max),N'configuration.master.manage root grant issued for existing local bootstrap administrator');
    INSERT security.SecurityOperation
        (OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
    VALUES(@op,@principal,@request,HASHBYTES('SHA2_256',@request),'I',@result);
    INSERT security.PermissionGrant
        (GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
    VALUES(@grant,@principal,@principal,'bootstrap','configuration.master.manage','I',1,1,@from,@until);
    INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
    VALUES(NEWID(),@op,@principal,'dev-bootstrap-master-grant-repair',
           N'Explicit local development repair of missing master administration bootstrap permission after migration 023');
    UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
    COMMIT TRANSACTION;
    SELECT 'PASS: development bootstrap master administration grant repaired' AS Result,
           @principal AS AdministratorPrincipalId,@grant AS GrantId,@epoch+1 AS AuthorityEpoch;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
