-- FactoryConnect V2 local acquisition readiness (read-only).
-- Run ONLY against FactoryConnect_V2_Dev; no inserts, updates, deletes or activation.
SET NOCOUNT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51000, 'Expected isolated FactoryConnect_V2_Dev database.', 1;

SELECT DB_NAME() AS DatabaseName,
       CAST('read-only' AS varchar(16)) AS CheckMode;

SELECT s.name AS SchemaName, t.name AS TableName,
       SUM(CASE WHEN c.name = 'SiteId' THEN 1 ELSE 0 END) AS HasSiteId,
       SUM(CASE WHEN c.name = 'MachineId' THEN 1 ELSE 0 END) AS HasMachineId
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id=t.schema_id
LEFT JOIN sys.columns AS c ON c.object_id=t.object_id
WHERE (s.name='acquisition' AND t.name IN ('MtConnectCursor','MtConnectObservation','MtConnectBatch'))
   OR (s.name='configuration' AND t.name IN ('MachineOperationalState','RuntimeAssignment','RuntimeActivationAcknowledgement'))
GROUP BY s.name,t.name
ORDER BY s.name,t.name;

-- Presence and identity only; no secrets, grants or certificate contents.
SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName,
       ty.name AS SqlType, c.is_nullable AS IsNullable
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id=t.schema_id
JOIN sys.columns AS c ON c.object_id=t.object_id
JOIN sys.types AS ty ON ty.user_type_id=c.user_type_id
WHERE (s.name='acquisition' AND t.name IN ('MtConnectCursor','MtConnectObservation'))
   OR (s.name='configuration' AND t.name IN ('RuntimeAssignment','RuntimeActivationAcknowledgement','MachineOperationalState'))
ORDER BY s.name,t.name,c.column_id;
