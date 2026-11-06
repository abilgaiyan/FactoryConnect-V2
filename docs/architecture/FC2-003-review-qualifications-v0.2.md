# FC2-003 — Review qualifications v0.2

**Status: PROPOSED, not frozen.** Applies to [FC2-003 foundation design](FC2-003-sql-foundation-design.md). Review baseline: draft PR #3 at `39707cd`. This addendum supersedes conflicting wording in the initial proposal **for review purposes only**; it does not authorize implementation.

## Q1 — Environment-specific database names

The database name is a required, explicitly selected environment setting, never a schema invariant. CI allocates unique, disposable test database names per job; local development uses a persistent but isolated database (suggested `FactoryConnect_V2_Dev`); Gajra production database name is **not yet chosen**. `FactoryConnect_V2` is an example, not an approved universal name. Each environment has independent secrets, installation identity and data. SQL Server 2022 Developer in disposable Docker is approved **only** for development/integration testing.

## Q2 — Immutable configuration packages

A configuration package is an immutable revision with exact machine scope, hash and effective-period semantics. It pins the exact shared shift schedule and break-policy revision IDs. Shared policy changes do not mutate previously approved packages. Approval and activation operate on exact package revisions; explicit machine-scoped activation may be partial, with per-machine outcomes recorded. Package dependency completeness, interval overlap validation and policy-revision eligibility must be specified before DDL.

## Q3 — Runtime-bound activation acknowledgements

An acknowledgement is accepted only from the authenticated runtime **currently assigned to the named machine** and must bind to its exact activation request ID, machine ID, immutable package revision/hash and intended effective period. Reject unassigned runtimes, stale/out-of-order acknowledgements, mismatched hashes, and conflicting duplicate operation IDs. Completed identical retries should retrieve the stored outcome, not apply a second transition. Runtime identity provisioning, credential rotation and the exact trust mechanism remain open security questions.

## Q4 — Independent commissioning discovery

The live `GET /dashboard/config` roster does not bootstrap new machines. A separate authenticated commissioning read model/API must expose approved, pending machine assignments and activation status **even if no machine has an active acknowledgement**. Commissioning discovery is not evidence that a machine is active. Exact endpoint, authorization and response fields must be approved before API implementation.

## Q5 — Per-machine retained active configuration

Track candidate activation and last successful acknowledged effective configuration separately. If an attempted replacement is pending, partially successful or failed, retain the previous effective configuration **for each affected machine** until replacement succeeds and is effective. Never advertise the pending package as active; show pending/failed activation independently in commissioning. A never-activated machine has no active configuration. Define retirement/revocation/expiration separately; retention of a stale or revoked configuration must not bypass an explicit safety suspension.

## Q6 — Installer identity and pre-DDL protections

Database-name denylists are not sufficient. Require an explicit SQL server/database target, operation mode (bootstrap or upgrade) and expected V2 installation identity; no silent default target. Before schema DDL, establish an exclusive installation lock and inspect database state. Bootstrap is allowed only under an explicitly approved empty-database policy. An unrecognized **nonempty** database must fail closed with no DDL; an existing V2 database must match its installation identity, ledger and migration checksums. Never adopt, drop, overwrite or repair an unknown database implicitly. CI creates a unique disposable database and verifies rejection of a seeded unknown database and mismatched installation identity.

## Decision status and implementation gates

| ID | Decision | Status |
| --- | --- | --- |
| D-01 | SQL Server 2022 Developer in disposable Docker for integration tests | **APPROVED** |
| D-02 | Environment-configured database names | PROPOSED — review pending |
| D-03 | Production SQL target / name | OPEN, outside FC2-003A |
| D-04 | `master`, `configuration`, `platform`; reserve future schemas | OPEN |
| D-05 | Machine-scoped immutable packages pinning schedule/break revisions | PROPOSED — review pending |
| D-06 | Assigned-runtime, exact-request/hash/machine acknowledgement | PROPOSED — review pending |
| D-07 | Admin bootstrap, delegation, grant limits, revocation and runtime credential authority | **OPEN** |
| D-08 | Commissioning discovery independent of active roster | PROPOSED — review pending |
| D-09 | Per-machine retain-last-successful effective configuration | PROPOSED — review pending |
| D-10 | Explicit installation identity and unrecognized-nonempty-DB rejection before DDL | PROPOSED — review pending |

**Gates:** D-02, D-04, D-05, D-06, D-08, D-09 and D-10 require explicit freeze before implementation that depends on them. D-07 remains open before configuration write endpoints are implemented. The existing dashboard decoder and test fixtures must be verified before freezing the roster response. No migrations, SQL instance/container startup, production access, cutover or branch merge is authorized by this addendum.
