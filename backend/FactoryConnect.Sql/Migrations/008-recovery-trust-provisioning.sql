CREATE TABLE security.RecoveryTrustRevision (
    TrustId uniqueidentifier NOT NULL PRIMARY KEY,
    Generation bigint NOT NULL UNIQUE CHECK(Generation>=1),
    ManifestBytes varbinary(max) NOT NULL CHECK(DATALENGTH(ManifestBytes)>0),
    ManifestHash binary(32) NOT NULL UNIQUE,
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    CONSTRAINT CK_RecoveryTrust_Hash CHECK(ManifestHash=HASHBYTES('SHA2_256',ManifestBytes))
);
CREATE TABLE security.RecoveryTrustState (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK(Singleton=1),
    CurrentTrustId uniqueidentifier NOT NULL REFERENCES security.RecoveryTrustRevision(TrustId),
    Version rowversion NOT NULL
);
CREATE TABLE security.RecoveryCustodianKey (
    KeyId uniqueidentifier NOT NULL PRIMARY KEY,
    TrustId uniqueidentifier NOT NULL REFERENCES security.RecoveryTrustRevision(TrustId),
    CustodianId uniqueidentifier NOT NULL,
    IdentityHash binary(32) NOT NULL,
    PublicKeySpki varbinary(2048) NOT NULL CHECK(DATALENGTH(PublicKeySpki)>0),
    PublicKeyHash binary(32) NOT NULL UNIQUE,
    ValidFrom datetimeoffset(7) NOT NULL,
    ExpiresAt datetimeoffset(7) NOT NULL,
    CONSTRAINT CK_RecoveryCustodian_Hash CHECK(PublicKeyHash=HASHBYTES('SHA2_256',PublicKeySpki)),
    CONSTRAINT CK_RecoveryCustodian_Window CHECK(ExpiresAt>ValidFrom)
);
CREATE TABLE security.RecoveryTrustProvisioning (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK(Singleton=1),
    TrustId uniqueidentifier NOT NULL UNIQUE REFERENCES security.RecoveryTrustRevision(TrustId),
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    NonceHash binary(32) NOT NULL UNIQUE,
    ApproverKeyId uniqueidentifier NOT NULL REFERENCES security.BootstrapCustodianKey(KeyId),
    ExecutorKeyId uniqueidentifier NOT NULL REFERENCES security.BootstrapCustodianKey(KeyId),
    ApproverSignature varbinary(1024) NOT NULL CHECK(DATALENGTH(ApproverSignature)>=384),
    ExecutorSignature varbinary(1024) NOT NULL CHECK(DATALENGTH(ExecutorSignature)>=384),
    RecoveryFirstKeyId uniqueidentifier NOT NULL REFERENCES security.RecoveryCustodianKey(KeyId),
    RecoverySecondKeyId uniqueidentifier NOT NULL REFERENCES security.RecoveryCustodianKey(KeyId),
    RecoveryFirstSignature varbinary(1024) NOT NULL CHECK(DATALENGTH(RecoveryFirstSignature)>=384),
    RecoverySecondSignature varbinary(1024) NOT NULL CHECK(DATALENGTH(RecoverySecondSignature)>=384),
    CONSTRAINT CK_RecoveryProvisioning_Separation CHECK(ApproverKeyId<>ExecutorKeyId AND RecoveryFirstKeyId<>RecoverySecondKeyId)
);
GO
CREATE TRIGGER security.TR_RecoveryTrustRevision_Immutable ON security.RecoveryTrustRevision INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Recovery trust history is immutable.',1; END;
GO
CREATE TRIGGER security.TR_RecoveryCustodianKey_Immutable ON security.RecoveryCustodianKey INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Recovery public keys are immutable.',1; END;
GO
CREATE TRIGGER security.TR_RecoveryProvisioning_Immutable ON security.RecoveryTrustProvisioning INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Initial recovery provisioning is immutable.',1; END;
