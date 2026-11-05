# DEP-01 — Universal Factory Deployment and Adapter Boundary

Status: proposed contract for review; documentation only. No production authorization or runtime/schema change.

## Purpose

One repeatable FactoryConnect deployment lifecycle supports heterogeneous machines at a factory: FANUC/MTConnect, existing Modbus TCP integrations, and future Siemens, Modbus RTU and vendor-specific adapters. A controller does not define the factory deployment topology. Do not install separate FactoryConnect stacks merely because machines use different controllers.

This roadmap extends, and does not replace, the frozen [FC-032.4 factory demo deployment contract](../features/FC-032.4-factory-demo-deployment-contract.md). The [factory-site commissioning runbook](factory-site-commissioning-runbook.md) remains the operator's existing Windows reference. Where a proposed feature conflicts with a frozen contract, require a separately reviewed change rather than silently overriding it.

## Invariants

1. **Stable machine identity:** physical MachineId remains stable across adapter, endpoint or controller changes; mapping and stream ownership are explicitly validated.
2. **Protocol boundary:** protocol-specific acquisition translates signals into canonical observations; downstream persistence, activity, aggregation, metrics and reporting do not depend on controller vendor.
3. **Evidence and continuity:** preserve source identity, event timestamps, quality/availability, ordering/cursors, mapping version and provenance sufficient for replay. Do not claim an adapter exposes evidence it cannot supply.
4. **Semantics remain downstream:** adapters must not manufacture production quantity, good quantity, downtime, cycle time, utilization or OEE.
5. **Independent fault domains:** one failing controller/adapter must not silently stop unrelated machine acquisition. Exact process isolation/supervision is an implementation question to verify in DEP-03.
6. **SQL authority:** one explicitly provisioned application database; migrations are the only DDL authority; Edge/API fail closed on incompatible schema. No production InMemory fallback.
7. **Immutable releases:** build from an exact source commit, verify release and manifest hashes, select the approved release, preserve external site configuration, record process ownership and launch/recovery evidence.
8. **Safe operations:** no unapproved interruption, reboot, scheduled-task registration, SQL repair, machine-identity rewrite or destructive rollback.
9. **Versioned mappings:** changing signal interpretation must not silently rewrite historical evidence or claim historical metrics remain equivalent.
10. **No fabricated metrics:** absent signals and unproven continuity remain explicitly unavailable.

## Deployment versus acquisition responsibilities

| Boundary | Owns | Does not own |
| --- | --- | --- |
| Release/deployment | artifact integrity, configuration validation, SQL readiness/migration operation, selected release, authorized startup/recovery, audit evidence | protocol interpretation, fabricated production data |
| Adapter/acquisition | controller connection, polling/subscription, source identity, raw signal capture, transport continuity and health | OEE, reporting policy, factory-wide schema migration |
| Canonical ingestion | stable machine/signal identity, normalized observation evidence, durable cursor/continuity | vendor-specific transport control |
| Processing/reporting | versioned production semantics, durable facts, aggregation, metric evaluation, API/dashboard | device connection configuration |

Existing Modbus TCP and MTConnect capabilities must be inventoried against these responsibilities before changing interfaces. Siemens OPC UA, Siemens S7 and Modbus RTU are *candidate integrations*, not validated delivered capabilities; controller model, license, security and available data items require site-specific verification.

## Configuration model to validate in DEP-02

The logical hierarchy is **factory/site → line → physical machine → controller capabilities → adapter instance/endpoint → versioned signal mapping → production policy**. This is a design vocabulary, not a new executable YAML/JSON schema. Map it onto existing `MTConnect:Machines`, production context, shift schedule, Dashboard source, Modbus and other current contracts before proposing schema changes.

Per-machine configuration records should identify stable MachineId, adapter kind/instance, endpoint, source device/key, expected capabilities, mapping version, secrets reference and commissioning evidence. Site configuration must remain outside immutable release artifacts. Do not hardcode customer names, IPs, GUIDs, ports or shifts in reusable scripts.

## Standard lifecycle and evidence gates

1. **Inventory:** approved host/site, controller models, physical MachineIds, network, protocol endpoints, SQL and credentials, shift/timezone and capability matrix.
2. **Preflight:** target identity, OS/runtime requirements, permissions, network reachability, existing processes/tasks, backups, release ZIP SHA-256 and manifest.
3. **Acquisition setup:** install only approved protocol components and configuration; verify each configured device identity, signal coverage, adapter health and continuity.
4. **Release deployment:** use the repository-owned deployment authority and immutable selected release; SQL migration remains explicit; follow frozen startup order and process-ownership checks.
5. **Functional verification:** controller → adapter/agent → Edge → advancing durable SQL observations → API → Dashboard. Check exact reporting processor identity and distinguish raw acquisition health from semantic metric correctness.
6. **Recovery verification:** verify individual component restart, no duplicate owners, preserved cursors/evidence and independently authorized BootSafe/system-start behavior.
7. **Handover:** publish release/manifest hash, effective sanitized configuration, machine-to-adapter matrix, tests/results, unresolved gaps and rollback/recovery decision.

Each gate is **fail closed**. A healthy HTTP response or a running process alone does not establish acquisition, persistence or report correctness. No unattended/reboot-safe claim until an approved end-to-end reboot test passes.

## Existing repository authorities (reuse first)

- `scripts/release/Build-FactoryConnectRelease.ps1` — exact release construction.
- `scripts/deployment/Deploy-FactoryConnect.ps1` — release deployment.
- `scripts/deployment/Start-FactoryConnectRuntime.ps1` — runtime startup, including BootSafe.
- `scripts/deployment/Start-FactoryConnectSystem.ps1` — acquisition then SQL readiness then BootSafe runtime.
- `scripts/deployment/Install-FactoryConnectSystemStartup.ps1` — publishes stable system-start entry; **does not register a Windows task**.
- `scripts/deployment/Restart-FactoryConnectEdge.ps1` and `Get-FactoryConnectEdgeRestartAssessment.ps1` — Edge restart and read-only assessment.
- **PR #127 (draft, separately reviewed):** `Restart-FactoryConnectDashboard.ps1` adds Dashboard-only restart, process ownership and health validation, shared intent/lock handling, and exact-source packaging. Windows lifecycle/executable CI and release-package checks passed for commit `9b453ac452efbc04659e6f69453a9579cb7dd07d`. This is **not merged or deployed**; final Gajra validation and controlled script installation remain separate.
- `config/templates/` — production configuration templates.
- `docs/deployment/factory-site-commissioning-runbook.md` — operator guidance.

These are existing authorities, not a claim that all mixed-controller paths are already production-tested.

## Follow-on roadmap and acceptance

| Workstream | Deliverable | Acceptance |
| --- | --- | --- |
| DEP-02 | Inventory current MTConnect/Modbus configuration and define factory/machine/adapter capability matrix | Stable machine identity and site-vs-release configuration boundaries verified; no competing schema |
| DEP-03 | Adapter lifecycle/health/isolation gap closure | Multiple adapters coexist; one failure does not corrupt or block unrelated evidence |
| DEP-04 | Reusable exact-release install/upgrade/rollback checks | Verified manifest, selected release, SQL readiness and ownership; no unsupported rollback |
| DEP-05A | Dashboard-only restart, tracked by [PR #127](https://github.com/abilgaiyan/FactoryConnect/pull/127) | Windows CI/package checks pass; review, merge, controlled site deployment and preservation evidence remain distinct gates |
| DEP-05B | Remaining component-specific restart and recovery, including API | Correct PID/path/start-time, locks, intents and no unrelated process termination |
| DEP-06 | Acquisition bootstrap and Windows startup registration | Explicit opt-in registration, BootSafe ordering, no duplicates, approved reboot test |
| DEP-07 | Automated commissioning evidence | Per-machine end-to-end observations advance; API/Dashboard and report identity verified |
| DEP-08 | FANUC/MTConnect reference acceptance | Repeatable clean-server and recovery run |
| DEP-09 | Modbus TCP validation and Modbus RTU extension if required | Real signal coverage and continuity proven; no invented quantity |
| DEP-10 | Siemens controller integration | Exact model/transport/security/license/capabilities confirmed and tested |
| DEP-11 | Mixed-controller factory acceptance | FANUC + Modbus + Siemens (where available) under one stack with independent failure/recovery |

## DEP-01 exit criteria

- One discoverable contract with links to frozen deployment authority and runbook.
- Clear controller-agnostic boundary, stable identity, evidence and configuration principles.
- Explicit inventory of existing authorities and **unimplemented/unverified** work.
- No production server mutation, SQL schema change, deployment script modification or claim of Siemens support.

Next action: DEP-02 repository-backed inventory and contract tests before introducing a new adapter or deployment configuration format.
