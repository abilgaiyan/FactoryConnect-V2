# FC2-002 — Dashboard dependency review and backend shell

## Baselines

Dashboard reviewed at `0c3dbcb7f033dc75fd69036a4a42c4636574cfa2`.
Reference project files reviewed at `088bf52492546a2a79029cf366a22f7a4d7e8d44`.
PR #1 remains draft; this implementation is a separate, stacked change.

## Actual dashboard boundaries

| Method and path | Consumer and contract |
| --- | --- |
| GET `/dashboard/config` | `src/application/runtime-configuration.ts`; same-origin reporting base `/`, positive timeout, machine sources and optional capability profiles |
| POST `/api/reporting/v1/operational-metrics/shifts/query` | `src/api/reporting/reporting-client.ts`; generated request/page contract and runtime decoder |
| POST `/api/reporting/v1/operational-metrics/production-days/query` | Same reporting client; production-day metrics |
| POST `/api/reporting/v1/operational-metrics/production-day-shifts/query` | Same client; coverage conflict handling plus shift-page decoder |
| GET `/api/machines/v1/{machineId}/current-state` | `src/api/current-state/current-state-client.ts`; evidence/no-evidence, coverage, freshness and usability; explicit 400/503 Problem Details |
| POST `/api/machines/v1/{machineId}/activity/query` | `src/application/machine-activity-report.ts`; interval validation, source positions and duration conservation |
| POST `/api/machines/v1/{machineId}/counter/query` | `src/application/machine-counter-report.ts`; selected counter evidence and validation |
| GET `/api/commissioning/v1/snapshot` | `src/presentation/CommissioningPage.tsx`; read-only commissioning snapshot |

`src/api/generated/reporting-contract.ts` covers reporting and current state, not every UI request. The activity, counter and commissioning contracts need separate conformance fixtures when their providers are implemented. Supervisor Desk is currently a preview; no durable supervisor write API is established by this inventory.

`vite.config.ts` currently installs `src/mock-api.ts` middleware in both dev and preview. Those screens and mounted tests demonstrate fixture behavior, not integration with .NET. This slice preserves that behavior. Live integration will need an explicit same-origin proxy and a mock-disabled mode, with tests proving real requests reach .NET.

## Minimum project decision

The host requires **one new project**: `backend/FactoryConnect.Api`. It depends only on the ASP.NET shared framework. No original .NET source is imported in this slice, so there are no imported-module provenance or behavioral-change claims.

Candidate source projects, inspected through their `.csproj` references:

| Reference project | Project dependencies | Disposition |
| --- | --- | --- |
| `src/FactoryConnect.Abstractions` | None | Select exact identifiers and contracts alongside their first real consumer and tests |
| `src/FactoryConnect.Core` | Abstractions | Select domain/evaluator modules after file-level dependency and test review; no wholesale copy |
| `src/FactoryConnect.Protocols.MTConnect` | Abstractions | Acquisition slice candidate; bring relevant protocol fixtures/tests |
| `src/FactoryConnect.Protocols.Modbus` | Abstractions | Acquisition slice candidate; bring relevant protocol fixtures/tests |

These project-level references do not establish that every file is appropriate for V2. Minimum candidate closure for either protocol project is that protocol plus Abstractions; Core separately requires Abstractions. Original API, Edge, Infrastructure, Persistence, Persistence.SqlServer and Migrations are excluded from this shell. Their runtime composition and SQL/reporting paths must not enter through transitive copying.

## Implemented behavior and validation

- `/health/live` returns 200 when the host is alive.
- `/health` and `/health/ready` return 503 Problem Details because no evidence authority exists.
- `/dashboard/config` returns an empty machine roster, with no production identity/configuration copied.
- GET/POST `/api/*` return 503 Problem Details. This is an explicit unavailable shell, not an implementation of the successful dashboard API contracts.
- No persistence provider, connection string, migration runner, hosted worker or production seed exists.
- Combined CI builds the React app, runs its existing tests/typecheck, builds .NET 10 and runs an HTTP smoke check against the real host.
- The existing workflow used `npm ci` despite having no committed lockfile. This slice adds a generated lockfile and retains `npm ci` and npm caching.

Local validation: React typecheck PASS, 463 tests PASS (0 failed/skipped), production build PASS; Python smoke script syntax and git whitespace checked. .NET SDK is unavailable in the implementation environment. Backend compilation and real-host HTTP smoke acceptance require CI; no backend PASS is claimed.

## Next slice

Freeze fresh-database identity and configuration authority before SQL implementation. Then add one reviewed domain/protocol module at a time, recording exact source files, baseline, deliberate changes and relevant tests. Implement bounded reporting providers and contract acceptance independently from legacy report readers. No schema, production deployment or cutover is authorized by this shell.
