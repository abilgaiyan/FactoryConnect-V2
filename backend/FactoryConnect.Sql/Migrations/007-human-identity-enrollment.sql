CREATE TABLE security.HumanIdentityEnrollment (
    PrincipalId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.Principal(PrincipalId),
    OperationId uniqueidentifier NOT NULL UNIQUE,
    ActorPrincipalId uniqueidentifier NOT NULL,
    CONSTRAINT FK_HumanEnrollment_OperationActor FOREIGN KEY(OperationId,ActorPrincipalId)
        REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId)
);
GO
CREATE TRIGGER security.TR_HumanIdentityEnrollment_Immutable ON security.HumanIdentityEnrollment INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Human enrollment history is immutable.',1; END;
