# FC-DEMO-01 — Supervisor Desk read-only reporting integration

Development demonstration candidate based on `3778bf2dc91bc1a57f942defa3b6493f68e98fbc`. No merge, deployment, factory connection, data-access provisioning, authentication, durable writes, migration or schedule activation is included.

## Workspace and source boundaries

Extend `/supervisor-preview` rather than introduce another Supervisor Desk. Both its development navigation and direct development entry now mount the same workspace. Real read-only is the initial mode. It loads the existing `/dashboard/config` runtime or receives the existing dashboard runtime. Configuration failure is explicit, with retry; it never switches to samples. Sample preview is an explicit separate mode retaining PR #167's browser-memory explanation, Production Details and Shift Targets workflows. Switching mode discards sample entries and clears the real selection; no sample author, thread, target or declaration is attached to a real event.

The entire workspace remains development-only, including real read-only integration. Production builds retain the existing non-enabled route notice and exclude the workspace/sample editors and fixtures. No configuration toggle enables production entry. Production reporting routes and their access boundaries remain unchanged.

## Existing contracts reused

- Select one configured machine/reporting processor and a production business date. Do not infer today's production day or choose a sample source when configuration is absent.
- Existing `queryAuthoritativeProductionDayShifts`, pagination, authority validation, page-state handling and projector request the five canonical metrics in the unpartitioned context. Coverage-required, invalid, unavailable, insufficient and conflicting states retain their existing behavior.
- Select an exact returned occurrence by machine, processor, site/line, assignment, shift ID and UTC start/end. Two occurrences sharing a shift label remain separate options. KPI rendering reuses `ShiftPerformanceOverviewView` with its identifier filters hidden for the already fixed scope; canonical values and conflicts are neither clamped nor recalculated.
- Query `loadActivityReport` using the occurrence's exact UTC bounds and AbortSignal. Do not call `activityDateRange`, which represents IST calendar days. Reuse the activity decoder and shared `MachineActivityEvidence` for timeline, totals, pagination, selected/original timestamps, state processor, stream, large decimal-string positions, instance, sequence, read time and unrepresented coverage.
- Reporting and activity are independent reads and revision domains. Matching machine/time scope is not an atomic common snapshot or proof of processor convergence. Reporting evidence read time is not returned by the current presentation contract and remains explicitly unavailable. Activity read time is displayed from its response. A request-received time is not substituted for source provenance.
- Activity positions/original interval bounds support inspection, not the complete durable explanation reference. No real-event explanation action is enabled; publication revision/output ordinal resolution and durable authorization remain separate technical gates. Open activity is not returned by this history query; unrepresented coverage is not assumed stopped time, an acquisition failure or an explainable open event.

Selection changes discard old reports and dispose their lifecycle controller. Occurrence changes unmount/abort the prior activity read; a late result cannot populate a successor selection. Retry clears failed activity evidence. No failure path substitutes sample data. A real activity failure does not rewrite successfully returned metrics, and separately shown metrics do not establish that the activity request succeeded.

## Isolated review

Use the repository-pinned Node version in `src/FactoryConnect.Dashboard/ClientApp`:

```sh
npm ci
npm run build
npm run typecheck
node --test test/supervisor-readonly-mounted.test.mjs test/machine-activity-mounted.test.mjs
npm test
npm run dev -- --host 127.0.0.1
```

Open `http://127.0.0.1:5173/supervisor-preview`. The existing Vite proxy forwards `/dashboard` and `/api` to the local Dashboard host at `http://localhost:5090`. Real read-only mode requires an independently approved local/isolated read-only data environment using the existing gateway and APIs. This packet creates no production connection, credentials or SQL permission boundary. If no approved backend is available, use explicit Sample preview or isolated intercepted test responses; never call synthetic responses verified factory data. Existing query APIs use POST for read requests; the HTTP verb does not mean a business mutation.

Validate at 1440px and 390px with exact overnight occurrences, evidence states, contained table scrolling and keyboard focus, mode switching and failed reads. Use synthetic test fixtures for CI/visual tests and retain their provenance. Verify the production bundle excludes workspace/sample-editor strings. Factory-data demonstration acceptance remains pending the approved data source and real endpoint checks.

## Validation scope

Build and typecheck precede targeted and full frontend tests. Mounted tests cover exact source requests and overnight UTC ranges, same-label distinct occurrences, canonical zero, missing/insufficient/unavailable metrics, above-100 conflict retention, wrong-scope reporting and activity rejection, cancelled/late requests across source/date/occurrence/mode, mode reset, configuration failure and empty configured sources. Existing activity pagination tests protect the extracted shared renderer.

No new reporting API/DTO, generated contract, metric formula, source authority or SQL behavior is added. GG-003 schedule activation, qualified quantity/OEE completion, production access control and durable supervisor workflows remain separate. Do not record this development demonstration as Phase-1 factory acceptance.
