CREATE SCHEMA activity;
GO
CREATE TABLE activity.ProcessorCheckpoint (
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBytes varbinary(512) NOT NULL,
    MappingId uniqueidentifier NOT NULL,
    QualificationHash binary(32) NOT NULL,
    Generation bigint NOT NULL CHECK (Generation >= 0),
    LastInstanceId decimal(20,0) NULL,
    LastSequence decimal(20,0) NULL,
    CheckpointJson nvarchar(max) NOT NULL,
    CheckpointHash binary(32) NOT NULL,
    UpdatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_ActivityProcessorCheckpoint PRIMARY KEY(MachineId,StreamKeyBytes,MappingId),
    CONSTRAINT FK_ActivityCheckpointMachine FOREIGN KEY(MachineId) REFERENCES [master].Machine(MachineId),
    CONSTRAINT CK_ActivityCheckpointSourcePair CHECK ((LastInstanceId IS NULL AND LastSequence IS NULL)
        OR (LastInstanceId IS NOT NULL AND LastSequence IS NOT NULL)),
    CONSTRAINT CK_ActivityCheckpointSourceRange CHECK (
        (LastInstanceId IS NULL OR LastInstanceId BETWEEN 0 AND 18446744073709551615)
        AND (LastSequence IS NULL OR LastSequence BETWEEN 0 AND 18446744073709551615)),
    CONSTRAINT CK_ActivityCheckpointHash CHECK(CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),CheckpointJson))),
    CONSTRAINT CK_ActivityCheckpointUtc CHECK(DATEPART(TZOFFSET,UpdatedAt)=0)
);
CREATE TABLE activity.ProcessorOperation (
    OperationId uniqueidentifier NOT NULL PRIMARY KEY,
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBytes varbinary(512) NOT NULL,
    MappingId uniqueidentifier NOT NULL,
    RequestHash binary(32) NOT NULL,
    RequestBytes varbinary(max) NOT NULL,
    ExpectedGeneration bigint NOT NULL CHECK(ExpectedGeneration>=0),
    AppliedGeneration bigint NOT NULL CHECK(AppliedGeneration>=1),
    ResultBytes varbinary(max) NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT FK_ActivityOperationScope FOREIGN KEY(MachineId,StreamKeyBytes,MappingId)
        REFERENCES activity.ProcessorCheckpoint(MachineId,StreamKeyBytes,MappingId),
    CONSTRAINT CK_ActivityOperationRequestHash CHECK(RequestHash=HASHBYTES('SHA2_256',RequestBytes)),
    CONSTRAINT CK_ActivityOperationUtc CHECK(DATEPART(TZOFFSET,RecordedAt)=0)
);
CREATE TABLE activity.ClosedPeriod (
    EffectId uniqueidentifier NOT NULL PRIMARY KEY,
    OperationId uniqueidentifier NOT NULL REFERENCES activity.ProcessorOperation(OperationId),
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBytes varbinary(512) NOT NULL,
    MappingId uniqueidentifier NOT NULL,
    QualificationHash binary(32) NOT NULL,
    InstanceId decimal(20,0) NOT NULL,
    StartSequence decimal(20,0) NOT NULL,
    EndSequence decimal(20,0) NOT NULL,
    State varchar(20) NOT NULL CHECK(State IN ('Running','Idle','Stopped','Fault','Unknown')),
    StartedAt datetimeoffset(7) NOT NULL,
    EndedAt datetimeoffset(7) NOT NULL,
    DurationTicks bigint NOT NULL CHECK(DurationTicks>0),
    StartRawValue nvarchar(max) NOT NULL,
    EndRawValue nvarchar(max) NOT NULL,
    CONSTRAINT FK_ActivityPeriodMachine FOREIGN KEY(MachineId) REFERENCES [master].Machine(MachineId),
    CONSTRAINT CK_ActivityPeriodScope CHECK(EndSequence>StartSequence AND
        InstanceId BETWEEN 0 AND 18446744073709551615 AND
        StartSequence BETWEEN 0 AND 18446744073709551615 AND
        EndSequence BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_ActivityPeriodTime CHECK (EndedAt>StartedAt AND
        DATEDIFF_BIG(NANOSECOND,StartedAt,EndedAt)/100=DurationTicks
        AND DATEPART(TZOFFSET,StartedAt)=0 AND DATEPART(TZOFFSET,EndedAt)=0),
    CONSTRAINT UQ_ActivityPeriodEffect UNIQUE(MachineId,StreamKeyBytes,MappingId,InstanceId,StartSequence,EndSequence)
);
CREATE INDEX IX_ActivityClosedPeriod_Keyset
ON activity.ClosedPeriod(MachineId,StreamKeyBytes,MappingId,StartedAt,InstanceId,StartSequence,EffectId)
INCLUDE(QualificationHash);
GO
CREATE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51061,'Activity operation receipt is immutable.',1; END;
GO
CREATE TRIGGER activity.TR_ClosedPeriodImmutable ON activity.ClosedPeriod INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51062,'Closed activity periods are immutable.',1; END;
