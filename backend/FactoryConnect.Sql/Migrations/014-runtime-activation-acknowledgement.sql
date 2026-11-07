ALTER TABLE configuration.ActivationRequest ADD CONSTRAINT UQ_Activation_AcknowledgementPin
    UNIQUE(SiteId,MachineId,ActivationId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration);
ALTER TABLE security.RuntimeCertificateBinding ADD CONSTRAINT UQ_RuntimeCertificate_AcknowledgementPin
    UNIQUE(RuntimeIdentityId,CredentialGeneration,CertificateHash);
CREATE TABLE configuration.RuntimeActivationAcknowledgement (
    AcknowledgementId uniqueidentifier NOT NULL PRIMARY KEY CHECK(AcknowledgementId<>'00000000-0000-0000-0000-000000000000'),
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,
    ActivationId uniqueidentifier NOT NULL UNIQUE,ActivationGeneration bigint NOT NULL,
    PackageRevisionId uniqueidentifier NOT NULL,ContentHash binary(32) NOT NULL,ReviewId uniqueidentifier NOT NULL,
    RuntimeIdentityId uniqueidentifier NOT NULL,AssignmentId uniqueidentifier NOT NULL,AssignmentGeneration bigint NOT NULL,
    CredentialGeneration bigint NOT NULL,CertificateHash binary(32) NOT NULL,
    AppliedFrom datetimeoffset(7) NOT NULL,EffectiveTo datetimeoffset(7) NULL,
    OperationId uniqueidentifier NOT NULL UNIQUE,RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    FOREIGN KEY(SiteId,MachineId,ActivationId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration)
        REFERENCES configuration.ActivationRequest(SiteId,MachineId,ActivationId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration),
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration,CertificateHash) REFERENCES security.RuntimeCertificateBinding(RuntimeIdentityId,CredentialGeneration,CertificateHash),
    FOREIGN KEY(OperationId,RuntimeIdentityId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CONSTRAINT UQ_Acknowledgement_Head UNIQUE(SiteId,MachineId,AcknowledgementId,ActivationGeneration),
    CHECK(DATEPART(TZOFFSET,AppliedFrom)=0 AND (EffectiveTo IS NULL OR (DATEPART(TZOFFSET,EffectiveTo)=0 AND EffectiveTo>AppliedFrom)))
);
CREATE TABLE configuration.MachineAcknowledgementHead (
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL PRIMARY KEY,
    AcknowledgementId uniqueidentifier NOT NULL,ActivationGeneration bigint NOT NULL,
    FOREIGN KEY(SiteId,MachineId,AcknowledgementId,ActivationGeneration)
        REFERENCES configuration.RuntimeActivationAcknowledgement(SiteId,MachineId,AcknowledgementId,ActivationGeneration)
);
GO
CREATE TRIGGER configuration.TR_RuntimeActivationAcknowledgement_Immutable ON configuration.RuntimeActivationAcknowledgement INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Runtime acknowledgement history is immutable.',1; END;
