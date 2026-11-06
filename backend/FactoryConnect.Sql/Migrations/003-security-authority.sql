CREATE SCHEMA security;
GO
CREATE TABLE security.InstallationState (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK(Singleton=1),
    IsSealed bit NOT NULL DEFAULT 1,
    AuthorityEpoch bigint NOT NULL DEFAULT 0 CHECK(AuthorityEpoch>=0),
    Version rowversion NOT NULL
);
INSERT security.InstallationState(Singleton) VALUES(1);
CREATE TABLE security.Principal (
    PrincipalId uniqueidentifier NOT NULL PRIMARY KEY CHECK(PrincipalId<>'00000000-0000-0000-0000-000000000000'),
    Kind char(1) NOT NULL CHECK(Kind IN ('H','R')),
    IssuerBytes varbinary(2048) NOT NULL CHECK(DATALENGTH(IssuerBytes)>0),
    SubjectBytes varbinary(2048) NOT NULL CHECK(DATALENGTH(SubjectBytes)>0),
    BindingHash AS CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(binary(4),DATALENGTH(IssuerBytes))+IssuerBytes+SubjectBytes)) PERSISTED,
    CONSTRAINT UQ_Principal_Binding UNIQUE(BindingHash)
);
CREATE TABLE security.SessionAuthority (
    TokenHash binary(32) NOT NULL PRIMARY KEY,
    PrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    IssuedAt datetimeoffset(7) NOT NULL,
    ExpiresAt datetimeoffset(7) NOT NULL,
    AuthenticationContext nvarchar(200) NOT NULL,
    IssuedEpoch bigint NOT NULL CHECK(IssuedEpoch>=0),
    CONSTRAINT CK_Session_Expiry CHECK(ExpiresAt>IssuedAt)
);
CREATE TABLE security.PermissionGrant (
    GrantId uniqueidentifier NOT NULL PRIMARY KEY CHECK(GrantId<>'00000000-0000-0000-0000-000000000000'),
    GranteeId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    GrantorId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    ParentGrantId uniqueidentifier NULL REFERENCES security.PermissionGrant(GrantId),
    RootOrigin varchar(32) NULL,
    Permission varchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ScopeKind char(1) NOT NULL CHECK(ScopeKind IN ('I','S','M')),
    SiteId uniqueidentifier NULL REFERENCES [master].Site(SiteId),
    MachineId uniqueidentifier NULL,
    UseAllowed bit NOT NULL,
    CanDelegate bit NOT NULL,
    EffectiveFrom datetimeoffset(7) NOT NULL,
    ExpiresAt datetimeoffset(7) NOT NULL,
    CONSTRAINT FK_Grant_Machine FOREIGN KEY(SiteId,MachineId) REFERENCES [master].Machine(SiteId,MachineId),
    CONSTRAINT CK_Grant_Scope CHECK((ScopeKind='I' AND SiteId IS NULL AND MachineId IS NULL) OR
        (ScopeKind='S' AND SiteId IS NOT NULL AND MachineId IS NULL) OR
        (ScopeKind='M' AND SiteId IS NOT NULL AND MachineId IS NOT NULL)),
    CONSTRAINT CK_Grant_Parent CHECK(ParentGrantId IS NULL AND RootOrigin IS NOT NULL AND RootOrigin='bootstrap' OR
        ParentGrantId IS NOT NULL AND RootOrigin IS NULL AND ParentGrantId<>GrantId),
    CONSTRAINT CK_Grant_Expiry CHECK(ExpiresAt>EffectiveFrom),
    CONSTRAINT CK_Grant_Permission CHECK(Permission IN ('security.access.manage','configuration.read','configuration.edit',
        'configuration.submit','configuration.review','configuration.activate','commissioning.read','audit.read',
        'machine.suspend','machine.enable','policy.revoke','runtime.assign','runtime.credential.manage'))
);
CREATE INDEX IX_Grant_ActorPermission ON security.PermissionGrant(GranteeId,Permission) INCLUDE(ParentGrantId);
CREATE TABLE security.SecurityOperation (
    OperationId uniqueidentifier NOT NULL PRIMARY KEY,
    ActorPrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    RequestBytes varbinary(max) NOT NULL,
    RequestHash binary(32) NOT NULL,
    ScopeKind char(1) NOT NULL,
    SiteId uniqueidentifier NULL,
    MachineId uniqueidentifier NULL,
    ResultBytes varbinary(max) NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Operation_Actor UNIQUE(OperationId,ActorPrincipalId),
    CONSTRAINT CK_Operation_Scope CHECK((ScopeKind='I' AND SiteId IS NULL AND MachineId IS NULL) OR
        (ScopeKind='S' AND SiteId IS NOT NULL AND MachineId IS NULL) OR
        (ScopeKind='M' AND SiteId IS NOT NULL AND MachineId IS NOT NULL)),
    CONSTRAINT CK_Operation_Size CHECK(DATALENGTH(RequestBytes) BETWEEN 1 AND 16384 AND DATALENGTH(ResultBytes) BETWEEN 1 AND 16384),
    CONSTRAINT CK_SecurityOperation_Hash CHECK(RequestHash=HASHBYTES('SHA2_256',RequestBytes))
);
CREATE TABLE security.GrantRevocation (
    GrantId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.PermissionGrant(GrantId),
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE security.SessionRevocation (
    TokenHash binary(32) NOT NULL PRIMARY KEY REFERENCES security.SessionAuthority(TokenHash),
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE security.PrincipalRevocation (
    PrincipalId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.Principal(PrincipalId),
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE security.SecurityAudit (
    AuditId uniqueidentifier NOT NULL PRIMARY KEY,
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    ActorPrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    Action varchar(100) NOT NULL,
    Reason nvarchar(500) NOT NULL CHECK(LEN(LTRIM(RTRIM(Reason)))>0),
    CONSTRAINT FK_Audit_OperationActor FOREIGN KEY(OperationId,ActorPrincipalId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TRIGGER security.TR_Principal_Immutable ON security.Principal INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security authority records are immutable.',1; END;
GO
CREATE TRIGGER security.TR_Session_Immutable ON security.SessionAuthority INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security authority records are immutable.',1; END;
GO
CREATE TRIGGER security.TR_Grant_Immutable ON security.PermissionGrant INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security authority records are immutable.',1; END;
GO
CREATE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security operation records are immutable.',1; END;
GO
CREATE TRIGGER security.TR_GrantRevocation_Immutable ON security.GrantRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security revocations are immutable.',1; END;
GO
CREATE TRIGGER security.TR_SessionRevocation_Immutable ON security.SessionRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security revocations are immutable.',1; END;
GO
CREATE TRIGGER security.TR_PrincipalRevocation_Immutable ON security.PrincipalRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security revocations are immutable.',1; END;
GO
CREATE TRIGGER security.TR_Audit_Immutable ON security.SecurityAudit INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Security audit records are immutable.',1; END;
