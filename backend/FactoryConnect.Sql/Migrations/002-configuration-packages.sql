CREATE SCHEMA configuration;
GO
CREATE TABLE configuration.SiteTimezoneRevision (
    TimezoneRevisionId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL REFERENCES [master].Site(SiteId),
    IanaZoneId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TzdbRelease varchar(32) NOT NULL,
    ResolverVersion nvarchar(200) NOT NULL,
    GapPolicy varchar(32) NOT NULL CHECK (GapPolicy IN ('reject','shift-forward')),
    FoldPolicy varchar(32) NOT NULL CHECK (FoldPolicy IN ('reject','earlier','later')),
    RulesBytes varbinary(max) NOT NULL CHECK (DATALENGTH(RulesBytes)>0),
    RulesSha256 binary(32) NOT NULL,
    CanonicalBytes varbinary(max) NOT NULL CHECK (DATALENGTH(CanonicalBytes)>0),
    ContentHash binary(32) NOT NULL,
    CONSTRAINT UQ_Timezone_Pin UNIQUE (SiteId,TimezoneRevisionId,ContentHash),
    CONSTRAINT CK_Timezone_Rules CHECK (RulesSha256=HASHBYTES('SHA2_256',RulesBytes)),
    CONSTRAINT CK_Timezone_Hash CHECK (ContentHash=HASHBYTES('SHA2_256',CanonicalBytes))
);
CREATE TABLE configuration.SharedScheduleRevision (
    ScheduleRevisionId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL REFERENCES [master].Site(SiteId),
    CanonicalBytes varbinary(max) NOT NULL CHECK (DATALENGTH(CanonicalBytes)>0),
    ContentHash binary(32) NOT NULL,
    CONSTRAINT UQ_Schedule_Pin UNIQUE (SiteId,ScheduleRevisionId,ContentHash),
    CONSTRAINT CK_Schedule_Hash CHECK (ContentHash=HASHBYTES('SHA2_256',CanonicalBytes))
);
CREATE TABLE configuration.SharedBreakPolicyRevision (
    BreakPolicyRevisionId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL REFERENCES [master].Site(SiteId),
    CanonicalBytes varbinary(max) NOT NULL CHECK (DATALENGTH(CanonicalBytes)>0),
    ContentHash binary(32) NOT NULL,
    CONSTRAINT UQ_Break_Pin UNIQUE (SiteId,BreakPolicyRevisionId,ContentHash),
    CONSTRAINT CK_Break_Hash CHECK (ContentHash=HASHBYTES('SHA2_256',CanonicalBytes))
);
CREATE TABLE configuration.ConfigurationPackage (
    PackageId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL REFERENCES [master].Site(SiteId),
    CONSTRAINT UQ_Package_Site UNIQUE(SiteId,PackageId)
);
CREATE TABLE configuration.ConfigurationPackageRevision (
    PackageRevisionId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    PackageId uniqueidentifier NOT NULL,
    RevisionNumber int NOT NULL CHECK(RevisionNumber>0),
    CanonicalizationVersion int NOT NULL CHECK(CanonicalizationVersion=1),
    CanonicalBytes varbinary(max) NOT NULL CHECK(DATALENGTH(CanonicalBytes)>0),
    ContentHash binary(32) NOT NULL,
    TimezoneRevisionId uniqueidentifier NOT NULL,
    TimezoneHash binary(32) NOT NULL,
    MachineCount int NOT NULL CHECK(MachineCount>0),
    MappingCount int NOT NULL CHECK(MappingCount>=0),
    SubmittedAt datetimeoffset(7) NULL,
    Version rowversion NOT NULL,
    CONSTRAINT UQ_Package_Revision UNIQUE(PackageId,RevisionNumber),
    CONSTRAINT UQ_Package_Revision_Site UNIQUE(SiteId,PackageRevisionId),
    CONSTRAINT FK_Revision_Package FOREIGN KEY(SiteId,PackageId) REFERENCES configuration.ConfigurationPackage(SiteId,PackageId),
    CONSTRAINT FK_Revision_Timezone FOREIGN KEY(SiteId,TimezoneRevisionId,TimezoneHash) REFERENCES configuration.SiteTimezoneRevision(SiteId,TimezoneRevisionId,ContentHash),
    CONSTRAINT CK_Package_Hash CHECK(ContentHash=HASHBYTES('SHA2_256',CanonicalBytes))
);
CREATE TABLE configuration.PackageMachineScope (
    PackageRevisionId uniqueidentifier NOT NULL,
    SiteId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    CONSTRAINT PK_Package_Scope PRIMARY KEY(PackageRevisionId,MachineId),
    CONSTRAINT UQ_Scope_Site UNIQUE(SiteId,PackageRevisionId,MachineId),
    CONSTRAINT FK_Scope_Package FOREIGN KEY(SiteId,PackageRevisionId) REFERENCES configuration.ConfigurationPackageRevision(SiteId,PackageRevisionId),
    CONSTRAINT FK_Scope_Machine FOREIGN KEY(SiteId,MachineId) REFERENCES [master].Machine(SiteId,MachineId)
);
CREATE TABLE configuration.PackageMachineAssignment (
    PackageRevisionId uniqueidentifier NOT NULL,
    SiteId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ProductionLineId uniqueidentifier NOT NULL,
    EffectiveFrom datetimeoffset(7) NOT NULL,
    EffectiveTo datetimeoffset(7) NULL,
    ScheduleRevisionId uniqueidentifier NOT NULL,
    ScheduleHash binary(32) NOT NULL,
    BreakPolicyRevisionId uniqueidentifier NOT NULL,
    BreakPolicyHash binary(32) NOT NULL,
    CONSTRAINT PK_Package_Assignment PRIMARY KEY(PackageRevisionId,MachineId),
    CONSTRAINT FK_Assignment_Scope FOREIGN KEY(SiteId,PackageRevisionId,MachineId) REFERENCES configuration.PackageMachineScope(SiteId,PackageRevisionId,MachineId),
    CONSTRAINT FK_Assignment_Line FOREIGN KEY(SiteId,ProductionLineId) REFERENCES [master].ProductionLine(SiteId,ProductionLineId),
    CONSTRAINT FK_Assignment_Schedule FOREIGN KEY(SiteId,ScheduleRevisionId,ScheduleHash) REFERENCES configuration.SharedScheduleRevision(SiteId,ScheduleRevisionId,ContentHash),
    CONSTRAINT FK_Assignment_Break FOREIGN KEY(SiteId,BreakPolicyRevisionId,BreakPolicyHash) REFERENCES configuration.SharedBreakPolicyRevision(SiteId,BreakPolicyRevisionId,ContentHash),
    CONSTRAINT CK_Assignment_Interval CHECK(EffectiveTo IS NULL OR EffectiveTo>EffectiveFrom),
    CONSTRAINT CK_Assignment_UTC CHECK(DATEPART(TZOFFSET,EffectiveFrom)=0 AND (EffectiveTo IS NULL OR DATEPART(TZOFFSET,EffectiveTo)=0))
);
CREATE TABLE configuration.PackageSignalMapping (
    MappingId uniqueidentifier NOT NULL PRIMARY KEY,
    PackageRevisionId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    SemanticKey nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SemanticKeyBytes AS CONVERT(varbinary(200),SemanticKey) PERSISTED,
    Protocol nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamKey nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AddressEncoding nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AddressBytes varbinary(4096) NOT NULL CHECK(DATALENGTH(AddressBytes)>0),
    Normalization nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT UQ_Mapping_Semantic UNIQUE(PackageRevisionId,MachineId,SemanticKeyBytes),
    CONSTRAINT FK_Mapping_Scope FOREIGN KEY(PackageRevisionId,MachineId) REFERENCES configuration.PackageMachineScope(PackageRevisionId,MachineId)
);
ALTER TABLE configuration.SiteTimezoneRevision ADD CONSTRAINT CK_SiteTimezoneRevision_NonEmptyId CHECK(TimezoneRevisionId<>'00000000-0000-0000-0000-000000000000');
ALTER TABLE configuration.SharedScheduleRevision ADD CONSTRAINT CK_SharedScheduleRevision_NonEmptyId CHECK(ScheduleRevisionId<>'00000000-0000-0000-0000-000000000000');
ALTER TABLE configuration.SharedBreakPolicyRevision ADD CONSTRAINT CK_SharedBreakPolicyRevision_NonEmptyId CHECK(BreakPolicyRevisionId<>'00000000-0000-0000-0000-000000000000');
ALTER TABLE configuration.ConfigurationPackage ADD CONSTRAINT CK_ConfigurationPackage_NonEmptyId CHECK(PackageId<>'00000000-0000-0000-0000-000000000000');
ALTER TABLE configuration.ConfigurationPackageRevision ADD CONSTRAINT CK_ConfigurationPackageRevision_NonEmptyId CHECK(PackageRevisionId<>'00000000-0000-0000-0000-000000000000');
ALTER TABLE configuration.PackageSignalMapping ADD CONSTRAINT CK_PackageSignalMapping_NonEmptyId CHECK(MappingId<>'00000000-0000-0000-0000-000000000000');
GO
CREATE TRIGGER configuration.TR_Timezone_Immutable ON configuration.SiteTimezoneRevision INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51010, 'Shared timezone revisions are immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_Schedule_Immutable ON configuration.SharedScheduleRevision INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51010, 'Shared schedule revisions are immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_Break_Immutable ON configuration.SharedBreakPolicyRevision INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51010, 'Shared break revisions are immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_Package_Lineage ON configuration.ConfigurationPackage INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51010, 'Package lineage identity is immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_Revision_Immutable ON configuration.ConfigurationPackageRevision AFTER INSERT,UPDATE,DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM deleted WHERE SubmittedAt IS NOT NULL)
        THROW 51010, 'Submitted package content is immutable.',1;
    IF EXISTS(SELECT 1 FROM inserted i WHERE i.SubmittedAt IS NOT NULL AND (
        i.MachineCount<>(SELECT COUNT(*) FROM configuration.PackageMachineScope s WHERE s.PackageRevisionId=i.PackageRevisionId)
        OR i.MachineCount<>(SELECT COUNT(*) FROM configuration.PackageMachineAssignment a WHERE a.PackageRevisionId=i.PackageRevisionId)
        OR i.MappingCount<>(SELECT COUNT(*) FROM configuration.PackageSignalMapping m WHERE m.PackageRevisionId=i.PackageRevisionId)))
        THROW 51010, 'Submitted package has incomplete normalized content.',1;
END;
GO
CREATE TRIGGER configuration.TR_Scope_Immutable ON configuration.PackageMachineScope AFTER INSERT,UPDATE,DELETE AS
BEGIN
    IF EXISTS(SELECT 1 FROM configuration.ConfigurationPackageRevision r JOIN
        (SELECT PackageRevisionId FROM inserted UNION SELECT PackageRevisionId FROM deleted) x ON x.PackageRevisionId=r.PackageRevisionId
        WHERE r.SubmittedAt IS NOT NULL) THROW 51010, 'Submitted machine scope is immutable.',1;
END;
GO
CREATE TRIGGER configuration.TR_Assignment_Immutable ON configuration.PackageMachineAssignment AFTER INSERT,UPDATE,DELETE AS
BEGIN
    IF EXISTS(SELECT 1 FROM configuration.ConfigurationPackageRevision r JOIN
        (SELECT PackageRevisionId FROM inserted UNION SELECT PackageRevisionId FROM deleted) x ON x.PackageRevisionId=r.PackageRevisionId
        WHERE r.SubmittedAt IS NOT NULL) THROW 51010, 'Submitted assignment is immutable.',1;
END;
GO
CREATE TRIGGER configuration.TR_Mapping_Immutable ON configuration.PackageSignalMapping AFTER INSERT,UPDATE,DELETE AS
BEGIN
    IF EXISTS(SELECT 1 FROM configuration.ConfigurationPackageRevision r JOIN
        (SELECT PackageRevisionId FROM inserted UNION SELECT PackageRevisionId FROM deleted) x ON x.PackageRevisionId=r.PackageRevisionId
        WHERE r.SubmittedAt IS NOT NULL) THROW 51010, 'Submitted signal mapping is immutable.',1;
END;
