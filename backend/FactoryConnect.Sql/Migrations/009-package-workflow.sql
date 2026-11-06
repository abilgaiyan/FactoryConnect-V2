CREATE TYPE configuration.PackageMachineBatch AS TABLE (
    MachineId uniqueidentifier NOT NULL PRIMARY KEY,ProductionLineId uniqueidentifier NOT NULL,
    EffectiveFrom datetimeoffset(7) NOT NULL,EffectiveTo datetimeoffset(7) NULL,
    ScheduleRevisionId uniqueidentifier NOT NULL,ScheduleHash binary(32) NOT NULL,
    BreakPolicyRevisionId uniqueidentifier NOT NULL,BreakPolicyHash binary(32) NOT NULL
);
GO
CREATE TYPE configuration.PackageSignalBatch AS TABLE (
    MappingId uniqueidentifier NOT NULL PRIMARY KEY,MachineId uniqueidentifier NOT NULL,
    SemanticKey nvarchar(100) NOT NULL,Protocol nvarchar(100) NOT NULL,StreamKey nvarchar(1024) NOT NULL,
    AddressEncoding nvarchar(100) NOT NULL,AddressBytes varbinary(4096) NOT NULL,Normalization nvarchar(100) NOT NULL
);
GO
CREATE TYPE configuration.AuthorityLockBatch AS TABLE (
    Phase tinyint NOT NULL,Resource nvarchar(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Mode varchar(16) NOT NULL,PRIMARY KEY(Phase,Resource)
);
GO
ALTER TABLE configuration.ConfigurationPackageRevision ADD CONSTRAINT UQ_Revision_ExactHash UNIQUE(SiteId,PackageRevisionId,ContentHash);
CREATE TABLE configuration.PackageSubmission (
    PackageRevisionId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,ContentHash binary(32) NOT NULL,
    AuthorPrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    SubmitterPrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    OperationId uniqueidentifier NOT NULL UNIQUE,
    CONSTRAINT FK_Submission_Revision FOREIGN KEY(SiteId,PackageRevisionId,ContentHash)
        REFERENCES configuration.ConfigurationPackageRevision(SiteId,PackageRevisionId,ContentHash),
    CONSTRAINT FK_Submission_Actor FOREIGN KEY(OperationId,SubmitterPrincipalId)
        REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CONSTRAINT CK_Submission_Creator CHECK(AuthorPrincipalId=SubmitterPrincipalId),
    CONSTRAINT UQ_Submission_Provenance UNIQUE(SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId)
);
CREATE TABLE configuration.PackageReview (
    ReviewId uniqueidentifier NOT NULL PRIMARY KEY CHECK(ReviewId<>'00000000-0000-0000-0000-000000000000'),PackageRevisionId uniqueidentifier NOT NULL UNIQUE,
    SiteId uniqueidentifier NOT NULL,ContentHash binary(32) NOT NULL,
    AuthorPrincipalId uniqueidentifier NOT NULL,SubmitterPrincipalId uniqueidentifier NOT NULL,
    ReviewerPrincipalId uniqueidentifier NOT NULL REFERENCES security.Principal(PrincipalId),
    Decision varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK(DATALENGTH(Decision)=8 AND Decision IN ('approved','rejected')),
    OperationId uniqueidentifier NOT NULL UNIQUE,
    CONSTRAINT FK_Review_Submission FOREIGN KEY(SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId)
        REFERENCES configuration.PackageSubmission(SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId),
    CONSTRAINT FK_Review_Actor FOREIGN KEY(OperationId,ReviewerPrincipalId)
        REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CONSTRAINT CK_Review_Separation CHECK(ReviewerPrincipalId<>AuthorPrincipalId AND ReviewerPrincipalId<>SubmitterPrincipalId)
);
GO
CREATE TRIGGER configuration.TR_Submission_Immutable ON configuration.PackageSubmission INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51010,'Authenticated package submission is immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_Review_Immutable ON configuration.PackageReview INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51010,'Package review decision is immutable.',1; END;
