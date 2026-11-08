-- Exact-signal next-input discovery, including sparse signals. No LOB payloads.
CREATE INDEX IX_MtConnectObservation_ActivityNext
ON acquisition.MtConnectObservation(MachineId,StreamKeyBytes,MappingId,InstanceId,Sequence)
INCLUDE(SiteId);
-- One producing operation per committed processor generation; bounded latest-ledger check.
CREATE UNIQUE INDEX IX_ActivityOperation_Generation
ON activity.ProcessorOperation(MachineId,StreamKeyBytes,MappingId,AppliedGeneration)
INCLUDE(OperationId);
