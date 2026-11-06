CREATE TABLE security.BootstrapTrust (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK(Singleton=1),
    InstallationId uniqueidentifier NOT NULL REFERENCES platform.InstallationIdentity(InstallationId),
    ManifestBytes varbinary(max) NOT NULL CHECK(DATALENGTH(ManifestBytes)>0),
    ManifestHash binary(32) NOT NULL,
    CONSTRAINT CK_Bootstrap_ManifestHash CHECK(ManifestHash=HASHBYTES('SHA2_256',ManifestBytes))
);
CREATE TABLE security.BootstrapCustodianKey (
    KeyId uniqueidentifier NOT NULL PRIMARY KEY,
    CustodianId uniqueidentifier NOT NULL,
    IdentityHash binary(32) NOT NULL,
    PublicKeySpki varbinary(2048) NOT NULL CHECK(DATALENGTH(PublicKeySpki)>0),
    PublicKeyHash binary(32) NOT NULL UNIQUE,
    ValidFrom datetimeoffset(7) NOT NULL,
    ExpiresAt datetimeoffset(7) NOT NULL,
    CONSTRAINT CK_Bootstrap_KeyHash CHECK(PublicKeyHash=HASHBYTES('SHA2_256',PublicKeySpki)),
    CONSTRAINT CK_Bootstrap_KeyWindow CHECK(ExpiresAt>ValidFrom),
    CONSTRAINT UQ_Bootstrap_CustodianBinding UNIQUE(KeyId,CustodianId,IdentityHash)
);
CREATE TABLE security.BootstrapCustodianRevocation (
    KeyId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.BootstrapCustodianKey(KeyId),
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId),
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE security.InitialBootstrap (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK(Singleton=1),
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    PrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    NonceHash binary(32) NOT NULL UNIQUE,
    ApproverKeyId uniqueidentifier NOT NULL,
    ApproverCustodianId uniqueidentifier NOT NULL,
    ApproverIdentityHash binary(32) NOT NULL,
    ExecutorKeyId uniqueidentifier NOT NULL,
    ExecutorCustodianId uniqueidentifier NOT NULL,
    ExecutorIdentityHash binary(32) NOT NULL,
    ApproverSignature varbinary(1024) NOT NULL CHECK(DATALENGTH(ApproverSignature)>=384),
    ExecutorSignature varbinary(1024) NOT NULL CHECK(DATALENGTH(ExecutorSignature)>=384),
    RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Bootstrap_Approver FOREIGN KEY(ApproverKeyId,ApproverCustodianId,ApproverIdentityHash)
        REFERENCES security.BootstrapCustodianKey(KeyId,CustodianId,IdentityHash),
    CONSTRAINT FK_Bootstrap_Executor FOREIGN KEY(ExecutorKeyId,ExecutorCustodianId,ExecutorIdentityHash)
        REFERENCES security.BootstrapCustodianKey(KeyId,CustodianId,IdentityHash),
    CONSTRAINT CK_Bootstrap_Separation CHECK(ApproverKeyId<>ExecutorKeyId AND ApproverCustodianId<>ExecutorCustodianId AND ApproverIdentityHash<>ExecutorIdentityHash)
);
GO
CREATE TRIGGER security.TR_BootstrapTrust_Immutable ON security.BootstrapTrust INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Bootstrap trust is immutable.',1; END;
GO
CREATE TRIGGER security.TR_BootstrapKey_Immutable ON security.BootstrapCustodianKey INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Bootstrap keys are immutable.',1; END;
GO
CREATE TRIGGER security.TR_BootstrapRevocation_Immutable ON security.BootstrapCustodianRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Custodian revocations are immutable.',1; END;
GO
CREATE TRIGGER security.TR_InitialBootstrap_Immutable ON security.InitialBootstrap INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Initial bootstrap is immutable and single-use.',1; END;
