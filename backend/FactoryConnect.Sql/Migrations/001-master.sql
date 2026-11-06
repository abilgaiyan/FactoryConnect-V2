CREATE SCHEMA [master];
GO
CREATE TABLE [master].Site (
    SiteId uniqueidentifier NOT NULL CONSTRAINT PK_Site PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    IsRetired bit NOT NULL CONSTRAINT DF_Site_Retired DEFAULT 0,
    Version rowversion NOT NULL,
    CONSTRAINT CK_Site_Id CHECK (SiteId <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT CK_Site_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0)
);
CREATE TABLE [master].ProductionLine (
    ProductionLineId uniqueidentifier NOT NULL CONSTRAINT PK_ProductionLine PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsRetired bit NOT NULL CONSTRAINT DF_Line_Retired DEFAULT 0,
    Version rowversion NOT NULL,
    CONSTRAINT FK_Line_Site FOREIGN KEY (SiteId) REFERENCES [master].Site(SiteId),
    CONSTRAINT UQ_Line_Site UNIQUE (SiteId, ProductionLineId),
    CONSTRAINT CK_Line_Id CHECK (ProductionLineId <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT CK_Line_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0)
);
CREATE TABLE [master].Machine (
    MachineId uniqueidentifier NOT NULL CONSTRAINT PK_Machine PRIMARY KEY,
    SiteId uniqueidentifier NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsRetired bit NOT NULL CONSTRAINT DF_Machine_Retired DEFAULT 0,
    Version rowversion NOT NULL,
    CONSTRAINT FK_Machine_Site FOREIGN KEY (SiteId) REFERENCES [master].Site(SiteId),
    CONSTRAINT UQ_Machine_Site UNIQUE (SiteId, MachineId),
    CONSTRAINT CK_Machine_Id CHECK (MachineId <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT CK_Machine_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0)
);
-- No line or adapter address belongs to mutable machine identity.
-- Effective assignments and aliases follow in the configuration slice.
GO
CREATE TRIGGER [master].TR_Site_StableIdentity ON [master].Site AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(SiteId) THROW 51002, 'Stable site identity cannot be updated.', 1;
END;
GO
CREATE TRIGGER [master].TR_Line_StableIdentity ON [master].ProductionLine AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(ProductionLineId) OR UPDATE(SiteId)
        THROW 51002, 'Stable line identity and site ownership cannot be updated.', 1;
END;
GO
CREATE TRIGGER [master].TR_Machine_StableIdentity ON [master].Machine AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(MachineId) OR UPDATE(SiteId)
        THROW 51002, 'Stable machine identity and site ownership cannot be updated.', 1;
END;
