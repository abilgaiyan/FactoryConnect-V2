param(
  [Parameter(Mandatory = $true)][string]$RosterCsv,
  [string]$OutputDirectory = (Join-Path $PWD 'artifacts\legacy-roster-reconciliation')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $RosterCsv -PathType Leaf)) { throw "Roster CSV not found: $RosterCsv" }
# Approved V2 factory shift clock times. This script compares original V1
# roster evidence only; it does not replace timestamps or infer break windows.
$approved = @{
  'SHIFT-1' = @{ Start = '07:30'; End = '16:00'; Minutes = 510 }
  'SHIFT-2' = @{ Start = '16:00'; End = '00:00'; Minutes = 480 }
  'SHIFT-3' = @{ Start = '00:00'; End = '07:30'; Minutes = 450 }
}
$source = @(Import-Csv -LiteralPath $RosterCsv)
if ($source.Count -eq 0) { throw 'Roster CSV contains no data.' }
$required = @('MachineId','ProductionBusinessDate','Revision','IsLatestRosterRevision',
  'ShiftId','ShiftStartsAtUtc','ShiftEndsAtUtc','StartsAtIst','EndsAtIst','WindowMinutes')
foreach ($column in $required) {
  if (-not $source[0].PSObject.Properties[$column]) { throw "Missing required source column: $column" }
}
$rows = foreach ($item in $source) {
  $expected = $approved[$item.ShiftId]
  $start = [datetime]::MinValue
  $end = [datetime]::MinValue
  $validStart = [datetime]::TryParseExact($item.StartsAtIst, 'yyyy-MM-dd HH:mm:ss',
    [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::None, [ref]$start)
  $validEnd = [datetime]::TryParseExact($item.EndsAtIst, 'yyyy-MM-dd HH:mm:ss',
    [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::None, [ref]$end)
  $validMinutes = 0.0
  $minutesOk = [double]::TryParse($item.WindowMinutes,
    [System.Globalization.NumberStyles]::Float,
    [System.Globalization.CultureInfo]::InvariantCulture, [ref]$validMinutes)
  $classification = 'Unverifiable'
  if ($expected -and $validStart -and $validEnd -and $minutesOk) {
    $matches = $start.ToString('HH:mm') -eq $expected.Start -and
      $end.ToString('HH:mm') -eq $expected.End -and
      [math]::Abs($validMinutes - $expected.Minutes) -lt 0.01
    $classification = if ($matches) { 'Matches approved V2 timing' } else { 'Historical timing differs' }
  }
  [pscustomobject]@{
    MachineId = $item.MachineId
    ProductionBusinessDate = $item.ProductionBusinessDate
    Revision = $item.Revision
    IsLatestRosterRevision = $item.IsLatestRosterRevision
    ShiftId = $item.ShiftId
    OriginalStartsAtUtc = $item.ShiftStartsAtUtc
    OriginalEndsAtUtc = $item.ShiftEndsAtUtc
    OriginalStartsAtIst = $item.StartsAtIst
    OriginalEndsAtIst = $item.EndsAtIst
    OriginalWindowMinutes = $item.WindowMinutes
    ApprovedStartClockIst = if ($expected) { $expected.Start } else { '' }
    ApprovedEndClockIst = if ($expected) { $expected.End } else { '' }
    ApprovedWindowMinutes = if ($expected) { $expected.Minutes } else { '' }
    Reconciliation = $classification
    BreakExclusion = 'Not assessed: historical break evidence required'
    OeeQualification = 'Not established'
  }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$path = Join-Path $OutputDirectory 'v1-v2-shift-timing-reconciliation.csv'
$rows | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
$rows | Group-Object MachineId,Reconciliation | Sort-Object Name |
  Select-Object Name,Count | Format-Table -AutoSize
Write-Host "Compared $($rows.Count) source roster occurrences. CSV: $path"
Write-Host 'No source timestamps were rewritten. No break deductions, OEE calculation, or database changes.'
