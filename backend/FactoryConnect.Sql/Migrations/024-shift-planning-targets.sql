-- FC2 shift calendar and target persistence foundation.
-- These are administrative planning records, not machine observations or OEE evidence.
CREATE SCHEMA planning;
GO
CREATE TABLE planning.ShiftDefinition (
    ShiftId uniqueidentifier NOT NULL CONSTRAINT PK_ShiftDefinition PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    DisplayName nvarchar(100) NOT NULL,
    StartMinute smallint NOT NULL,
    EndMinute smallint NOT NULL,
    IsRetired bit NOT NULL CONSTRAINT DF_ShiftDefinition_IsRetired DEFAULT (0),
    Version rowversion NOT NULL,
    CONSTRAINT FK_ShiftDefinition_Site FOREIGN KEY (SiteId) REFERENCES master.Site(SiteId),
    CONSTRAINT UQ_ShiftDefinition_Site UNIQUE (SiteId, ShiftId),
    CONSTRAINT CK_ShiftDefinition_Start CHECK (StartMinute BETWEEN 0 AND 1439),
    CONSTRAINT CK_ShiftDefinition_End CHECK (EndMinute BETWEEN 0 AND 1439),
    CONSTRAINT CK_ShiftDefinition_Duration CHECK (StartMinute <> EndMinute),
    CONSTRAINT CK_ShiftDefinition_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0)
);
CREATE TABLE planning.ShiftBreak (
    BreakId uniqueidentifier NOT NULL CONSTRAINT PK_ShiftBreak PRIMARY KEY,
    ShiftId uniqueidentifier NOT NULL,
    DisplayName nvarchar(100) NOT NULL,
    StartOffsetMinute smallint NOT NULL,
    DurationMinute smallint NOT NULL,
    ExcludeFromPlannedProduction bit NOT NULL CONSTRAINT DF_ShiftBreak_Exclude DEFAULT (1),
    CONSTRAINT FK_ShiftBreak_Shift FOREIGN KEY (ShiftId) REFERENCES planning.ShiftDefinition(ShiftId),
    CONSTRAINT CK_ShiftBreak_Start CHECK (StartOffsetMinute >= 0 AND StartOffsetMinute < 1440),
    CONSTRAINT CK_ShiftBreak_Duration CHECK (DurationMinute > 0 AND DurationMinute <= 480),
    CONSTRAINT CK_ShiftBreak_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0)
);
CREATE TABLE planning.ShiftTargetRevision (
    TargetRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_ShiftTargetRevision PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftId uniqueidentifier NOT NULL,
    BusinessDate date NOT NULL,
    Revision int NOT NULL,
    TargetQuantity decimal(18,3) NOT NULL,
    QuantityUnit nvarchar(50) NOT NULL,
    ProductionOrder nvarchar(100) NULL,
    PartNumber nvarchar(100) NULL,
    Operation nvarchar(100) NULL,
    RevisionReason nvarchar(1000) NOT NULL,
    RecordedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ShiftTarget_Recorded DEFAULT (SYSUTCDATETIME()),
    RecordedByPrincipalId uniqueidentifier NOT NULL,
    CONSTRAINT FK_ShiftTarget_Machine FOREIGN KEY (SiteId, MachineId) REFERENCES master.Machine(SiteId, MachineId),
    CONSTRAINT FK_ShiftTarget_Shift FOREIGN KEY (SiteId, ShiftId) REFERENCES planning.ShiftDefinition(SiteId, ShiftId),
    CONSTRAINT FK_ShiftTarget_Principal FOREIGN KEY (RecordedByPrincipalId) REFERENCES security.Principal(PrincipalId),
    CONSTRAINT UQ_ShiftTarget_Revision UNIQUE (MachineId, ShiftId, BusinessDate, Revision),
    CONSTRAINT CK_ShiftTarget_Revision CHECK (Revision > 0),
    CONSTRAINT CK_ShiftTarget_Quantity CHECK (TargetQuantity >= 0),
    CONSTRAINT CK_ShiftTarget_Unit CHECK (LEN(LTRIM(RTRIM(QuantityUnit))) > 0),
    CONSTRAINT CK_ShiftTarget_Reason CHECK (LEN(LTRIM(RTRIM(RevisionReason))) > 0)
);
CREATE INDEX IX_ShiftTarget_Day ON planning.ShiftTargetRevision(SiteId, BusinessDate, MachineId, ShiftId, Revision DESC);
GO
-- A break must fit within the containing shift, including shifts crossing midnight.
-- The application must also reject overlapping breaks and overlapping active shifts.
CREATE TRIGGER planning.TR_ShiftBreak_Bounds ON planning.ShiftBreak AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted b
        JOIN planning.ShiftDefinition s ON s.ShiftId = b.ShiftId
        WHERE b.StartOffsetMinute + b.DurationMinute >
            CASE WHEN s.EndMinute > s.StartMinute THEN s.EndMinute - s.StartMinute
                 ELSE 1440 - s.StartMinute + s.EndMinute END
    ) THROW 51040, 'Break must be fully contained within its shift.', 1;
END;
GO
