ALTER TABLE security.PermissionGrant ADD CONSTRAINT UQ_Grant_Grantor UNIQUE(GrantId,GrantorId);
CREATE TABLE security.GrantIssuance (
    GrantId uniqueidentifier NOT NULL PRIMARY KEY,
    OperationId uniqueidentifier NOT NULL UNIQUE,
    ActorPrincipalId uniqueidentifier NOT NULL,
    CONSTRAINT FK_GrantIssuance_GrantActor FOREIGN KEY(GrantId,ActorPrincipalId)
        REFERENCES security.PermissionGrant(GrantId,GrantorId),
    CONSTRAINT FK_GrantIssuance_OperationActor FOREIGN KEY(OperationId,ActorPrincipalId)
        REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId)
);
GO
CREATE TRIGGER security.TR_GrantIssuance_Immutable ON security.GrantIssuance INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Grant issuance history is immutable.',1; END;
