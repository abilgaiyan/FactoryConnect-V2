# Local Windows development — real V2 API and dashboard

This guide uses a **dedicated V2 development database**. Do not target the restored V1 incident/qualification databases. The installer is never run automatically by these startup commands.

## Prerequisites

.NET 10, Node.js 24+, npm, a trusted localhost ASP.NET Core development certificate, and an already installed V2 SQL database. Confirm the exact SQL instance and installation GUID from `platform.InstallationIdentity`. The database must have the current migration ledger; initialize/upgrade only with the official SQL installer and a backup.

```powershell
dotnet dev-certs https --check --trust
dotnet build FactoryConnect.slnx
npm ci
```

## Terminal 1 — API

Set the connection string and installation ID **in the API process terminal only**. Replace the placeholders; do not commit secrets or local identifiers.

```powershell
$env:ConnectionStrings__Evidence = 'Server=.\FCINVENTORY;Database=FactoryConnect_V2_Dev;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:Evidence__InstallationId = '<retained-V2-installation-guid>'
dotnet run --project backend/FactoryConnect.Api -- --urls 'https://localhost:5081;http://127.0.0.1:5080'
```

The HTTP listener is for local liveness only. Protected endpoints require HTTPS, valid session authority and machine-scoped grants. API liveness is not database readiness.

```powershell
Invoke-RestMethod https://localhost:5081/health/live
```

## Terminal 2 — Dashboard (real backend)

```powershell
.\scripts\dev\Start-Dashboard.ps1
```

The launcher sets `NODE_OPTIONS=--use-system-ca` so Node/Vite uses the trusted Windows development certificate. It does not set `secure:false` or disable TLS verification. The Vite server proxies `/dashboard/config`, `/api` and `/health` to `https://localhost:5081`. An alternative local HTTPS origin may be passed as `-ApiUrl`; remote origins and URL credentials are refused.

```powershell
Invoke-RestMethod http://localhost:3000/health/live
Invoke-RestMethod http://localhost:3000/dashboard/config
```

If the database has no commissioned machines, `sources: []` and an empty dashboard are **expected**. They are not proof of acquisition or reporting. Do not populate the V2 database by copying V1 tables.

## Explicit fixture mode

```powershell
.\scripts\dev\Start-Dashboard.ps1 -Mode Fixtures
```

Fixture mode uses the existing mock middleware and does **not** proxy to SQL-backed APIs. It is not a production-data demo. Stop the Vite process before switching modes.

## TLS troubleshooting

If Vite reports `self-signed certificate`, confirm the .NET development certificate is trusted, restart using the launcher, and check `node --use-system-ca -e "fetch('https://localhost:5081/health/live').then(r=>console.log(r.status)).catch(e=>{console.error(e);process.exitCode=1})"`. Never use `NODE_TLS_REJECT_UNAUTHORIZED=0` or `secure:false` to hide a certificate failure.

## Commissioning boundary

A functioning API/proxy and empty dashboard do not create sites, machines, bootstrap custodians, runtime credentials, package approval or activation. Follow the separate [local commissioning guide](../../docs/implementation/FC2-local-end-to-end-commissioning.md) after these authorities are explicitly provisioned.
