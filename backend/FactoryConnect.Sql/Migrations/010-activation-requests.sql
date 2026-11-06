ALTER TABLE security.Principal ADD CONSTRAINT UQ_Principal_Kind UNIQUE(PrincipalId,Kind);
CREATE TABLE security.RuntimeIdentity (
    RuntimeIdentityId uniqueidentifier NOT NULL PRIMARY KEY,
    PrincipalKind char(1) NOT NULL CHECK(PrincipalKind='R'),
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId),
    CONSTRAINT FK_Runtime_Principal FOREIGN KEY(RuntimeIdentityId,PrincipalKind) REFERENCES security.Principal(PrincipalId,Kind)
);
CREATE TABLE security.RuntimeCredentialGeneration (
    RuntimeIdentityId uniqueidentifier NOT NULL REFERENCES security.RuntimeIdentity(RuntimeIdentityId),
    CredentialGeneration bigint NOT NULL CHECK(CredentialGeneration>0),
    CredentialBindingHash binary(32) NOT NULL,
    ValidFrom datetimeoffset(7) NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId),
    PRIMARY KEY(RuntimeIdentityId,CredentialGeneration),
    CHECK(ExpiresAt>ValidFrom AND DATEPART(TZOFFSET,ValidFrom)=0 AND DATEPART(TZOFFSET,ExpiresAt)=0)
);
CREATE TABLE security.RuntimeCredentialState (
    RuntimeIdentityId uniqueidentifier NOT NULL PRIMARY KEY,CredentialGeneration bigint NOT NULL,
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration) REFERENCES security.RuntimeCredentialGeneration(RuntimeIdentityId,CredentialGeneration)
);
CREATE TABLE security.RuntimeCredentialRevocation (
    RuntimeIdentityId uniqueidentifier NOT NULL,CredentialGeneration bigint NOT NULL,
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId),
    PRIMARY KEY(RuntimeIdentityId,CredentialGeneration),
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration) REFERENCES security.RuntimeCredentialGeneration(RuntimeIdentityId,CredentialGeneration)
);
CREATE TABLE configuration.RuntimeAssignment (
    AssignmentId uniqueidentifier NOT NULL PRIMARY KEY,SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,
    RuntimeIdentityId uniqueidentifier NOT NULL REFERENCES security.RuntimeIdentity(RuntimeIdentityId),
    AssignmentGeneration bigint NOT NULL CHECK(AssignmentGeneration>0),
    EffectiveFrom datetimeoffset(7) NOT NULL,EffectiveTo datetimeoffset(7) NULL,
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId),
    FOREIGN KEY(SiteId,MachineId) REFERENCES [master].Machine(SiteId,MachineId),
    CONSTRAINT UQ_RuntimeAssignment_Generation UNIQUE(SiteId,MachineId,AssignmentGeneration),
    CONSTRAINT UQ_RuntimeAssignment_Exact UNIQUE(SiteId,MachineId,AssignmentId,AssignmentGeneration,RuntimeIdentityId),
    CHECK((EffectiveTo IS NULL OR EffectiveTo>EffectiveFrom) AND DATEPART(TZOFFSET,EffectiveFrom)=0 AND (EffectiveTo IS NULL OR DATEPART(TZOFFSET,EffectiveTo)=0))
);
CREATE TABLE configuration.RuntimeAssignmentState (
    MachineId uniqueidentifier NOT NULL PRIMARY KEY,SiteId uniqueidentifier NOT NULL,AssignmentId uniqueidentifier NOT NULL,
    AssignmentGeneration bigint NOT NULL,RuntimeIdentityId uniqueidentifier NOT NULL,
    FOREIGN KEY(SiteId,MachineId,AssignmentId,AssignmentGeneration,RuntimeIdentityId)
        REFERENCES configuration.RuntimeAssignment(SiteId,MachineId,AssignmentId,AssignmentGeneration,RuntimeIdentityId)
);
CREATE TABLE configuration.RuntimeAssignmentRevocation (
    AssignmentId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES configuration.RuntimeAssignment(AssignmentId),
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId)
);
CREATE TABLE configuration.PackageApprovalRevocation (
    ReviewId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES configuration.PackageReview(ReviewId),
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId)
);
CREATE TABLE configuration.PolicySafetyRevocation (
    RevocationId uniqueidentifier NOT NULL PRIMARY KEY,SiteId uniqueidentifier NOT NULL,PolicyKind varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TimezoneRevisionId uniqueidentifier NULL,ScheduleRevisionId uniqueidentifier NULL,BreakPolicyRevisionId uniqueidentifier NULL,
    ContentHash binary(32) NOT NULL,EffectiveFrom datetimeoffset(7) NOT NULL CHECK(DATEPART(TZOFFSET,EffectiveFrom)=0),
    OperationId uniqueidentifier NOT NULL REFERENCES security.SecurityOperation(OperationId),
    CHECK((PolicyKind='timezone' AND DATALENGTH(PolicyKind)=8 AND TimezoneRevisionId IS NOT NULL AND ScheduleRevisionId IS NULL AND BreakPolicyRevisionId IS NULL) OR
        (PolicyKind='schedule' AND DATALENGTH(PolicyKind)=8 AND TimezoneRevisionId IS NULL AND ScheduleRevisionId IS NOT NULL AND BreakPolicyRevisionId IS NULL) OR
        (PolicyKind='break' AND DATALENGTH(PolicyKind)=5 AND TimezoneRevisionId IS NULL AND ScheduleRevisionId IS NULL AND BreakPolicyRevisionId IS NOT NULL)),
    FOREIGN KEY(SiteId,TimezoneRevisionId,ContentHash) REFERENCES configuration.SiteTimezoneRevision(SiteId,TimezoneRevisionId,ContentHash),
    FOREIGN KEY(SiteId,ScheduleRevisionId,ContentHash) REFERENCES configuration.SharedScheduleRevision(SiteId,ScheduleRevisionId,ContentHash),
    FOREIGN KEY(SiteId,BreakPolicyRevisionId,ContentHash) REFERENCES configuration.SharedBreakPolicyRevision(SiteId,BreakPolicyRevisionId,ContentHash),
    UNIQUE(PolicyKind,SiteId,TimezoneRevisionId,ScheduleRevisionId,BreakPolicyRevisionId)
);
ALTER TABLE configuration.PackageReview ADD CONSTRAINT UQ_Review_ApprovalPin UNIQUE(SiteId,PackageRevisionId,ContentHash,ReviewId,Decision);
CREATE TABLE configuration.ActivationRequest (
    ActivationId uniqueidentifier NOT NULL PRIMARY KEY CHECK(ActivationId<>'00000000-0000-0000-0000-000000000000'),
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,ActivationGeneration bigint NOT NULL CHECK(ActivationGeneration>0),
    PackageRevisionId uniqueidentifier NOT NULL,ContentHash binary(32) NOT NULL,ReviewId uniqueidentifier NOT NULL,
    ApprovalDecision varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK(ApprovalDecision='approved' AND DATALENGTH(ApprovalDecision)=8),
    RuntimeIdentityId uniqueidentifier NOT NULL,AssignmentId uniqueidentifier NOT NULL,AssignmentGeneration bigint NOT NULL,
    CredentialGeneration bigint NOT NULL,EffectiveFrom datetimeoffset(7) NOT NULL,EffectiveTo datetimeoffset(7) NULL,
    SupersedesActivationId uniqueidentifier NULL,ActorPrincipalId uniqueidentifier NOT NULL,
    OperationId uniqueidentifier NOT NULL UNIQUE,RecordedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    FOREIGN KEY(SiteId,PackageRevisionId,MachineId) REFERENCES configuration.PackageMachineScope(SiteId,PackageRevisionId,MachineId),
    FOREIGN KEY(SiteId,PackageRevisionId,ContentHash,ReviewId,ApprovalDecision) REFERENCES configuration.PackageReview(SiteId,PackageRevisionId,ContentHash,ReviewId,Decision),
    FOREIGN KEY(SiteId,MachineId,AssignmentId,AssignmentGeneration,RuntimeIdentityId) REFERENCES configuration.RuntimeAssignment(SiteId,MachineId,AssignmentId,AssignmentGeneration,RuntimeIdentityId),
    FOREIGN KEY(RuntimeIdentityId,CredentialGeneration) REFERENCES security.RuntimeCredentialGeneration(RuntimeIdentityId,CredentialGeneration),
    FOREIGN KEY(OperationId,ActorPrincipalId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CONSTRAINT UQ_Activation_Generation UNIQUE(SiteId,MachineId,ActivationGeneration),
    CONSTRAINT UQ_Activation_Head UNIQUE(SiteId,MachineId,ActivationId,ActivationGeneration),
    CONSTRAINT UQ_Activation_Scope UNIQUE(SiteId,MachineId,ActivationId),
    FOREIGN KEY(SiteId,MachineId,SupersedesActivationId) REFERENCES configuration.ActivationRequest(SiteId,MachineId,ActivationId),
    CHECK(SupersedesActivationId IS NULL OR SupersedesActivationId<>ActivationId),
    CHECK((EffectiveTo IS NULL OR EffectiveTo>EffectiveFrom) AND DATEPART(TZOFFSET,EffectiveFrom)=0 AND (EffectiveTo IS NULL OR DATEPART(TZOFFSET,EffectiveTo)=0))
);
CREATE TABLE configuration.MachineActivationRequestHead (
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL PRIMARY KEY,
    ActivationId uniqueidentifier NOT NULL,ActivationGeneration bigint NOT NULL,
    FOREIGN KEY(SiteId,MachineId,ActivationId,ActivationGeneration) REFERENCES configuration.ActivationRequest(SiteId,MachineId,ActivationId,ActivationGeneration)
);
CREATE TABLE configuration.ActivationRequestRevocation (
    ActivationId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES configuration.ActivationRequest(ActivationId),
    ActorPrincipalId uniqueidentifier NOT NULL,OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(OperationId,ActorPrincipalId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId)
);

GO
CREATE TRIGGER security.TR_RuntimeIdentity_Immutable ON security.RuntimeIdentity INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER security.TR_RuntimeCredentialGeneration_Immutable ON security.RuntimeCredentialGeneration INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER security.TR_RuntimeCredentialRevocation_Immutable ON security.RuntimeCredentialRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER configuration.TR_RuntimeAssignment_Immutable ON configuration.RuntimeAssignment INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER configuration.TR_RuntimeAssignmentRevocation_Immutable ON configuration.RuntimeAssignmentRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER configuration.TR_PackageApprovalRevocation_Immutable ON configuration.PackageApprovalRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER configuration.TR_PolicySafetyRevocation_Immutable ON configuration.PolicySafetyRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER configuration.TR_ActivationRequest_Immutable ON configuration.ActivationRequest INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;

GO
CREATE TRIGGER configuration.TR_ActivationRequestRevocation_Immutable ON configuration.ActivationRequestRevocation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Activation prerequisite or intent history is immutable.',1; END;
