-- FC2: Site-managed downtime reason catalogue.
-- Reason codes are stable identifiers; retiring a reason preserves historical references.
-- This is administrative classification, not evidence that a stop occurred.
CREATE TABLE planning.StopReason (
    StopReasonId uniqueidentifier NOT NULL CONSTRAINT PK_StopReason PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    ReasonCode nvarchar(40) NOT NULL,
    DisplayName nvarchar(150) NOT NULL,
    Category nvarchar(40) NOT NULL,
    IsPlanned bit NOT NULL CONSTRAINT DF_StopReason_Planned DEFAULT (0),
    IsRetired bit NOT NULL CONSTRAINT DF_StopReason_Retired DEFAULT (0),
    Version rowversion NOT NULL,
    CONSTRAINT FK_StopReason_Site FOREIGN KEY (SiteId) REFERENCES master.Site(SiteId),
    CONSTRAINT UQ_StopReason_SiteCode UNIQUE (SiteId, ReasonCode),
    CONSTRAINT UQ_StopReason_SiteId UNIQUE (SiteId, StopReasonId),
    CONSTRAINT CK_StopReason_Code CHECK (LEN(LTRIM(RTRIM(ReasonCode))) > 0),
    CONSTRAINT CK_StopReason_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0),
    CONSTRAINT CK_StopReason_Category CHECK (Category IN
        (N'Breakdown', N'Setup', N'Tooling', N'Material', N'Quality',
         N'Operator', N'Planned', N'Other'))
);
GO
CREATE INDEX IX_StopReason_Active ON planning.StopReason(SiteId, IsRetired, Category, DisplayName);
GO
-- Supervisor explanations must preserve the original reason and stop occurrence
-- provenance. No mutable stop/evidence row is altered by this separate record.
CREATE TABLE planning.StopReasonAssignment (
    StopReasonAssignmentId uniqueidentifier NOT NULL CONSTRAINT PK_StopReasonAssignment PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    StopReasonId uniqueidentifier NOT NULL,
    StopStartedAtUtc datetime2(7) NOT NULL,
    StopEndedAtUtc datetime2(7) NOT NULL,
    Explanation nvarchar(1000) NULL,
    RecordedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_StopReasonAssignment_Recorded DEFAULT (SYSUTCDATETIME()),
    RecordedByPrincipalId uniqueidentifier NOT NULL,
    CONSTRAINT FK_StopReasonAssignment_Machine FOREIGN KEY (SiteId, MachineId)
        REFERENCES master.Machine(SiteId, MachineId),
    CONSTRAINT FK_StopReasonAssignment_Reason FOREIGN KEY (SiteId, StopReasonId)
        REFERENCES planning.StopReason(SiteId, StopReasonId),
    CONSTRAINT FK_StopReasonAssignment_Principal FOREIGN KEY (RecordedByPrincipalId)
        REFERENCES security.Principal(PrincipalId),
    CONSTRAINT CK_StopReasonAssignment_Bounds CHECK (StopEndedAtUtc > StopStartedAtUtc)
);
GO
CREATE INDEX IX_StopReasonAssignment_MachineTime
    ON planning.StopReasonAssignment(SiteId, MachineId, StopStartedAtUtc, StopEndedAtUtc);
GO
