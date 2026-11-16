param(
  [string]$ServerInstance = '.\FCINVENTORY',
  [string]$SourceDatabase = 'FactoryConnect_P0_20261105',
  [string]$OutputDirectory = (Join-Path $PWD 'artifacts\legacy-break-inventory')
)
$ErrorActionPreference = 'Stop'
Import-Module SqlServer -ErrorAction Stop
if ($SourceDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw 'Invalid source database name.' }
# Discovery only: no DDL, DML, source configuration changes or V2 writes.
# Inspect column metadata before deciding how to reconstruct effective-dated
# shift assignments and machine-specific excluded breaks.
$query = @'
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT s.name AS SchemaName, t.name AS TableName, c.column_id AS ColumnOrdinal,
       c.name AS ColumnName, ty.name AS SqlType,
       c.max_length AS MaxLength, c.is_nullable AS IsNullable
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id=t.schema_id
JOIN sys.columns AS c ON c.object_id=t.object_id
JOIN sys.types AS ty ON ty.user_type_id=c.user_type_id
WHERE t.name LIKE '%shift%' OR t.name LIKE '%break%'
   OR t.name LIKE '%schedule%' OR t.name LIKE '%calendar%'
   OR t.name LIKE '%machine%config%'
ORDER BY s.name,t.name,c.column_id;
'@
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$columns = @(Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $SourceDatabase -Query $query -TrustServerCertificate -QueryTimeout 60 -AbortOnError)
$columns | Select-Object SchemaName,TableName,ColumnOrdinal,ColumnName,SqlType,MaxLength,IsNullable |
  Export-Csv (Join-Path $OutputDirectory 'v1-shift-break-schema.csv') -NoTypeInformation -Encoding UTF8
$tables = @($columns | Select-Object SchemaName,TableName -Unique)
Write-Host "Read-only schema discovery: $($tables.Count) candidate tables, $($columns.Count) columns."
Write-Host "CSV: $(Join-Path $OutputDirectory 'v1-shift-break-schema.csv')"
Write-Host 'No source rows, machine observations, or production data were exported. No database writes.'
