CREATE SCHEMA acquisition;
GO
ALTER TABLE configuration.RuntimeActivationAcknowledgement ADD CONSTRAINT UQ_Ack_Ingest UNIQUE(SiteId,MachineId,AcknowledgementId,RuntimeIdentityId);
CREATE TABLE acquisition.MtConnectBatch (
    BatchId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.SecurityOperation(OperationId),
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,StreamKeyBytes varbinary(512) NOT NULL CHECK(DATALENGTH(StreamKeyBytes)>0),
    AcknowledgementId uniqueidentifier NOT NULL,
    RuntimeIdentityId uniqueidentifier NOT NULL REFERENCES security.RuntimeIdentity(RuntimeIdentityId),
    InstanceId decimal(20,0) NOT NULL,FromSequence decimal(20,0) NOT NULL,NextSequence decimal(20,0) NOT NULL,FirstAvailableSequence decimal(20,0) NOT NULL,
    RawDocument varbinary(max) NOT NULL CHECK(DATALENGTH(RawDocument) BETWEEN 1 AND 1048576),DocumentHash binary(32) NOT NULL,
    MappedCount int NOT NULL CHECK(MappedCount BETWEEN 0 AND 1000),ReceivedAt datetimeoffset(7) NOT NULL,
    FOREIGN KEY(SiteId,MachineId) REFERENCES [master].Machine(SiteId,MachineId),
    FOREIGN KEY(BatchId,RuntimeIdentityId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    FOREIGN KEY(SiteId,MachineId,AcknowledgementId,RuntimeIdentityId) REFERENCES configuration.RuntimeActivationAcknowledgement(SiteId,MachineId,AcknowledgementId,RuntimeIdentityId),
    CONSTRAINT UQ_MtConnectBatch_Scope UNIQUE(SiteId,MachineId,StreamKeyBytes,BatchId),
    CHECK(InstanceId BETWEEN 0 AND 18446744073709551615 AND FromSequence BETWEEN 0 AND 18446744073709551615 AND NextSequence BETWEEN FromSequence AND 18446744073709551615 AND FirstAvailableSequence BETWEEN 0 AND FromSequence),
    CHECK(DocumentHash=HASHBYTES('SHA2_256',RawDocument) AND DATEPART(TZOFFSET,ReceivedAt)=0)
);
ALTER TABLE configuration.PackageSignalMapping ADD CONSTRAINT UQ_Mapping_Ingest UNIQUE(MachineId,MappingId);
CREATE TABLE acquisition.MtConnectObservation (
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,StreamKeyBytes varbinary(512) NOT NULL,
    InstanceId decimal(20,0) NOT NULL CHECK(InstanceId BETWEEN 0 AND 18446744073709551615),
    Sequence decimal(20,0) NOT NULL CHECK(Sequence BETWEEN 0 AND 18446744073709551615),
    BatchId uniqueidentifier NOT NULL,MappingId uniqueidentifier NOT NULL,
    AddressBytes varbinary(512) NOT NULL CHECK(DATALENGTH(AddressBytes)>0),RawValue nvarchar(max) NOT NULL,SourceTimestamp nvarchar(128) NOT NULL,
    ObservedAt datetimeoffset(7) NOT NULL,Quality varchar(32) NOT NULL CHECK(Quality IN ('source-reported','unavailable')),
    CanonicalBytes varbinary(max) NOT NULL,ContentHash binary(32) NOT NULL,
    CHECK(ContentHash=HASHBYTES('SHA2_256',CanonicalBytes)),
    PRIMARY KEY(MachineId,StreamKeyBytes,InstanceId,Sequence),
    FOREIGN KEY(MachineId,MappingId) REFERENCES configuration.PackageSignalMapping(MachineId,MappingId),
    FOREIGN KEY(SiteId,MachineId,StreamKeyBytes,BatchId) REFERENCES acquisition.MtConnectBatch(SiteId,MachineId,StreamKeyBytes,BatchId)
);
CREATE TABLE acquisition.MtConnectCursor (
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,StreamKeyBytes varbinary(512) NOT NULL,
    InstanceId decimal(20,0) NOT NULL,NextSequence decimal(20,0) NOT NULL,BaselineSequence decimal(20,0) NOT NULL,
    BatchId uniqueidentifier NOT NULL,PRIMARY KEY(MachineId,StreamKeyBytes),
    FOREIGN KEY(SiteId,MachineId,StreamKeyBytes,BatchId) REFERENCES acquisition.MtConnectBatch(SiteId,MachineId,StreamKeyBytes,BatchId),
    CHECK(InstanceId BETWEEN 0 AND 18446744073709551615 AND NextSequence BETWEEN 0 AND 18446744073709551615 AND BaselineSequence BETWEEN 0 AND NextSequence)
);
GO
CREATE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Accepted acquisition batch is immutable.',1; END;
GO
CREATE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Accepted source observation is immutable.',1; END;
