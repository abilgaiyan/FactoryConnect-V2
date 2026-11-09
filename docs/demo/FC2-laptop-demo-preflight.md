# FC2 — Isolated laptop demo preflight (draft)

Purpose: prepare a management demonstration without modifying V1 production or the reserved FactoryConnect_V2_Dev database. This is a **preflight**, not proof that the dashboard is wired to a live V2 backend.

## Approved boundaries

- Use a separate disposable database, proposed name `FactoryConnect_V2_Demo`, only after confirming the target SQL instance and backup/ownership. Do not create, drop, restore or migrate any database as part of this preflight.
- Never connect to the Gajra production SQL instance, MTConnect devices, or live Edge acquisition for this demonstration.
- The repository README describes Vite mock middleware and warns that the dashboard is not connected to the backend by default. Treat mock screens as previews and label them accordingly.
- No default administrator or automatic security bootstrap exists. Do not bypass authentication or seed a production-like account.
- PR #41 remains draft; its outstanding 101-witness writer-bound, authenticated v4 HTTP tamper and 100-operation performance gates remain separate.

## Windows PowerShell inventory (read-only)

Run from the laptop. Paste output for review before any database or application setup.

```powershell
$PSVersionTable.PSVersion.ToString()
Get-Location
git --version
git status --short --branch
git remote -v
dotnet --info
node --version
npm --version
docker --version
docker compose version
Get-Service -Name 'MSSQL*','SQLBrowser' -ErrorAction SilentlyContinue |
  Select-Object Name,Status,DisplayName
Get-Command sqlcmd -ErrorAction SilentlyContinue | Select-Object Source
```

If Docker is absent, record that fact; SQL Server Developer on an existing local instance may be an alternative **after** explicit instance verification. Do not install software or change services during inventory.

## Proposed acceptance sequence (not yet executed)

1. Verify repository HEAD and CI; use an isolated worktree/checkout at the approved demo revision.
2. Inspect the exact local SQL instance, database list and migration state using read-only queries. Confirm `FactoryConnect_V2_Demo` does not conflict with any existing database.
3. Agree on a reproducible, non-production fixture and security bootstrap flow before writing demo data.
4. Start the API with verified environment configuration and check liveness/readiness separately; never represent liveness as evidence availability.
5. Start the Vite dashboard and explicitly identify mock/preview pages versus SQL-backed authenticated pages.
6. Rehearse the operator workflow and record screenshots, HTTP status, SQL target, and any unsupported features.

## Demo narration rules

- Seeded activity is **simulated**, not a live machine state or verified Gajra production record.
- OEE, Performance and Quality must not be presented as calculated without sufficient accepted production evidence.
- A successful dashboard preview is not evidence of production deployment readiness.
