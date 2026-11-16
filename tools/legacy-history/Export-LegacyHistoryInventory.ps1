param(
  [string]$ServerInstance = '.\FCINVENTORY',
  [string]$SourceDatabase = 'FactoryConnect_P0_20261105',
  [string]$TargetDatabase = 'FactoryConnect_V2_Dev',
  [string]$OutputDirectory = (Join-Path $PWD 'artifacts\legacy-history-inventory')
)
$ErrorActionPreference = 'Stop'
Import-Module SqlServer -ErrorAction Stop
if ($SourceDatabase -eq $TargetDatabase) { throw 'Source and target databases must differ.' }
if ($SourceDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]*$' -or $TargetDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw 'Database names must contain only letters, digits and underscores.' }
$sourceQuery = @'
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT CAST('MachineObservation' AS nvarchar(80)) AS DataSet, CONVERT(nvarchar(100),MachineId) AS MachineId, COUNT_BIG(*) AS RowCount, MIN(ObservedAt) AS FirstAt, MAX(ObservedAt) AS LastAt FROM dbo.MachineObservation GROUP BY MachineId
UNION ALL
SELECT 'MachineActivityPeriodHistory', CONVERT(nvarchar(100),MachineId), COUNT_BIG(*), MIN(StartedAt), MAX(EndedAt) FROM dbo.MachineActivityPeriodHistory GROUP BY MachineId
UNION ALL
SELECT 'ShiftMetricAggregate', CONVERT(nvarchar(100),MachineId), COUNT_BIG(*), MIN(ShiftStartsAtUtc), MAX(ShiftEndsAtUtc) FROM dbo.ShiftMetricAggregate GROUP BY MachineId
UNION ALL
SELECT 'OperationalMetricProjection', CONVERT(nvarchar(100),MachineId), COUNT_BIG(*), MIN(ShiftStartsAtUtc), MAX(ShiftEndsAtUtc) FROM dbo.OperationalMetricProjection WHERE ShiftStartsAtUtc IS NOT NULL GROUP BY MachineId;
'@
$targetQuery = @'
SELECT CAST(s.SiteId AS nvarchar(100)) AS SiteId, s.DisplayName AS SiteName, CAST(m.MachineId AS nvarchar(100)) AS MachineId, m.DisplayName AS MachineName, m.IsRetired
FROM master.Machine m JOIN master.Site s ON s.SiteId=m.SiteId ORDER BY s.DisplayName,m.DisplayName;
'@
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# SQL ApplicationIntent=ReadOnly is advisory without an availability group; no write SQL is executed.
$source = @(Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $SourceDatabase -Query $sourceQuery -TrustServerCertificate -QueryTimeout 120)
$target = @(Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $TargetDatabase -Query $targetQuery -TrustServerCertificate -QueryTimeout 120)
$source | Select-Object DataSet,MachineId,RowCount,FirstAt,LastAt | Export-Csv (Join-Path $OutputDirectory 'v1-machine-history.csv') -NoTypeInformation -Encoding UTF8
$target | Select-Object SiteId,SiteName,MachineId,MachineName,IsRetired | Export-Csv (Join-Path $OutputDirectory 'v2-machines.csv') -NoTypeInformation -Encoding UTF8
Write-Host "Read-only inventory complete: $OutputDirectory"
Write-Host 'No database changes made. Review identifiers before sharing exported CSVs.'
