-- Metadata seeks for bounded accepted-document discovery and one-time bootstrap.
CREATE INDEX IX_MtConnectBatch_ActivityWindow
ON acquisition.MtConnectBatch(MachineId,StreamKeyBytes,InstanceId,NextSequence)
INCLUDE(SiteId,FromSequence,AcknowledgementId);
CREATE INDEX IX_MtConnectBatch_ActivityBootstrap
ON acquisition.MtConnectBatch(MachineId,StreamKeyBytes,InstanceId,AcknowledgementId,FromSequence)
INCLUDE(SiteId);
