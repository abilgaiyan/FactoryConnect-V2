ALTER TABLE security.RuntimeCredentialPublicKey ADD CONSTRAINT UQ_RuntimeCredentialPublicKey_Operation
    UNIQUE(RuntimeIdentityId,CredentialGeneration,OperationId);
CREATE TABLE security.RuntimeCertificateBinding (
    RuntimeIdentityId uniqueidentifier NOT NULL,CredentialGeneration bigint NOT NULL,
    CertificateDer varbinary(max) NOT NULL,CertificateHash binary(32) NOT NULL UNIQUE,
    TrustAnchorDer varbinary(max) NOT NULL,TrustAnchorHash binary(32) NOT NULL,AnchorPublicKeyHash binary(32) NOT NULL,
    ValidFrom datetimeoffset(7) NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,
    OperationId uniqueidentifier NOT NULL UNIQUE,
    PRIMARY KEY(RuntimeIdentityId,CredentialGeneration),
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration,OperationId)
        REFERENCES security.RuntimeCredentialPublicKey(RuntimeIdentityId,CredentialGeneration,OperationId),
    FOREIGN KEY(OperationId) REFERENCES security.RuntimeCredentialCeremony(OperationId),
    CHECK(DATALENGTH(CertificateDer) BETWEEN 1 AND 8192 AND CertificateHash=HASHBYTES('SHA2_256',CertificateDer)),
    CHECK(DATALENGTH(TrustAnchorDer) BETWEEN 1 AND 8192 AND TrustAnchorHash=HASHBYTES('SHA2_256',TrustAnchorDer)),
    CHECK(ExpiresAt>ValidFrom AND DATEPART(TZOFFSET,ValidFrom)=0 AND DATEPART(TZOFFSET,ExpiresAt)=0)
);
GO
CREATE TRIGGER security.TR_RuntimeCertificateBinding_Immutable ON security.RuntimeCertificateBinding INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Runtime certificate binding is immutable.',1; END;

GO
CREATE INDEX IX_RuntimeCertificateBinding_AnchorKey ON security.RuntimeCertificateBinding(AnchorPublicKeyHash);
