# FactoryConnect V2 local developer runtime helper. No security bootstrap or machine activation.
[CmdletBinding()]
param([ValidateSet('Check','Api','Simulator','Dashboard')][string]$Action='Check')
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$settings=Join-Path $PSScriptRoot 'local.settings.json'
if (-not (Test-Path $settings)) { throw "Create scripts/dev/local.settings.json from local.settings.example.json first." }
$c=Get-Content $settings -Raw | ConvertFrom-Json
if ($c.database -ne 'FactoryConnect_V2_Dev' -or $c.sqlServer -ne '.\FCINVENTORY') { throw 'This helper only supports the known isolated development database.' }
if ($c.installationId -ne 'AAE0C57A-BEA6-427F-8046-4E22DDFD63E9') { throw 'Unexpected development installation identity.' }
$edge=Join-Path $PSScriptRoot 'edge.simulator.local.json'
$issuer='https://localhost:8443/realms/factoryconnect-dev'
function Status($label,$ok) { Write-Host ("{0}: {1}" -f $label, $(if($ok){'OK'}else{'NOT READY'})) }
if ($Action -eq 'Check') {
  Status 'Simulator config' (Test-Path $edge)
  Status 'Identity secret in current shell' (-not [string]::IsNullOrWhiteSpace($env:Identity__ClientSecret))
  Status 'Keycloak containers' ([bool](docker ps --format '{{.Names}}' | Select-String -SimpleMatch 'dev-identity-identity-1'))
  foreach($entry in @(@('Simulator','http://127.0.0.1:5055/health'),@('API','https://localhost:5081/health/live'),@('Dashboard','http://localhost:3000/dashboard/config'))) {
    try { $r=Invoke-WebRequest -Uri $entry[1] -UseBasicParsing -TimeoutSec 3; Status $entry[0] ($r.StatusCode -eq 200) }
    catch { Status $entry[0] $false }
  }
  exit
}
Push-Location $root
try {
  switch($Action) {
    'Simulator' { python .\scripts\dev\mtconnect_simulator.py --port 5055 --observations 120 }
    'Dashboard' { & (Join-Path $PSScriptRoot 'Start-Dashboard.ps1') -Mode Real }
    'Api' {
      if (-not (Test-Path $edge)) { throw "Missing $edge" }
      if ([string]::IsNullOrWhiteSpace($env:Identity__ClientSecret)) {
        $secret=Read-Host 'Keycloak confidential client secret' -AsSecureString
        $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        try { $env:Identity__ClientSecret=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
      }
      $env:Identity__Authority=$issuer
      $env:Identity__ClientId='factoryconnect-api'
      $env:Evidence__InstallationId=$c.installationId
      $env:ConnectionStrings__Evidence="Server=$($c.sqlServer);Database=$($c.database);Integrated Security=True;TrustServerCertificate=True;"
      $env:MTConnect__EdgeConfigurationFile=(Resolve-Path $edge).Path
      if ($c.siteId) { $env:Evidence__SiteId=$c.siteId }
      dotnet run --project .\backend\FactoryConnect.Api -- --urls 'https://localhost:5081;http://localhost:5080'
    }
  }
} finally { Pop-Location }
