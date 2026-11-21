-- Read-only machine ingestion status for the isolated V2 development database.
-- No runtime grants, activation changes, cursor writes, or evidence inserts.
SET NOCOUNT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51000, 'Expected isolated FactoryConnect_V2_Dev database.', 1;

DECLARE @SiteId uniqueidentifier='54981c9d-d569-4c9f-a977-78e66d02c1f2';
DECLARE @MachineId uniqueidentifier='49befb26-0ac8-4bdf-87fe-a4ff9e6f3bfd';

SELECT 'machine-operational-state' AS RecordType,COUNT_BIG(*) AS RowCount
FROM configuration.MachineOperationalState
WHERE SiteId=@SiteId AND MachineId=@MachineId
UNION ALL
SELECT 'runtime-assignment',COUNT_BIG(*)
FROM configuration.RuntimeAssignment
WHERE SiteId=@SiteId AND MachineId=@MachineId
UNION ALL
SELECT 'activation-acknowledgement',COUNT_BIG(*)
FROM configuration.RuntimeActivationAcknowledgement
WHERE SiteId=@SiteId AND MachineId=@MachineId
UNION ALL
SELECT 'acquisition-batch',COUNT_BIG(*)
FROM acquisition.MtConnectBatch
WHERE SiteId=@SiteId AND MachineId=@MachineId
UNION ALL
SELECT 'acquisition-cursor',COUNT_BIG(*)
FROM acquisition.MtConnectCursor
WHERE SiteId=@SiteId AND MachineId=@MachineId
UNION ALL
SELECT 'acquisition-observation',COUNT_BIG(*)
FROM acquisition.MtConnectObservation
WHERE SiteId=@SiteId AND MachineId=@MachineId;

SELECT InstanceId,NextSequence,BaselineSequence,BatchId
FROM acquisition.MtConnectCursor
WHERE SiteId=@SiteId AND MachineId=@MachineId
ORDER BY InstanceId DESC,NextSequence DESC;

SELECT TOP (10) InstanceId,Sequence,RawValue,SourceTimestamp,ObservedAt,Quality
FROM acquisition.MtConnectObservation
WHERE SiteId=@SiteId AND MachineId=@MachineId
ORDER BY ObservedAt DESC,Sequence DESC;

-- Inspect actual state and assignment records; counts alone do not prove activation.
-- SELECT * is intentionally restricted to the known local simulator and development DB.
SELECT 'machine-operational-state-detail' AS Diagnostic, state.*
FROM configuration.MachineOperationalState AS state
WHERE state.SiteId=@SiteId AND state.MachineId=@MachineId;

SELECT 'runtime-assignment-detail' AS Diagnostic, assignment.*
FROM configuration.RuntimeAssignment AS assignment
WHERE assignment.SiteId=@SiteId AND assignment.MachineId=@MachineId;

SELECT 'activation-acknowledgement-detail' AS Diagnostic, acknowledgement.*
FROM configuration.RuntimeActivationAcknowledgement AS acknowledgement
WHERE acknowledgement.SiteId=@SiteId AND acknowledgement.MachineId=@MachineId;

PRINT 'DIAGNOSTIC ONLY: rows indicate recorded state, not proof of an active acquisition host or authorized activation.';
