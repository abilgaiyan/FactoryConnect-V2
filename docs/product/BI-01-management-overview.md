# BI-01 — Management Overview

27 October 2026. Implementation for review; production deployment and merge are not authorized.
Baseline: `ba50e158e7da881b0f37d1169ad30051cf0d3b49` (refreshed from `origin/main`).
Scope: BI-01 only, following the conditionally frozen S0 v1.0 and S2 plan.

## Delivered behavior

`/overview` extends the existing dashboard. It has explicit site, line, machine and production-day selection encoded in the URL. No default production calendar/day is inferred. Existing `/` and report routes remain available.

Population is distinct machine identity, independent of reporting-source count. Operational state, request outcome and freshness groups each reconcile independently. Only successful, complete, current and usable server evidence counts as a usable operational state. Stale responses retain their reported state/freshness/read-as-of explanation, but count as no usable current state. Failed refreshes never display an OFF state. The previous response may be retained and explicitly excluded from current counts. Configuration conflicts also withhold operational classification.

Selected-day Availability and Utilization reuse `loadRange` and the existing reporting client, exact identity checks, source revision checks and decimal formatting. Multiple reporting processors remain separately labeled; they are not averaged or silently selected. All returned definition versions, statuses, reasons and revision positions remain visible. Calculated zero is rendered as zero; missing, unavailable or insufficient results have no numeric substitute. Fleet ratios, coverage percentages, quantity and OEE calculations are not introduced.

Machine and historical-report links preserve Overview selection. The supporting range report initializes the exact machine and day and retains its existing explicit Load range action. Machine status/activity/counter tabs preserve the Overview return context. Search-only navigation and browser Back restore selection. Manual refresh invalidates older generations; unmount and filter changes abort pending work. Current-state and reporting loads each have at most four active machine requests. This is burst control, not a 100-machine capacity claim. The combined selected-day reporting selection retains the 1,000 source/day bound.

## Configuration projection decision

`Dashboard:MachineProfiles` is optional and read-only. `/dashboard/config` adds `machineProfileSchemaVersion: 1` and `machineProfiles`, leaving reporting sources unchanged. Legacy missing metadata means not assessed, not unsupported or qualified. The browser rejects unknown profile schema versions and malformed or duplicate bindings.

Each profile contains:

| Field | Meaning |
|---|---|
| MachineId | Existing registered dashboard machine GUID; never created by this adapter |
| ProfileId / ProfileVersion | Explicit profile identity and reviewed version |
| IntegrationCategoryId | Extensible identifier; no category-based UI branching |
| ApprovalReference / EffectiveFrom | Reviewed snapshot provenance; not a new approval workflow or historical profile resolver |
| Capabilities | Unique key with support (`supported`, `unsupported`, `not-assessed`) and qualification (`qualified`, `pending`, `rejected`, `not-assessed`) |
| StoppedLabel | Optional approved `Machine OFF` presentation label |
| FaultLabel | Optional approved `Alarm` presentation label |

Qualified capability metadata requires supported capability metadata. Approved state labels require qualified `operational-state` metadata. Unknown capability keys remain explanatory metadata and do not activate UI operations. Runtime metric availability always comes from reporting evidence. No capability declares quantity qualified by implication. Physical alarm/outage acceptance is separate from engineer-confirmed signal interpretation.

Startup validation rejects duplicate profiles, unregistered bindings, duplicate/invalid capability metadata, missing profile provenance, and conflicting site/line/presentation metadata for one machine across processors. The endpoint projects explicit allowlisted DTOs, never connection strings, upstream addresses or raw configuration. Do not place credentials in presentation metadata.

The host does not ship production profiles or invent approval dates. Commissioned configuration must supply reviewed snapshots through the existing configuration boundary. Machine OFF is a factory signal label, not proof of electrical isolation. It is applied only to usable stopped evidence; no failure or stale response is converted to OFF.

## Registered-machine verification and acceptance gates

| Item | Evidence / remaining work |
|---|---|
| GG-003 identity | Repository records GUID `677b030b-1ede-4a67-82ae-6a494791357f` in `docs/features/P1.6-IMP-1C-GG003-historical-inventory-specification.md` |
| MOXA source contract | `docs/moxa-status-normalization.md` records `DEWAS-SR-MOXA-001`, stream `mtconnect:DEWAS-SR-MOXA-001`; the commissioned machine GUID is not stored there |
| Current production dashboard bindings | Not available in the reviewed repository or supplied S0/S2 documents. Verify sanitized `Dashboard:Sources` for both machines: GUID, processor, site and line. Do not infer bindings from device labels or synthetic test IDs |
| Profile snapshots | Need reviewed profile identity/version, provenance/effective date and support/qualification entries for each actual machine before factory acceptance |
| Software fixtures | Synthetic CNC-like and MOXA-like profiles exercise common UI behavior. These are not evidence of production identity or commissioning |
| Physical acceptance | MOXA alarm, invalid/all-OFF and communication interruption/recovery remain separate witnessed checks |
| Business/IT gates | G-01–G-09 remain open within their S0 blocking scopes; coverage denominator and authentication provider are not decided here |

The missing production binding/profile evidence prevents declaring the two-machine entry gate or factory acceptance complete. The branch is reviewable implementation against configurable contracts. No server access, production configuration edit, SQL migration, acquisition change, business write endpoint, deployment or merge was performed.

## Validation and acceptance traceability

Build the dashboard host/test project and frontend before testing; typecheck the frontend. Run the full dashboard Node suite and Dashboard .NET suite. Run reporting contract conformance with the .NET SDK on PATH. Results are recorded in the draft PR.

- AC-01: distinct population, multiple reporting sources, profile differences and conflict rejection.
- AC-04: bounded refresh, old response rejection, filter/search navigation, unmount cancellation and Back restoration.
- AC-05: calculated zero, unavailable reasons, no-result presentation and isolated report failure.
- AC-06: exact machine/day historical links and preserved Overview return context.
- Related AC-03/13 regression protection: stale/failed reads cannot become usable OFF, and existing source/day bounds remain enforced.

No new reporting formula, SQL integrity/transaction behavior or acquisition path was changed. Real-factory validation, physical checks and progressive capacity testing remain open. BI-02 through BI-04 require their own authorization.

### Local validation record

- Frontend production build and TypeScript checks: PASS.
- Full dashboard client suite: **429/429 PASS**, including new model and mounted Overview tests.
- Dashboard host/test project build: PASS, zero warnings/errors.
- Dashboard .NET suite: **99/99 PASS**, zero skipped.
- Reporting contract determinism/type/synchronization check: PASS; generated contract has no diff. Local API regeneration used serial MSBuild because the local SDK's parallel restore exited without diagnostics.
- `git diff --check`: PASS.

Environment: .NET SDK 10.0.100; Node 24.22.1 (repository CI selects its pinned Node version). These are software checks with synthetic fixtures, not real-factory acceptance. CI is assessed separately after PR creation.
