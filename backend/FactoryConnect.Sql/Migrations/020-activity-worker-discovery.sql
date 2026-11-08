-- Exact-signal next-input discovery, including sparse signals. No LOB payloads.
CREATE INDEX IX_MtConnectObservation_ActivityNext
ON acquisition.MtConnectObservation(MachineId,StreamKeyBytes,MappingId,InstanceId,Sequence)
INCLUDE(SiteId);
