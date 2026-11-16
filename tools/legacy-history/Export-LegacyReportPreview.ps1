param(
 [string]$ServerInstance='.\FCINVENTORY',
 [string]$TargetDatabase='FactoryConnect_V2_Dev',
 [string]$SourceDatabase='FactoryConnect_P0_20261105',
 [string]$OutputDirectory=(Join-Path $PWD 'artifacts\legacy-report-preview')
)
$ErrorActionPreference='Stop'
Import-Module SqlServer -ErrorAction Stop
if ($TargetDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]*$' -or $SourceDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw 'Invalid database name' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$queries=@{
 'activity-by-day'=@"
SELECT m.DeviceKey,CONVERT(date, SWITCHOFFSET(a.StartedAt,'+05:30')) AS LocalDate,
 a.MachineState,COUNT_BIG(*) AS [Periods],
 SUM(DATEDIFF_BIG(second,a.StartedAt,a.EndedAt)) AS DurationSeconds
FROM legacy_reporting.ActivityPeriod a
JOIN legacy_reporting.Machine m ON m.SourceDatabase=a.SourceDatabase AND m.SourceMachineId=a.SourceMachineId
WHERE a.SourceDatabase=N'$SourceDatabase' AND a.EndedAt IS NOT NULL AND a.EndedAt>a.StartedAt
GROUP BY m.DeviceKey,CONVERT(date, SWITCHOFFSET(a.StartedAt,'+05:30')),a.MachineState
ORDER BY m.DeviceKey,LocalDate,a.MachineState;
"@
 'shift-metric-values'=@"
SELECT m.DeviceKey,s.ShiftId,s.ShiftStartsAtUtc,s.ShiftEndsAtUtc,
 s.MetricInputKey,s.AggregateValue,s.Unit,s.InputCount
FROM legacy_reporting.ShiftAggregate s
JOIN legacy_reporting.Machine m ON m.SourceDatabase=s.SourceDatabase AND m.SourceMachineId=s.SourceMachineId
WHERE s.SourceDatabase=N'$SourceDatabase'
ORDER BY m.DeviceKey,s.ShiftStartsAtUtc,s.MetricInputKey;
"@
 'metric-projections'=@"
SELECT m.DeviceKey,p.PeriodKind,p.ShiftId,p.ShiftStartsAtUtc,p.ShiftEndsAtUtc,
 p.ProductionBusinessDate,p.MetricKey,p.DefinitionVersion,p.Status,p.MetricValue,p.Unit,p.ReasonCode
FROM legacy_reporting.MetricProjection p
JOIN legacy_reporting.Machine m ON m.SourceDatabase=p.SourceDatabase AND m.SourceMachineId=p.SourceMachineId
WHERE p.SourceDatabase=N'$SourceDatabase'
ORDER BY m.DeviceKey,p.ShiftStartsAtUtc,p.MetricKey;
"@
}
foreach($name in $queries.Keys) {
 $data=@(Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $TargetDatabase -Query $queries[$name] -TrustServerCertificate -AbortOnError -QueryTimeout 120)
 $file=Join-Path $OutputDirectory "$name.csv"
 $data | Export-Csv -Path $file -NoTypeInformation -Encoding UTF8
 Write-Host "$name : $($data.Count) rows -> $file"
}
Write-Host 'Read-only historical reporting preview completed. No database changes made.'
