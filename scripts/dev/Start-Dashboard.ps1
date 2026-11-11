# Run from any directory. Starts the Vite dashboard with the local HTTPS API.
# The API must already be listening on https://localhost:5081.
[CmdletBinding()]
param(
    [ValidateSet('Real', 'Fixtures')]
    [string]$Mode = 'Real',
    [string]$ApiUrl = 'https://localhost:5081'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    if ($Mode -eq 'Fixtures') {
        $env:VITE_ENABLE_FIXTURES = 'true'
        Remove-Item Env:FC2_DEV_API_URL -ErrorAction SilentlyContinue
    } else {
        $uri = [Uri]$ApiUrl
        if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('localhost','127.0.0.1','::1') -or
            $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
            throw 'API URL must be a loopback HTTPS origin'
        }
        $env:VITE_ENABLE_FIXTURES = 'false'
        $env:FC2_DEV_API_URL = $ApiUrl
        $env:NODE_OPTIONS = (($env:NODE_OPTIONS, '--use-system-ca' | Where-Object { $_ }) -join ' ').Trim()
    }
    npm run dev
    if ($LASTEXITCODE -ne 0) { throw "Vite exited with code $LASTEXITCODE" }
}
finally { Pop-Location }
