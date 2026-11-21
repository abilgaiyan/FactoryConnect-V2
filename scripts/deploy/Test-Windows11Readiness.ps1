# FactoryConnect V2: read-only preflight for side-by-side Windows 11 deployment.
# Run from an elevated or ordinary PowerShell session on the target host.
# Does not install software, stop services, modify firewall rules or write SQL.
[CmdletBinding()]
param(
    [int[]]$ProposedPorts = @(5080, 5081, 3000, 8443),
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$report = [ordered]@{
    CapturedAt = (Get-Date).ToUniversalTime().ToString('o')
    Computer = $env:COMPUTERNAME
    OperatingSystem = $null
    DotNetSdks = @()
    DotNetRuntimes = @()
    SqlServices = @()
    ExistingFactoryServices = @()
    PortListeners = @()
    ProposedPorts = $ProposedPorts
}
$os = Get-CimInstance Win32_OperatingSystem
$report.OperatingSystem = [ordered]@{
    Caption = $os.Caption
    Version = $os.Version
    BuildNumber = $os.BuildNumber
    Architecture = $os.OSArchitecture
}
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $report.DotNetSdks = @(dotnet --list-sdks 2>&1 | Out-String)
    $report.DotNetRuntimes = @(dotnet --list-runtimes 2>&1 | Out-String)
}
$services = @(Get-CimInstance Win32_Service)
$report.SqlServices = @($services | Where-Object { $_.Name -match '^MSSQL|^SQLAgent|^SQLBrowser' } | Select-Object Name, State, StartMode, PathName)
$report.ExistingFactoryServices = @($services | Where-Object { $_.Name -match 'FactoryConnect|MTConnect' -or $_.DisplayName -match 'FactoryConnect|MTConnect' } | Select-Object Name, DisplayName, State, StartMode, PathName)
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)
$report.PortListeners = @($listeners | Where-Object { $ProposedPorts -contains $_.LocalPort } | Select-Object LocalAddress, LocalPort, OwningProcess)
$report | ConvertTo-Json -Depth 6 | Tee-Object -Variable json
if ($ReportPath) {
    $json | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "Read-only preflight report saved: $ReportPath"
}
Write-Host "Preflight only: no installation or production changes were performed."
