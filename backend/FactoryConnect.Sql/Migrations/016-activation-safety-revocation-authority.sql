CREATE TABLE configuration.PolicyRevocationProposal (
    ProposalId uniqueidentifier NOT NULL PRIMARY KEY CHECK(ProposalId<>'00000000-0000-0000-0000-000000000000'),
    SiteId uniqueidentifier NOT NULL,PolicyKind varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TimezoneRevisionId uniqueidentifier NULL,ScheduleRevisionId uniqueidentifier NULL,BreakPolicyRevisionId uniqueidentifier NULL,
    ContentHash binary(32) NOT NULL,Classification varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK(Classification='safety-critical' AND DATALENGTH(Classification)=15),
    RequestedEffectiveFrom datetimeoffset(7) NULL,IssuedAt datetimeoffset(7) NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,
    ProposerId uniqueidentifier NOT NULL,ProposerSessionHash binary(32) NOT NULL,PrincipalKind char(1) NOT NULL DEFAULT 'H' CHECK(PrincipalKind='H'),
    OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(ProposerId,PrincipalKind) REFERENCES security.Principal(PrincipalId,Kind),
    FOREIGN KEY(ProposerSessionHash,ProposerId) REFERENCES security.SessionAuthority(TokenHash,PrincipalId),
    FOREIGN KEY(OperationId,ProposerId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    FOREIGN KEY(SiteId,TimezoneRevisionId,ContentHash) REFERENCES configuration.SiteTimezoneRevision(SiteId,TimezoneRevisionId,ContentHash),
    FOREIGN KEY(SiteId,ScheduleRevisionId,ContentHash) REFERENCES configuration.SharedScheduleRevision(SiteId,ScheduleRevisionId,ContentHash),
    FOREIGN KEY(SiteId,BreakPolicyRevisionId,ContentHash) REFERENCES configuration.SharedBreakPolicyRevision(SiteId,BreakPolicyRevisionId,ContentHash),
    CONSTRAINT UQ_PolicyProposal_Actor UNIQUE(ProposalId,ProposerId),
    CHECK((PolicyKind='timezone' AND DATALENGTH(PolicyKind)=8 AND TimezoneRevisionId IS NOT NULL AND ScheduleRevisionId IS NULL AND BreakPolicyRevisionId IS NULL) OR
        (PolicyKind='schedule' AND DATALENGTH(PolicyKind)=8 AND TimezoneRevisionId IS NULL AND ScheduleRevisionId IS NOT NULL AND BreakPolicyRevisionId IS NULL) OR
        (PolicyKind='break' AND DATALENGTH(PolicyKind)=5 AND TimezoneRevisionId IS NULL AND ScheduleRevisionId IS NULL AND BreakPolicyRevisionId IS NOT NULL)),
    CHECK(DATEPART(TZOFFSET,IssuedAt)=0 AND DATEPART(TZOFFSET,ExpiresAt)=0 AND ExpiresAt>IssuedAt AND ExpiresAt<=DATEADD(minute,15,IssuedAt)),
    CHECK(RequestedEffectiveFrom IS NULL OR (DATEPART(TZOFFSET,RequestedEffectiveFrom)=0 AND RequestedEffectiveFrom>IssuedAt))
);
-- Keep all prior facts. Additional events may only accelerate a prior schedule through the guarded command.
DECLARE @constraint sysname;
IF (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('configuration.PolicySafetyRevocation') AND type='UQ')<>1
    THROW 51041,'Unexpected policy revocation uniqueness baseline.',1;
SELECT @constraint=name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('configuration.PolicySafetyRevocation') AND type='UQ';
DECLARE @dropConstraint nvarchar(max)=N'ALTER TABLE configuration.PolicySafetyRevocation DROP CONSTRAINT '+QUOTENAME(@constraint);
EXEC sys.sp_executesql @dropConstraint;
ALTER TABLE configuration.PolicySafetyRevocation ADD CONSTRAINT UQ_PolicyRevocation_Operation UNIQUE(RevocationId,OperationId);
CREATE INDEX IX_PolicySafety_Timezone ON configuration.PolicySafetyRevocation(SiteId,PolicyKind,TimezoneRevisionId,EffectiveFrom) INCLUDE(ContentHash) WHERE TimezoneRevisionId IS NOT NULL;
CREATE INDEX IX_PolicySafety_Schedule ON configuration.PolicySafetyRevocation(SiteId,PolicyKind,ScheduleRevisionId,EffectiveFrom) INCLUDE(ContentHash) WHERE ScheduleRevisionId IS NOT NULL;
CREATE INDEX IX_PolicySafety_Break ON configuration.PolicySafetyRevocation(SiteId,PolicyKind,BreakPolicyRevisionId,EffectiveFrom) INCLUDE(ContentHash) WHERE BreakPolicyRevisionId IS NOT NULL;
CREATE TABLE configuration.PolicyRevocationAuthority (
    RevocationId uniqueidentifier NOT NULL PRIMARY KEY,ProposalId uniqueidentifier NOT NULL UNIQUE,
    ProposerId uniqueidentifier NOT NULL,ApproverId uniqueidentifier NOT NULL,PrincipalKind char(1) NOT NULL DEFAULT 'H' CHECK(PrincipalKind='H'),
    RecordedAt datetimeoffset(7) NOT NULL CHECK(DATEPART(TZOFFSET,RecordedAt)=0),OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(RevocationId,OperationId) REFERENCES configuration.PolicySafetyRevocation(RevocationId,OperationId),
    FOREIGN KEY(ProposalId,ProposerId) REFERENCES configuration.PolicyRevocationProposal(ProposalId,ProposerId),
    FOREIGN KEY(ApproverId,PrincipalKind) REFERENCES security.Principal(PrincipalId,Kind),
    FOREIGN KEY(OperationId,ApproverId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CHECK(ProposerId<>ApproverId)
);
ALTER TABLE configuration.PackageApprovalRevocation ADD CONSTRAINT UQ_ApprovalRevocation_Operation UNIQUE(ReviewId,OperationId);
ALTER TABLE configuration.PackageReview ADD CONSTRAINT UQ_Review_WithdrawalPin UNIQUE(ReviewId,SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId);
CREATE TABLE configuration.ApprovalWithdrawalAuthority (
    ReviewId uniqueidentifier NOT NULL PRIMARY KEY,SiteId uniqueidentifier NOT NULL,PackageRevisionId uniqueidentifier NOT NULL,ContentHash binary(32) NOT NULL,
    AuthorPrincipalId uniqueidentifier NOT NULL,SubmitterPrincipalId uniqueidentifier NOT NULL,ActorPrincipalId uniqueidentifier NOT NULL,
    PrincipalKind char(1) NOT NULL DEFAULT 'H' CHECK(PrincipalKind='H'),RecordedAt datetimeoffset(7) NOT NULL CHECK(DATEPART(TZOFFSET,RecordedAt)=0),OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(ReviewId,OperationId) REFERENCES configuration.PackageApprovalRevocation(ReviewId,OperationId),
    FOREIGN KEY(ReviewId,SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId) REFERENCES configuration.PackageReview(ReviewId,SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId),
    FOREIGN KEY(ActorPrincipalId,PrincipalKind) REFERENCES security.Principal(PrincipalId,Kind),
    FOREIGN KEY(OperationId,ActorPrincipalId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CHECK(ActorPrincipalId<>AuthorPrincipalId AND ActorPrincipalId<>SubmitterPrincipalId)
);
GO
CREATE TRIGGER configuration.TR_PolicyRevocationProposal_Immutable ON configuration.PolicyRevocationProposal INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Policy revocation proposal is immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_PolicyRevocationAuthority_Immutable ON configuration.PolicyRevocationAuthority INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Policy revocation authority is immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_ApprovalWithdrawalAuthority_Immutable ON configuration.ApprovalWithdrawalAuthority INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Approval withdrawal authority is immutable.',1; END;
