# Creates an ignored, simulator-only acquisition-host configuration. Does not provision certificates,
# activate a machine, poll MTConnect or write to SQL. Never use on the factory server.
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$TrustAnchorPath,
  [Parameter(Mandatory=$true)][string]$AllowedCrlUri,
  [Parameter(Mandatory=$true)][string]$ServerCertificatePath,
  [Parameter(Mandatory=$true)][string]$ServerKeyPath,
  [Parameter(Mandatory=$true)][string]$RuntimeCertificatePath,
  [Parameter(Mandatory=$true)][string]$RuntimeKeyPath,
  [Parameter(Mandatory=$true)][string]$StreamKey,
  [string]$OutputPath='scripts/dev/acquisition.local.json'
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$settings=Get-Content (Join-Path $PSScriptRoot 'local.settings.json') -Raw | ConvertFrom-Json
if ($settings.sqlServer -ne '.\FCINVENTORY' -or $settings.database -ne 'FactoryConnect_V2_Dev' -or
    $settings.installationId -ne 'AAE0C57A-BEA6-427F-8046-4E22DDFD63E9') {
  throw 'Only the isolated FactoryConnect V2 development installation is supported.'
}
foreach ($name in @('installationId','siteId','simulatedMachineId')) {
  $id=[guid]::Empty
  if (-not [guid]::TryParse([string]$settings.$name,[ref]$id) -or $id -eq [guid]::Empty) {
    throw "Missing or invalid development identity: $name"
  }
}
if (-not $StreamKey.StartsWith('mtconnect:uuid:',[StringComparison]::Ordinal) -or $StreamKey.Length -gt 512) {
  throw 'Supply the commissioned mtconnect:uuid: stream key; do not guess it.'
}
$crl=$null
if (-not [uri]::TryCreate($AllowedCrlUri,[UriKind]::Absolute,[ref]$crl) -or
    $crl.Scheme -notin @('http','https')) { throw 'Explicit HTTP(S) CRL URI required.' }
$certPaths=@($TrustAnchorPath,$ServerCertificatePath,$ServerKeyPath,$RuntimeCertificatePath,$RuntimeKeyPath)
$resolved=@()
foreach ($path in $certPaths) {
  $file=(Resolve-Path -LiteralPath $path -ErrorAction Stop).Path
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Certificate/key path must be a file.' }
  if ((Get-Item -LiteralPath $file).Length -gt 32768) { throw 'Certificate/key file exceeds host limit.' }
  $resolved+= $file
}
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$dev=[IO.Path]::GetFullPath((Join-Path $root 'scripts/dev')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($dev,[StringComparison]::OrdinalIgnoreCase) -or
    -not $output.EndsWith('.local.json',[StringComparison]::OrdinalIgnoreCase)) {
  throw 'Output must be a *.local.json file under scripts/dev (ignored by Git).'
}
if (Test-Path -LiteralPath $output) { throw 'Configuration already exists; refusing to overwrite.' }
$configuration=[ordered]@{
  Version=1
  InstallationId=[string]$settings.installationId
  SiteId=[string]$settings.siteId
  MachineId=[string]$settings.simulatedMachineId
  StreamKey=$StreamKey
  AgentBaseUri='http://127.0.0.1:5055/device'
  BootstrapFromSequence='1'
  SqlConnectionString="Server=$($settings.sqlServer);Database=$($settings.database);Integrated Security=True;TrustServerCertificate=True;"
  TrustAnchorPath=$resolved[0]
  AllowedCrlUri=$AllowedCrlUri
  ServerCertificatePath=$resolved[1]
  ServerKeyPath=$resolved[2]
  RuntimeCertificatePath=$resolved[3]
  RuntimeKeyPath=$resolved[4]
  PollIntervalMilliseconds=250
  BatchCount=100
  OperationTimeoutSeconds=15
}
$json=ConvertTo-Json -InputObject $configuration -Depth 4
[IO.File]::WriteAllText($output,$json,[Text.UTF8Encoding]::new($false))
Write-Host 'Local acquisition configuration created. No acquisition started.'
Write-Host 'Run Start-Local.ps1 -Action AcquisitionCheck -AcquisitionConfigPath scripts/dev/acquisition.local.json'
