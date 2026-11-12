-- Commissioning assignments are administrative intent only; acquisition remains independently disabled.
CREATE TABLE master.MachineCommissioning (
    MachineId uniqueidentifier NOT NULL PRIMARY KEY,
    ProductionLineId uniqueidentifier NOT NULL,
    MtConnectDeviceId nvarchar(200) NOT NULL,
    LastOperationId uniqueidentifier NOT NULL,
    CONSTRAINT FK_MachineCommissioning_Machine FOREIGN KEY (MachineId)
        REFERENCES master.Machine(MachineId),
    CONSTRAINT FK_MachineCommissioning_Line FOREIGN KEY (ProductionLineId)
        REFERENCES master.ProductionLine(ProductionLineId),
    CONSTRAINT FK_MachineCommissioning_Operation FOREIGN KEY (LastOperationId)
        REFERENCES security.SecurityOperation(OperationId),
    CONSTRAINT CK_MachineCommissioning_DeviceId CHECK (LEN(LTRIM(RTRIM(MtConnectDeviceId))) BETWEEN 1 AND 200)
);
GO
CREATE UNIQUE INDEX UX_MachineCommissioning_LineDevice
    ON master.MachineCommissioning(ProductionLineId, MtConnectDeviceId);
