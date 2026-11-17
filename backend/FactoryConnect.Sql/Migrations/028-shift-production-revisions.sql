-- FC2-165: Durable, append-only supervisor-declared shift production quantities.
-- These are human-entered records, not verified machine counts or OEE results.
-- Corrections append a new revision; original declarations are never overwritten.
CREATE TABLE planning.ShiftProductionRevision (
    ProductionRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_ShiftProductionRevision PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftId uniqueidentifier NOT NULL,
    BusinessDate date NOT NULL,
    Revision int NOT NULL,
    GoodQuantity decimal(18,3) NOT NULL,
    RejectedQuantity decimal(18,3) NOT NULL,
    QuantityUnit nvarchar(50) NOT NULL,
    ProductionOrder nvarchar(100) NULL,
    PartNumber nvarchar(100) NULL,
    Operation nvarchar(100) NULL,
    RevisionReason nvarchar(1000) NOT NULL,
    RecordedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ShiftProduction_Recorded DEFAULT (SYSUTCDATETIME()),
    RecordedByPrincipalId uniqueidentifier NOT NULL,
    CONSTRAINT FK_ShiftProduction_Machine FOREIGN KEY (SiteId, MachineId)
        REFERENCES master.Machine(SiteId, MachineId),
    CONSTRAINT FK_ShiftProduction_Shift FOREIGN KEY (SiteId, ShiftId)
        REFERENCES planning.ShiftDefinition(SiteId, ShiftId),
    CONSTRAINT FK_ShiftProduction_Principal FOREIGN KEY (RecordedByPrincipalId)
        REFERENCES security.Principal(PrincipalId),
    CONSTRAINT UQ_ShiftProduction_Revision UNIQUE (SiteId, MachineId, ShiftId, BusinessDate, Revision),
    CONSTRAINT CK_ShiftProduction_Revision CHECK (Revision > 0),
    CONSTRAINT CK_ShiftProduction_Good CHECK (GoodQuantity >= 0),
    CONSTRAINT CK_ShiftProduction_Rejected CHECK (RejectedQuantity >= 0),
    CONSTRAINT CK_ShiftProduction_Unit CHECK (LEN(LTRIM(RTRIM(QuantityUnit))) > 0),
    CONSTRAINT CK_ShiftProduction_Reason CHECK (LEN(LTRIM(RTRIM(RevisionReason))) > 0)
);
GO
CREATE INDEX IX_ShiftProduction_Day
    ON planning.ShiftProductionRevision(SiteId, BusinessDate, MachineId, ShiftId, Revision DESC);
GO
CREATE VIEW planning.vLatestShiftProduction
AS
SELECT ProductionRevisionId, SiteId, MachineId, ShiftId, BusinessDate, Revision,
       GoodQuantity, RejectedQuantity, QuantityUnit, ProductionOrder, PartNumber,
       Operation, RevisionReason, RecordedAtUtc, RecordedByPrincipalId
FROM (
    SELECT p.*, ROW_NUMBER() OVER (
        PARTITION BY p.SiteId, p.MachineId, p.ShiftId, p.BusinessDate
        ORDER BY p.Revision DESC
    ) AS RevisionRank
    FROM planning.ShiftProductionRevision p
) ranked
WHERE RevisionRank = 1;
GO
