-- Public proof material only. Legacy prerequisite fixture generations are not trusted transport keys.
CREATE TABLE security.RuntimeCredentialPublicKey (
    RuntimeIdentityId uniqueidentifier NOT NULL,CredentialGeneration bigint NOT NULL,
    PublicKeySpki varbinary(2048) NOT NULL CHECK(DATALENGTH(PublicKeySpki)>0),
    PublicKeyHash binary(32) NOT NULL UNIQUE,
    OperationId uniqueidentifier NOT NULL UNIQUE REFERENCES security.SecurityOperation(OperationId),
    PRIMARY KEY(RuntimeIdentityId,CredentialGeneration),
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration) REFERENCES security.RuntimeCredentialGeneration(RuntimeIdentityId,CredentialGeneration),
    CHECK(PublicKeyHash=HASHBYTES('SHA2_256',PublicKeySpki))
);
CREATE TABLE security.RuntimeCredentialCeremony (
    OperationId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.SecurityOperation(OperationId),
    RuntimeIdentityId uniqueidentifier NOT NULL,CredentialGeneration bigint NOT NULL,
    NonceHash binary(32) NOT NULL UNIQUE,
    ApproverKeyId uniqueidentifier NOT NULL REFERENCES security.BootstrapCustodianKey(KeyId),
    ExecutorKeyId uniqueidentifier NOT NULL REFERENCES security.BootstrapCustodianKey(KeyId),
    ApproverSignature varbinary(1024) NOT NULL,ExecutorSignature varbinary(1024) NOT NULL,PossessionSignature varbinary(1024) NOT NULL,
    CHECK(ApproverKeyId<>ExecutorKeyId),
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration) REFERENCES security.RuntimeCredentialGeneration(RuntimeIdentityId,CredentialGeneration)
);
GO
CREATE TRIGGER security.TR_RuntimeCredentialPublicKey_Immutable ON security.RuntimeCredentialPublicKey INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Runtime credential proof history is immutable.',1; END;
GO
CREATE TRIGGER security.TR_RuntimeCredentialCeremony_Immutable ON security.RuntimeCredentialCeremony INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Runtime credential proof history is immutable.',1; END;
GO
-- Key reuse checks must remain indexed as enrollment history grows.
CREATE INDEX IX_RuntimeCredential_BindingHash ON security.RuntimeCredentialGeneration(CredentialBindingHash);
