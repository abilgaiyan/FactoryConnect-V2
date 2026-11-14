# FactoryConnect V2 local developer runtime helper. No security bootstrap or machine activation.
[CmdletBinding()]
param([ValidateSet('Check','StartAll','Identity','Api','Simulator','Dashboard')][string]$Action='Check')
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$settings=Join-Path $PSScriptRoot 'local.settings.json'
if (-not (Test-Path $settings)) { throw "Create scripts/dev/local.settings.json from local.settings.example.json first." }
$c=Get-Content $settings -Raw | ConvertFrom-Json
if ($c.database -ne 'FactoryConnect_V2_Dev' -or $c.sqlServer -ne '.\FCINVENTORY') { throw 'This helper only supports the known isolated development database.' }
if ($c.installationId -ne 'AAE0C57A-BEA6-427F-8046-4E22DDFD63E9') { throw 'Unexpected development installation identity.' }
$edge=Join-Path $PSScriptRoot 'edge.simulator.local.json'
$issuer='https://localhost:8443/realms/factoryconnect-dev'
function DockerContainers {
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is not installed or not on PATH.' }
  $null = docker info --format '{{.ServerVersion}}' 2>$null
  if ($LASTEXITCODE -ne 0) { throw 'Docker engine is unavailable. Start Docker Desktop and retry.' }
  $names = @(docker ps -a --format '{{.Names}}')
  foreach ($name in @('dev-identity-identity-db-1','dev-identity-identity-1')) {
    if ($names -notcontains $name) { throw "Expected existing development identity container not found: $name. Check Docker Desktop; this helper does not create containers." }
  }
}
function IsReady($uri) {
  try { return (Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 }
  catch { return $false }
}
function StartLocalWindow($action) {
  $scriptPath = Join-Path $PSScriptRoot 'Start-Local.ps1'
  $command = "& '" + $scriptPath.Replace("'","''") + "' -Action " + $action
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
  Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NoExit','-EncodedCommand', $encoded) | Out-Null
  Write-Host "Started $action in a separate PowerShell window."
}
function Status($label,$ok) { Write-Host ("{0}: {1}" -f $label, $(if($ok){'OK'}else{'NOT READY'})) }
if ($Action -eq 'Check') {
  Status 'Simulator config' (Test-Path $edge)
  Status 'Identity secret available' (-not [string]::IsNullOrWhiteSpace($env:Identity__ClientSecret) -or -not [string]::IsNullOrWhiteSpace($c.keycloakClientSecret))
  try {
    DockerContainers
    $running = @(docker ps --format '{{.Names}}')
    Status 'PostgreSQL container' ($running -contains 'dev-identity-identity-db-1')
    Status 'Keycloak container' ($running -contains 'dev-identity-identity-1')
  } catch { Write-Warning $_.Exception.Message; Status 'Identity containers' $false }
  try {
    $metadata = Invoke-RestMethod -Uri "$issuer/.well-known/openid-configuration" -TimeoutSec 5
    Status 'Keycloak realm' ($metadata.issuer -eq $issuer)
  } catch { Status 'Keycloak realm' $false }
  foreach($entry in @(@('Simulator','http://127.0.0.1:5055/health'),@('API','https://localhost:5081/health/live'),@('Dashboard','http://localhost:3000/dashboard/config'))) {
    try { $r=Invoke-WebRequest -Uri $entry[1] -UseBasicParsing -TimeoutSec 3; Status $entry[0] ($r.StatusCode -eq 200) }
    catch { Status $entry[0] $false }
  }
  exit
}
Push-Location $root
try {
  switch($Action) {
    'StartAll' {
      DockerContainers
      $running = @(docker ps --format '{{.Names}}')
      foreach ($name in @('dev-identity-identity-db-1','dev-identity-identity-1')) {
        if ($running -notcontains $name) {
          docker start $name | Out-Null
          if ($LASTEXITCODE -ne 0) { throw "Failed to start $name" }
        }
      }
      if (-not (Test-Path $edge)) { throw "Missing $edge" }
      if ([string]::IsNullOrWhiteSpace($env:Identity__ClientSecret) -and
          [string]::IsNullOrWhiteSpace($c.keycloakClientSecret)) {
        throw 'Set keycloakClientSecret in ignored local.settings.json before StartAll; never put secrets in command arguments.'
      }
      if (-not (IsReady 'http://127.0.0.1:5055/health')) { StartLocalWindow 'Simulator' }
      else { Write-Host 'Simulator already responding.' }
      if (-not (IsReady 'https://localhost:5081/health/live')) { StartLocalWindow 'Api' }
      else { Write-Host 'API already responding.' }
      if (-not (IsReady 'http://localhost:3000/dashboard/config')) { StartLocalWindow 'Dashboard' }
      else { Write-Host 'Dashboard already responding.' }
      Write-Host 'Startup requested. Wait for services, then run -Action Check. Acquisition is not started.'
    }
    'Identity' {
      DockerContainers
      $running = @(docker ps --format '{{.Names}}')
      foreach ($name in @('dev-identity-identity-db-1','dev-identity-identity-1')) {
        if ($running -notcontains $name) {
          docker start $name | Out-Null
          if ($LASTEXITCODE -ne 0) { throw "Failed to start $name" }
        }
      }
      Write-Host 'Development PostgreSQL and Keycloak containers started. Use -Action Check to verify realm readiness.'
    }
    'Simulator' { python .\scripts\dev\mtconnect_simulator.py --port 5055 --observations 120 }
    'Dashboard' { & (Join-Path $PSScriptRoot 'Start-Dashboard.ps1') -Mode Real }
    'Api' {
      if (-not (Test-Path $edge)) { throw "Missing $edge" }
      if ([string]::IsNullOrWhiteSpace($env:Identity__ClientSecret) -and
          -not [string]::IsNullOrWhiteSpace($c.keycloakClientSecret)) {
        $env:Identity__ClientSecret = [string]$c.keycloakClientSecret
      }
      if ([string]::IsNullOrWhiteSpace($env:Identity__ClientSecret)) {
        $secret=Read-Host 'Keycloak confidential client secret' -AsSecureString
        $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        try { $env:Identity__ClientSecret=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
      }
      $env:ASPNETCORE_ENVIRONMENT='Development'
      $env:Identity__DevelopmentDashboardReturnUri='http://localhost:3000/operator-evidence'
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
