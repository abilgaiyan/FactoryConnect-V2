param(
  [string]$ServerInstance = '.\FCINVENTORY',
  [string]$SourceDatabase = 'FactoryConnect_P0_20261105',
  [string]$TargetDatabase = 'FactoryConnect_V2_Dev',
  [switch]$Apply,
  [string]$ConfirmTargetDatabase = ''
)
$ErrorActionPreference = 'Stop'
Import-Module SqlServer -ErrorAction Stop
if ($SourceDatabase -eq $TargetDatabase) { throw 'Source and target must differ.' }
foreach ($db in @($SourceDatabase,$TargetDatabase)) {
  if ($db -cnotmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw "Invalid database name: $db" }
}
if ($Apply -and $ConfirmTargetDatabase -cne $TargetDatabase) {
  throw 'For writes, supply -ConfirmTargetDatabase with the exact target database name.'
}
$src = '[' + $SourceDatabase + ']'
$counts = @"
SELECT 'MachineActivityPeriodHistory' AS DataSet, COUNT_BIG(*) AS [RowCount] FROM $src.dbo.MachineActivityPeriodHistory
UNION ALL SELECT 'ShiftMetricAggregate', COUNT_BIG(*) FROM $src.dbo.ShiftMetricAggregate
UNION ALL SELECT 'OperationalMetricProjection', COUNT_BIG(*) FROM $src.dbo.OperationalMetricProjection;
"@
Write-Host "Source: $SourceDatabase (read only); target: $TargetDatabase"
Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $TargetDatabase -Query $counts -TrustServerCertificate -AbortOnError -QueryTimeout 120 | Format-Table
if (-not $Apply) {
  Write-Host 'DRY RUN: No changes made. Pass -Apply -ConfirmTargetDatabase <target> to import historical reporting data.'
  return
}
$sql = @"
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF SCHEMA_ID('legacy_reporting') IS NULL EXEC('CREATE SCHEMA legacy_reporting');
 IF OBJECT_ID('legacy_reporting.Machine','U') IS NULL
  CREATE TABLE legacy_reporting.Machine(
   SourceDatabase sysname NOT NULL, SourceMachineId uniqueidentifier NOT NULL,
   DeviceKey nvarchar(120) NOT NULL, V2MachineId uniqueidentifier NULL,
   CONSTRAINT PK_LegacyMachine PRIMARY KEY(SourceDatabase,SourceMachineId)
  );
 IF OBJECT_ID('legacy_reporting.ActivityPeriod','U') IS NULL
  CREATE TABLE legacy_reporting.ActivityPeriod(
   SourceDatabase sysname NOT NULL, SourceMachineId uniqueidentifier NOT NULL,
   StreamKeyBinary varbinary(512) NOT NULL, StateProcessorIdOrderKey varbinary(769) NOT NULL,
   ProjectionRevision decimal(20,0) NOT NULL, OutputOrdinal int NOT NULL,
   MachineState tinyint NOT NULL, StartedAt datetimeoffset NOT NULL, EndedAt datetimeoffset NULL,
   InstanceId decimal(20,0) NULL, Sequence decimal(20,0) NULL,
   CONSTRAINT PK_LegacyActivity PRIMARY KEY(SourceDatabase,SourceMachineId,StreamKeyBinary,StateProcessorIdOrderKey,ProjectionRevision,OutputOrdinal)
  );
 IF OBJECT_ID('legacy_reporting.ShiftAggregate','U') IS NULL
  CREATE TABLE legacy_reporting.ShiftAggregate(
   SourceDatabase sysname NOT NULL, SourceRowId bigint NOT NULL,
   SourceMachineId uniqueidentifier NOT NULL, SourceSiteId nvarchar(256) NULL,
   ShiftScheduleAssignmentId nvarchar(256) NULL, ShiftId nvarchar(256) NULL,
   ShiftStartsAtUtc datetimeoffset NULL, ShiftEndsAtUtc datetimeoffset NULL,
   MetricInputKey nvarchar(256) NOT NULL, AggregateValue nvarchar(max) NULL, Unit nvarchar(128) NULL,
   InputCount bigint NULL,
   CONSTRAINT PK_LegacyShiftAggregate PRIMARY KEY(SourceDatabase,SourceRowId)
  );
 IF OBJECT_ID('legacy_reporting.MetricProjection','U') IS NULL
  CREATE TABLE legacy_reporting.MetricProjection(
   SourceDatabase sysname NOT NULL, SourceRowId bigint NOT NULL,
   SourceMachineId uniqueidentifier NOT NULL, PeriodKind tinyint NOT NULL,
   SourceSiteId nvarchar(256) NULL, ShiftId nvarchar(256) NULL,
   ShiftStartsAtUtc datetimeoffset NULL, ShiftEndsAtUtc datetimeoffset NULL,
   ProductionBusinessDate date NULL, MetricKey nvarchar(256) NOT NULL,
   DefinitionVersion nvarchar(256) NULL, Status tinyint NOT NULL,
   MetricValue nvarchar(max) NULL, Unit nvarchar(128) NULL, ReasonCode tinyint NULL,
   CONSTRAINT PK_LegacyMetricProjection PRIMARY KEY(SourceDatabase,SourceRowId)
  );
 IF NOT EXISTS (SELECT 1 FROM legacy_reporting.Machine WHERE SourceDatabase=N'$SourceDatabase' AND SourceMachineId='677b030b-1ede-4a67-82ae-6a494791357f')
 INSERT legacy_reporting.Machine(SourceDatabase,SourceMachineId,DeviceKey) VALUES(N'$SourceDatabase','677b030b-1ede-4a67-82ae-6a494791357f',N'DEWAS-SR-GG-003');
 IF NOT EXISTS (SELECT 1 FROM legacy_reporting.Machine WHERE SourceDatabase=N'$SourceDatabase' AND SourceMachineId='bfa0dc97-9a68-45ed-b189-1dacb7d863f0')
 INSERT legacy_reporting.Machine(SourceDatabase,SourceMachineId,DeviceKey) VALUES(N'$SourceDatabase','bfa0dc97-9a68-45ed-b189-1dacb7d863f0',N'DEWAS-SR-MOXA-001');
 INSERT legacy_reporting.ActivityPeriod(SourceDatabase,SourceMachineId,StreamKeyBinary,StateProcessorIdOrderKey,ProjectionRevision,OutputOrdinal,MachineState,StartedAt,EndedAt,InstanceId,Sequence)
 SELECT N'$SourceDatabase',s.MachineId,s.StreamKeyBinary,s.StateProcessorIdOrderKey,s.ProjectionRevision,s.OutputOrdinal,s.MachineState,s.StartedAt,s.EndedAt,s.InstanceId,s.Sequence
 FROM $src.dbo.MachineActivityPeriodHistory s
 WHERE NOT EXISTS(SELECT 1 FROM legacy_reporting.ActivityPeriod d WHERE d.SourceDatabase=N'$SourceDatabase' AND d.SourceMachineId=s.MachineId AND d.StreamKeyBinary=s.StreamKeyBinary AND d.StateProcessorIdOrderKey=s.StateProcessorIdOrderKey AND d.ProjectionRevision=s.ProjectionRevision AND d.OutputOrdinal=s.OutputOrdinal);
 INSERT legacy_reporting.ShiftAggregate(SourceDatabase,SourceRowId,SourceMachineId,SourceSiteId,ShiftScheduleAssignmentId,ShiftId,ShiftStartsAtUtc,ShiftEndsAtUtc,MetricInputKey,AggregateValue,Unit,InputCount)
 SELECT N'$SourceDatabase',s.ShiftMetricAggregateRowId,s.MachineId,s.SiteId,s.ShiftScheduleAssignmentId,s.ShiftId,s.ShiftStartsAtUtc,s.ShiftEndsAtUtc,s.MetricInputKey,s.AggregateValue,s.Unit,s.InputCount
 FROM $src.dbo.ShiftMetricAggregate s
 WHERE NOT EXISTS(SELECT 1 FROM legacy_reporting.ShiftAggregate d WHERE d.SourceDatabase=N'$SourceDatabase' AND d.SourceRowId=s.ShiftMetricAggregateRowId);
 INSERT legacy_reporting.MetricProjection(SourceDatabase,SourceRowId,SourceMachineId,PeriodKind,SourceSiteId,ShiftId,ShiftStartsAtUtc,ShiftEndsAtUtc,ProductionBusinessDate,MetricKey,DefinitionVersion,Status,MetricValue,Unit,ReasonCode)
 SELECT N'$SourceDatabase',s.OperationalMetricProjectionRowId,s.MachineId,s.PeriodKind,s.PeriodSiteId,s.ShiftId,s.ShiftStartsAtUtc,s.ShiftEndsAtUtc,s.ProductionBusinessDate,s.MetricKey,s.DefinitionVersion,s.Status,s.MetricValue,s.Unit,s.ReasonCode
 FROM $src.dbo.OperationalMetricProjection s
 WHERE NOT EXISTS(SELECT 1 FROM legacy_reporting.MetricProjection d WHERE d.SourceDatabase=N'$SourceDatabase' AND d.SourceRowId=s.OperationalMetricProjectionRowId);
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
SELECT 'ActivityPeriod' AS DataSet,COUNT_BIG(*) AS [RowCount] FROM legacy_reporting.ActivityPeriod WHERE SourceDatabase=N'$SourceDatabase'
UNION ALL SELECT 'ShiftAggregate',COUNT_BIG(*) FROM legacy_reporting.ShiftAggregate WHERE SourceDatabase=N'$SourceDatabase'
UNION ALL SELECT 'MetricProjection',COUNT_BIG(*) FROM legacy_reporting.MetricProjection WHERE SourceDatabase=N'$SourceDatabase';
"@
Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $TargetDatabase -Query $sql -TrustServerCertificate -AbortOnError -QueryTimeout 600 -MaxCharLength 1000000 | Format-Table
Write-Host 'Historical reporting import committed to the explicitly confirmed V2 development database.'
