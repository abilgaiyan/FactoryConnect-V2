# MOXA-001 audited pre-checkpoint recovery

## Operational acceptance — 28 October 2026

**Recovery complete; no rerun required.**

Operational MOXA recovery is accepted on the basis of the operator's supplied recovery report, as confirmed in the project conversation. This closes the operational recovery decision only. Historical metric semantics, sustained stabilization and full pilot acceptance remain separate.

| Evidence | Recorded status / provenance |
| --- | --- |
| Implementation | PR #165 merged; GitHub independently verified |
| Deployed runtime identity | `aa73c52104194423a30364498f17670abebea135` — deployment reported by operator; GitHub merge identity verified |
| Admission scope | MOXA activity processor row 2; boundary `2026-10-25T02:00:00Z` |
| Checkpoint convergence | **3,808** — operator-reported production result; exact capture time and processor-by-processor snapshot not supplied here |
| Quantity processor row 4 | Required unchanged by acceptance scope; attach the actual before/after result before marking evidence independently verified |
| Screenshot | Preserve original screenshot with capture time and artifact reference; attachment/reference not supplied in this record |
| Recovery operation / SQL audit | Preserve exact OperationId and audit export; identifiers/export not supplied in this record |
| Migration / backup | Preserve migration 023 ledger result, backup identity/path and available checksums; production artifacts not supplied in this record |

The production artifacts above have **not** been independently inspected for this documentation update. Missing references are evidence-archive follow-up, not grounds to rerun the recovery. Do not invent an OperationId or substitute CI artifacts for production evidence. Keep backups/configuration exports containing secrets in restricted storage; link redacted evidence or controlled references.

### Remaining stabilization acceptance

- [ ] Before/crossing/after-boundary MOXA interval evidence, including exclusion before `2026-10-25T02:00:00Z`.
- [ ] Representative GG-003 and MOXA production-day/shift reports, calculations and unavailable/insufficient-evidence reasons.
- [ ] Reporting latency, sustained checkpoint progression and restart continuity.
- [ ] GG-003 break correction tracked separately with approved effective dates.
- [ ] Complete the production evidence references above.

Open diagnostic PRs #151, #159 and #160 are not closed or superseded by operational recovery acceptance. Configuration approval, runtime activation and historical evidence recovery remain separate operations. No production action or rerun is authorized by this record.

## Historical preparation and recovery design

The sections below preserve the pre-execution design and safeguards. Their
preparation requirements are not a statement that the completed operational
recovery remains pending. They do not authorize a second execution. No clone
rehearsal or failure-reconciliation proof is inferred from the reported production
success. Code/deployed baseline remains `aa73c52104194423a30364498f17670abebea135`;
this acceptance update changes documentation only.

## Fixed scope and schema

Migration 023 adds ProductionAdmissionRecoveryAudit under the normal repository
migration ledger/schema descriptor. It does not change admission policy. Do not
apply ad hoc DDL or modify immutable releases. Deploy the matching runtime/schema
through the reviewed release process; an older runtime is not assumed compatible.
The dedicated tool fixes row 2, machine bfa0dc97-9a68-45ed-b189-1dacb7d863f0,
stream mtconnect:DEWAS-SR-MOXA-001, from bound/null to bound/2026-10-25T02:00Z.
Row 4, source observations/activity, schedules and all checkpoints are unchanged.
The normal runtime binding remains immutable.

The transaction takes the shared migration lock and verifies current schema,
locks the exact processor and missing checkpoint range, verifies all output
identity/payload hashes and typed JSON ownership, and follows eligibility source
references. Unsupported, malformed, duplicate-property or ambiguous outputs abort.
Metric stream/fact/aggregate/projection absence is protected with SERIALIZABLE
predicate locks. Trusted foreign keys connect contributions, revisions, projection
evidence and coverage to these parents. Immutable assessment ownership is decoded
with application codecs. Global output scans can block GG-003 reporting; bounded
SQL timeouts abort rather than weakening integrity.

Audit insertion and binding update share one transaction. An internal test seam
injects failure between them; no production CLI fault switch exists. A replay
fails closed; it never clears an audit, resets admission or advances a checkpoint.
After a lost commit acknowledgement, inspect the audit by operation ID before
any next action. Never automatically restore the old binding/configuration.

## Maintenance operation

Use the PR's matching Restart-FactoryConnectEdge.ps1 and helpers from a reviewed
operations bundle, not stale installed script copies. Inventory all installed
entry points and disable/repoint obsolete launchers before execution. Every
FactoryConnect deploy/start/restart path must use the deployment lock and honor
moxa-maintenance.intent.json. Independent Windows services must be disabled;
scheduled tasks/external supervisors/manual launchers must be suspended and
recorded in ExternalLaunchersSuspendedReference. The wrapper rejects detected
active service launchers. An arbitrary external launcher cannot be proven absent
by a SQL transaction or a filesystem lock. This commissioning prerequisite must
be reviewed against the actual server; the script does not claim otherwise.

The optional MoxaRecoveryPlan parameter on Edge restart selects the maintenance
path. The existing operation retains deployment.lock continuously from ownership
preflight through shutdown, SQL correction, configuration activation and restart.
A persistent marker is published before termination. It blocks cooperating
operations even if this wrapper crashes and releases its file handle. Failure
preserves the marker, runtime failure/intent evidence and original config backup.
No automatic retry, marker deletion, restart or adoption of unrecorded processes.
The wrapper rechecks global Edge absence before calling SQL. The tool executable
hash is verified; review the complete published artifact/manifest as well.

A plan JSON contains OperationId (new GUID), ExpectedServer, ExpectedDatabase,
ApprovalReference, ExternalLaunchersSuspendedReference, RecoveryExecutable (absolute
path to the reviewed self-contained EXE), RecoveryExecutableSha256 (uppercase).
Credentials come from the commissioned Edge configuration and are passed only in
a temporary process environment, not arguments or audit. Restrict permissions on
maintenance logs/config backups, which contain the original configuration.
The matching MOXA boundary is generated in memory and activated only after SQL
commit; all other configuration values are retained. The audit captures the
maintenance evidence, selected release, old Edge identity and original config hash.

No production invocation command is supplied until clone rehearsal and operational
review. For development, build first, then run:

```powershell
dotnet build FactoryConnect.sln -c Release
dotnet test tests/FactoryConnect.Integration.Tests -c Release --no-build --filter "FullyQualifiedName~MoxaAdmissionRecoveryIntegrationTests"
powershell -NoProfile -File tests/deployment/Invoke-MoxaMaintenanceConformance.ps1
```

SQL tests require FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING and create
isolated disposable databases; never target factory production through the tests.
CI runs the SQL tests in the existing integration suite and the maintenance
conformance/publish checks in moxa-recovery.yml. Mocked maintenance tests do not
establish Windows executable/process or production acceptance.

## Acceptance and failure review

On a clone: exercise normal correction, row/key/policy/checkpoint mismatches,
existing outputs and unsupported ownership, audit rollback, competing writer,
timeout, replay and lost-ack inspection. Rehearse wrapper interruption before/after
SQL commit, config activation and process launch; retain the persistent marker
until a separately reviewed reconciliation proves the outcome. Do not delete it
merely to make restart proceed.

After approved production activation: prove checkpoint advancement, before-boundary
empty-output consumption, crossing interval clipping, and no activity-derived
outputs before 02:00 UTC. Confirm row 4 binding/configuration unchanged and GG-003
historical reporting accepted, preserving insufficient-evidence classifications.
Missing shift coverage after the boundary remains an error; recovery does not
prove that the configured admitted-time shift coverage is complete.
