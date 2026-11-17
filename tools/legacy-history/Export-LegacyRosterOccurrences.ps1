param(
  [string]$ServerInstance = '.\FCINVENTORY',
  [string]$SourceDatabase = 'FactoryConnect_P0_20261105',
  [datetime]$FromDate = [datetime]'2026-10-01',
  [datetime]$ThroughDate = [datetime]'2026-11-30',
  [string]$OutputDirectory = (Join-Path $PWD 'artifacts\legacy-roster-reconciliation')
)
$ErrorActionPreference = 'Stop'
Import-Module SqlServer -ErrorAction Stop
if ($SourceDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw 'Invalid source database name.' }
if ($ThroughDate.Date -lt $FromDate.Date -or ($ThroughDate.Date - $FromDate.Date).TotalDays -gt 366) {
  throw 'Choose a valid inclusive date range no longer than 366 days.'
}
# Export original V1 source boundaries, never rewrite to current V2 shift definitions.
# Include all roster revisions to reveal the history of schedule corrections.
$query = @'
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT r.MachineId, r.ProductionBusinessDate, r.ProductionDaySiteId,
       r.ProductionLineId, r.Revision, o.ShiftScheduleAssignmentId, o.ShiftId,
       o.ShiftStartsAtUtc, o.ShiftEndsAtUtc,
       CONVERT(varchar(19),o.ShiftStartsAtUtc AT TIME ZONE 'India Standard Time',120) AS StartsAtIst,
       CONVERT(varchar(19),o.ShiftEndsAtUtc AT TIME ZONE 'India Standard Time',120) AS EndsAtIst,
       DATEDIFF_BIG(SECOND,o.ShiftStartsAtUtc,o.ShiftEndsAtUtc) / 60.0 AS WindowMinutes,
       CASE WHEN r.Revision = MAX(r.Revision) OVER (
         PARTITION BY r.MachineId,r.ProductionDaySiteId,r.ProductionBusinessDate,r.ProductionLineId
       ) THEN 1 ELSE 0 END AS IsLatestRosterRevision
FROM dbo.MachineShiftOccurrenceRoster r
JOIN dbo.MachineShiftOccurrenceRosterOccurrence o
  ON o.MachineShiftOccurrenceRosterRowId = r.MachineShiftOccurrenceRosterRowId
WHERE r.ProductionBusinessDate BETWEEN @FromDate AND @ThroughDate
ORDER BY r.MachineId,r.ProductionBusinessDate,r.Revision,o.ShiftStartsAtUtc;
'@
# Parameterized query protects dates and prevents SQL injection.
$parameters = @(
  [System.Data.SqlClient.SqlParameter]::new('@FromDate',[System.Data.SqlDbType]::Date),
  [System.Data.SqlClient.SqlParameter]::new('@ThroughDate',[System.Data.SqlDbType]::Date)
)
$parameters[0].Value = $FromDate.Date
$parameters[1].Value = $ThroughDate.Date
# Invoke-Sqlcmd's variable substitution is not SQL parameter binding. Use SqlClient.
$connection = [System.Data.SqlClient.SqlConnection]::new(
  "Server=$ServerInstance;Database=$SourceDatabase;Integrated Security=True;TrustServerCertificate=True;Application Name=FC2-159 ReadOnlyRosterExport")
$connection.Open()
try {
  $command = $connection.CreateCommand()
  $command.CommandText = $query
  $command.CommandTimeout = 120
  foreach ($parameter in $parameters) { [void]$command.Parameters.Add($parameter) }
  $table = [System.Data.DataTable]::new()
  $reader = $command.ExecuteReader()
  try { $table.Load($reader) } finally { $reader.Dispose() }
} finally { $connection.Dispose() }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$rows = @($table.Rows | ForEach-Object {
  [pscustomobject]@{
    MachineId = $_['MachineId']; ProductionBusinessDate = $_['ProductionBusinessDate']
    ProductionDaySiteId = $_['ProductionDaySiteId']; ProductionLineId = $_['ProductionLineId']
    Revision = $_['Revision']; IsLatestRosterRevision = $_['IsLatestRosterRevision']
    ShiftScheduleAssignmentId = $_['ShiftScheduleAssignmentId']; ShiftId = $_['ShiftId']
    ShiftStartsAtUtc = $_['ShiftStartsAtUtc']; ShiftEndsAtUtc = $_['ShiftEndsAtUtc']
    StartsAtIst = $_['StartsAtIst']; EndsAtIst = $_['EndsAtIst']; WindowMinutes = $_['WindowMinutes']
  }
})
$path = Join-Path $OutputDirectory 'v1-machine-shift-roster-occurrences.csv'
$rows | Export-Csv $path -NoTypeInformation -Encoding UTF8
Write-Host "Exported $($rows.Count) original V1 roster occurrences (all revisions) to $path"
Write-Host 'Source database was read-only. Current V2 shift and break definitions were not changed.'
Write-Host 'No assumed break durations or qualified OEE values have been calculated.'
