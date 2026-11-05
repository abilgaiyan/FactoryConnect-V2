# FC2-003 G1 — Schema ownership and immutable configuration packages

**Status: DESIGN PROPOSAL v0.2 — DIRECTION ACCEPTED WITH QUALIFICATIONS; G1 NOT FROZEN.**  
PR #3 stays draft. No DDL, migration runner, database creation, configuration write endpoint, deployment or machine cutover authorized.

Related: [foundation design](FC2-003-sql-foundation-design.md), [review qualifications](FC2-003-review-qualifications-v0.2.md), [accepted direction and gates](FC2-003-design-acceptance-v0.3.md).

## 1. Ownership and authority matrix

The schemas below are proposed. The **authority** column identifies the sole logical writer, not a SQL login. Read projections may consume these records but may not modify their authority.

| Table | Schema | Authority | Purpose and primary key |
| --- | --- | --- | --- |
| Site | `master` | Master administration | `SiteId` GUID; immutable site identity and lifecycle. Current display timezone is non-authoritative for historical configuration. |
| ProductionLine | `master` | Master administration | `ProductionLineId` GUID; required `SiteId`; stable line identity |
| Machine | `master` | Master administration | `MachineId` GUID; required `SiteId`; stable machine identity and lifecycle |
| MachineExternalAlias | `master` | Master administration | `AliasId` GUID; typed, scoped, effective-dated external identifiers |
| SharedScheduleRevision | `configuration` | Configuration authoring | `ScheduleRevisionId` GUID; immutable submitted schedule revision and content hash |
| SharedBreakPolicyRevision | `configuration` | Configuration authoring | `BreakPolicyRevisionId` GUID; immutable submitted break-policy revision and content hash |
| SiteTimezoneRevision | `configuration` | Configuration authoring | `TimezoneRevisionId` GUID; immutable IANA/Windows mapping identifier, timezone ruleset/provenance and content hash |
| SharedPolicyRevocation | `configuration` | Controlled policy safety authority | `PolicyRevocationId` GUID; append-only safety-critical revocation of exact schedule, break or timezone revision, actor, reason, effective instant and operation identity |
| RuntimePolicyNotification | `configuration` | Activation/runtime coordination | `NotificationId` GUID; requested notification to assigned runtime, exact revocation and machine |
| RuntimePolicyNotificationAcknowledgement | `configuration` | Trusted runtime acknowledgement | `NotificationAckId` GUID; observed notification or applied-stop acknowledgement, exact runtime identity, machine and revocation; distinct from database eligibility |
| ConfigurationPackage | `configuration` | Configuration authoring | `PackageId` GUID; stable package lineage identity |
| ConfigurationPackageRevision | `configuration` | Configuration authoring | `PackageRevisionId` GUID; parent `PackageId`, revision number, canonical content hash and lifecycle |
| PackageMachineScope | `configuration` | Configuration authoring | `(PackageRevisionId, MachineId)`; explicit package membership and machine-specific settings |
| PackageMachineAssignment | `configuration` | Configuration authoring | `(PackageRevisionId, MachineId)`; line assignment and effective interval, pinned schedule/break revisions |
| PackageSignalMapping | `configuration` | Configuration authoring | `MappingId` GUID; per-machine exact signal source, address, semantic type and normalization |
| ConfigurationApproval | `configuration` | Configuration approval | `ApprovalId` GUID; exact submitted revision/hash, scope, actor and decision |
| ActivationRequest | `configuration` | Activation control | `ActivationRequestId` GUID; exact approved revision/hash, machine subset, intended effective interval, operation identity |
| ActivationRequestMachine | `configuration` | Activation control | `(ActivationRequestId, MachineId)`; requested machine scope and assigned runtime identity |
| ActivationAcknowledgement | `configuration` | Trusted runtime acknowledgement | `AcknowledgementId` GUID; machine, request, runtime identity, hash, result and observed instant |
| MachineActivationState | `configuration` | Activation projection under transaction | `MachineId` GUID; latest eligible acknowledged effective revision plus separate pending candidate reference and concurrency token |
| ConfigurationChangeAudit | `configuration` | Append-only audit authority | `AuditId` GUID; actor, operation, exact revision, decision and time |
| InstallationIdentity | `platform` | Explicit installer bootstrap | One V2 installation UUID, product marker, creation metadata |
| SchemaMigration | `platform` | Migration runner only | Ordered migration ID, SHA-256 checksum, applied instant and installation UUID |

`acquisition` and `reporting` remain **reserved future schemas** without tables in FC2-003A. Administrator principals, grants, sessions, credential authority and revocation storage are **not yet assigned tables**: G2 must settle their owner and trust boundary before write endpoints.

## 2. Package-owned content vs pinned shared revisions

- **Package-owned and immutable after submission:** package revision identity, declared machine set, per-machine line assignments, effective intervals, adapter/signal mapping, source semantics, declared capability intent, and exact references to shared policy revisions. A new edit requires a new package revision.
- **Shared and independently revisioned:** site timezone definitions, shift schedules and break policies, each with immutable revision identity and content hash. Their lineage may publish later revisions; old revisions remain addressable.
- Each package pins an exact `TimezoneRevisionId` and hash in addition to schedule/break revision IDs and hashes. Do **not** use a mutable `master.Site.TimezoneId` when interpreting an existing package, shift, observation or historical report. Site timezone display defaults may be updated for administration, but are not the authority for package execution or historical interpretation.
- Each machine assignment pins `ScheduleRevisionId` and `BreakPolicyRevisionId`, including exact hashes in the canonical package content. The package pins its timezone revision and hash; no `latest` or floating reference is allowed. Schedule local wall-clock expressions must resolve under that pinned timezone revision, including DST gap/fold handling and ruleset provenance.
- Shared revisions must be submitted and eligible for reference at package submission; they cannot be deleted or mutated after being referenced. Approval and activation revalidate reference integrity and eligibility.
- Changes to shared policy do **not** rewrite package contents or silently activate a new policy. New policy adoption requires a new package revision and activation.
- The package's machine scope is fixed at submission; activation may select a subset of that scope but cannot add machines. A machine cannot be activated twice in conflicting effective intervals without explicit supersession rules.

## 3. Cross-package references, site/line scope and time

- A package revision has a declared `SiteId` scope. Every machine and line in the package must belong to that site; no implicit cross-site assignment.
- Cross-package references are limited to immutable shared schedule/break revisions and stable master identities. A package may identify its predecessor revision within the same package lineage, but cannot import another package's mutable assignment or mapping.
- An activation request references exactly one approved package revision, its canonical hash, a subset of its machine scope, and a bounded intended effective period.
- Effective periods use UTC `datetimeoffset` instants with **half-open** intervals `[from, to)`; `to` may be null for open-ended periods. Require `to > from` when present. Local shift semantics use the **pinned immutable site-timezone revision** and pinned schedule/break revisions rather than the mutable `master.Site` display timezone.
- Overlap rules apply **per machine** across active or pending candidate intervals; exact conflicts are rejected unless an explicit replacement/supersession operation identifies the prior active assignment and expected version. Mere package approval does not reserve a runtime interval.
- A package revision can be approved but never activated; machine state is derived from accepted runtime acknowledgements and effective-time validity, not package status alone.
- The per-machine last-successful acknowledged configuration is eligible only within its effective interval and while not disabled, suspended, revoked, retired or otherwise ineligible. Pending/failed replacement retains the prior configuration **only while eligible**. No indefinite fallback after expiry/revocation. Stale acknowledgements never resurrect a revoked configuration.
- **Accepted direction, detailed semantics to freeze:** an explicitly classified **safety-critical** revocation of a shared schedule/break/timezone revision is an append-only event with exact revision ID and hash, classification, actor, reason, immutable operation ID, recorded timestamp and effective instant. Every referencing package becomes ineligible at or after that instant without rewriting historical packages, approvals, acknowledgements or machine evidence. A normal shared-policy replacement does not automatically revoke prior revisions; it requires a separately approved and runtime-acknowledged replacement package.
- Roster and effective-configuration queries must **directly check authoritative effective safety revocations** by pinned revision ID at the requested evaluation instant. They must not trust `MachineActivationState` alone, which can be stale. Retain-last-successful remains bounded by the active interval, disablement, suspension, revocation, retirement and shared-policy eligibility; never advertise expired/revoked configuration as active.
- Database ineligibility is **not** proof that the assigned runtime stopped consuming the prior configuration. Runtime notification, delivery, stop/reconfiguration acknowledgement and timeout/escalation are separate durable records. They do not delay database revocation effectiveness. Operational views must distinguish *database ineligible*, *notification pending*, *runtime acknowledgement received* and *runtime stop unverified*. The exact notification transport and runtime stop semantics are G2/runtime design decisions.

## 4. Canonical package bytes and hashing

- Canonicalization must be **versioned** (`CanonicalizationVersion`) and deterministic across .NET and SQL readers. Use a documented canonical UTF-8 JSON profile with fixed field names/order, invariant number formatting, UTC timestamps at fixed precision, explicit null representation and sorted machine/mapping arrays by stable identity. Unicode NFC may be applied only to **designated human-facing metadata** with explicitly documented normalization rules. **Do not normalize or case-fold source addresses, device identifiers, stream keys, controller tokens or other byte/case-sensitive fields.** Preserve their original bytes or exact character sequence and encoding; canonical serialization must be lossless and explicitly typed for binary fields. Use a case-sensitive or binary comparison contract where applicable, never a default case-insensitive database collation.
- Include: schema/canonicalization version; package lineage and revision IDs; site ID; exact machine scope; each machine assignment and interval; signal mappings and capability declarations **with lossless exact source address bytes/characters**; pinned timezone, schedule and break-policy revision IDs **and their content hashes**. Exclude approval signatures, actor/time of approval, activation requests, runtime acknowledgement and database rowversion.
- Hash canonical bytes with SHA-256; store binary digest and canonicalization version. Preserve the canonical bytes or an immutable blob reference for audit and cross-runtime revalidation.
- The hash alone does not prove authorization: submission freezes bytes and digest; approval binds both exact revision and digest; activation binds approved digest; runtime acknowledgement binds activation request, digest and machine.
- Reject changed content under the same revision ID or reuse of an operation ID with different canonical bytes. Canonicalization version upgrades must not rehash or mutate existing submitted revisions.

## 5. SQL-enforceable invariants and transaction rules

| Invariant | SQL constraint / transaction strategy |
| --- | --- |
| Stable IDs and ownership | PK/FK; composite unique `(SiteId, MachineId)` and `(SiteId, ProductionLineId)` to support composite site-scope FKs |
| Package scope | FK `PackageMachineScope.PackageRevisionId`; composite FK `(PackageRevisionId, MachineId)` for assignments/mappings; site-aware FKs for machine and line |
| Revision identity | Unique `(PackageId, RevisionNumber)`; FK to package lineage; unique immutable `PackageRevisionId` |
| Pinned shared revisions | FKs to immutable timezone/schedule/break revision IDs; stored expected content hashes verified transactionally |
| Safety-critical revocation | Append-only `SharedPolicyRevocation` keyed to exact policy revision; classification, reason, actor and effective instant required; no historical mutation |
| Eligibility read | Anti-join/NOT EXISTS over authoritative effective safety revocations for all pinned references; `MachineActivationState` is not sufficient by itself |
| Revocation-activation serialization | Policy-scoped transaction locks acquired in deterministic order for *both* activation validation/commit and safety-critical revocation. Activation takes and holds a compatible shared policy lock from eligibility check through commit; revocation takes the exclusive lock and commits the append-only event before releasing. Use transaction-level `sp_getapplock` keyed by immutable policy revision ID or a proven equivalent locking protocol, with deadlock and retry tests. |
| Effective intervals | CHECK `EffectiveTo IS NULL OR EffectiveTo > EffectiveFrom`; enforce non-overlap in a serializable, per-machine lock/transaction, since ordinary SQL Server CHECK/UNIQUE cannot enforce arbitrary interval exclusion |
| Signal mapping | Unique per revision/machine/semantic key according to an explicit per-signal type policy. Exact source-address identity uses binary/case-sensitive matching and lossless encoding; **no indiscriminate Unicode normalization** |
| Submitted immutability | Revoke direct table UPDATE/DELETE for application roles; use stored transaction boundary/DB trigger or equivalent guarded write authority to reject changes to submitted revisions and referenced shared content |
| Approval/activation binding | FKs plus exact hash/expected-version checks under transaction; append-only decisions and acknowledgements |
| Runtime acknowledgement | Unique `(ActivationRequestId, MachineId, RuntimeOperationId)` and transactionally validated assigned-runtime identity, request/hash and ordering |
| Per-machine active state | One row per machine; guarded compare-and-swap `rowversion`, serialized machine-scoped transition and monotonic activation generation |
| Audit and migration | Append-only audit; installer-only `platform` writes; exclusive install lock and fail-closed installation identity checks before DDL |

**Concurrency sequence:** authorize actor/runtime → acquire shared policy-revision locks in deterministic revision-ID order and hold until commit → acquire machine-scoped lock(s) in deterministic MachineId order → validate expected revision and current assignment/runtime identity → revalidate pinned policy eligibility, effective interval, revocation and hash **within the same transaction** → append immutable decision/acknowledgement and audit → update candidate/active projection → commit atomically. Revocation takes exclusive locks on the affected policy revision(s), commits the append-only safety record, then releases locks. A revocation cannot commit between activation eligibility verification and activation commit. Any activation committed before revocation becomes ineligible from the revocation effective instant; accepted runtime acknowledgements do not override this. A completed idempotent operation may be replayed only if its immutable request fingerprint matches. Access revocation ordering and session gate remain a **G2 prerequisite**, not an implementation decision in G1.

SQL Server `rowversion` is a concurrency token, **not** a business revision or a substitute for package hashing. Constraints enforce referential integrity; runtime trust, interval exclusion, immutable state transitions and revocation checks require explicit transactional logic and tests.

## 6. Review questions / decision register

| ID | Question | Proposed direction | Status |
| --- | --- | --- | --- |
| G1-01 | Schema ownership | `master`, `configuration`, `platform`; future `acquisition`/`reporting` | PROPOSED |
| G1-02 | Package aggregate | Immutable machine-scoped revision; package-owned mappings/assignments | PROPOSED |
| G1-03 | Shared policies | Pin immutable timezone, schedule and break-policy revision IDs and hashes | ACCEPTED DIRECTION; DETAIL REVIEW |
| G1-04 | Scope and time | Single-site packages, half-open UTC intervals, serialized per-machine conflict checks | PROPOSED |
| G1-05 | Hashing | Versioned lossless canonical UTF-8 JSON + SHA-256; exact source-address semantics and type-specific normalization | ACCEPTED DIRECTION; DETAIL REVIEW |
| G1-06 | SQL integrity | PK/FK/unique/CHECK + guarded writes, per-machine serializable transition and CAS | PROPOSED |
| G1-07 | Shared policy revocation | Immediate database ineligibility for explicitly classified safety-critical revocation; append-only facts, runtime notification/ack distinct; normal replacement does not revoke | ACCEPTED DIRECTION; DETAIL REVIEW |
| G1-10 | Site timezone reproducibility | Pin immutable timezone revision and hash; no mutable site timezone as historical authority | ACCEPTED DIRECTION; DETAIL REVIEW |
| G1-11 | Concurrent revocation | Serialized policy-revision locks across activation and revocation commit; mandatory direct eligibility checks | ACCEPTED DIRECTION; DETAIL REVIEW |
| G1-08 | Runtime identity storage and access control | Assigned runtime and operation binding; credential/authorization authority | DEFERRED TO G2 |
| G1-09 | Roster capability representation | Active vs commissioning states and capability contract | DEFERRED TO G3 |

**Freeze gate:** Explicit G1 acceptance must resolve G1-01 through G1-07 **and** G1-10/G1-11, including the chosen timezone-revision provenance, source-field encoding policy, policy lock protocol and revocation-effective-time semantics. Authorize FC2-003A separately before any DDL. G2 and G3 remain independent blockers for configuration writes and SQL-backed roster respectively. This proposal does not authorize migrations or production actions.
