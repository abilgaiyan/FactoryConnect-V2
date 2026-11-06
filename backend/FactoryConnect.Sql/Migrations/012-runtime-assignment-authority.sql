ALTER TABLE configuration.RuntimeAssignment ADD CONSTRAINT UQ_RuntimeAssignment_Scope UNIQUE(SiteId,MachineId,AssignmentId);
ALTER TABLE configuration.RuntimeAssignment ADD CONSTRAINT UQ_RuntimeAssignment_Operation UNIQUE(AssignmentId,OperationId);
CREATE TABLE configuration.RuntimeAssignmentIssuance (
    AssignmentId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,
    SupersedesAssignmentId uniqueidentifier NULL,
    ActorPrincipalId uniqueidentifier NOT NULL,OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(SiteId,MachineId,AssignmentId) REFERENCES configuration.RuntimeAssignment(SiteId,MachineId,AssignmentId),
    FOREIGN KEY(SiteId,MachineId,SupersedesAssignmentId) REFERENCES configuration.RuntimeAssignment(SiteId,MachineId,AssignmentId),
    FOREIGN KEY(AssignmentId,OperationId) REFERENCES configuration.RuntimeAssignment(AssignmentId,OperationId),
    FOREIGN KEY(OperationId,ActorPrincipalId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CHECK(SupersedesAssignmentId IS NULL OR SupersedesAssignmentId<>AssignmentId)
);
GO
CREATE TRIGGER configuration.TR_RuntimeAssignmentIssuance_Immutable ON configuration.RuntimeAssignmentIssuance INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Runtime assignment issuance is immutable.',1; END;
