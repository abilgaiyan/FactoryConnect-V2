# FC2 — Authenticated package submission and independent review

Baseline: `main` `1e4fbc2` (PR #19). This executable prerequisite supplies the review provenance needed by G3. It does not yet implement activation requests, runtime enrollment, acknowledgement, policy revocation, current activation projection or roster publication.

## Authority and immutable facts

`SubmitConfigurationAsync` requires both current site-scoped `configuration.edit` and `configuration.submit` permissions. This initial workflow creates an inline package revision and records the authenticated caller as both author and submitter. There is no author supplied by the client, transfer of authorship or adoption of an existing unattributed row. Existing persistence-only fixtures remain stored but are ineligible for review through this workflow.

`ReviewConfigurationAsync` requires independent site-scoped `configuration.review` authority. The exact immutable revision/hash and its durable authenticated submission must exist. The author or submitter cannot review their own revision even when they have review permission; SQL additionally enforces this separation. One terminal approved/rejected decision exists per revision. Revision changes require a new submitted revision; a rejected revision cannot be changed in place. General review revision/supersession and explicit approval revocation remain future features.

Approval records a permission-checked human decision at commit. It does not establish runtime application, current policy eligibility, machine connectivity, available OEE evidence or production readiness. Revoking a reviewer's later permission denies new actions and receipt retrieval; it does not silently erase a historical decision. Any future approval withdrawal must be an explicit append-only authority.

Migration 009 adds immutable submission/review records, exact revision/hash composite references, actor-to-operation foreign keys and self-review constraints. Actor/reason/operation/audit data remain outside the canonical package content, preserving frozen G1 hashing. Old submitted rows are not backfilled with guessed creators.

## Transaction and lock order

The command uses the existing operation-ID lock → exclusive installation security gate. It validates current session and exact action permissions, checks authorized exact completed operations, then expected security epoch. Next it acquires all policy locks before machine locks, sorted ordinally within each phase, through one SQL lock batch:

- Policy resource: `fc2:policy:<installation N>:<timezone|schedule|break>:<revision N>`, shared.
- Machine resource: `fc2:machine:<installation N>:<machine N>`, exclusive.

`ConfigurationLockResources` supplies the same keys for future policy/activation authorities. Initial session/permission checks are admission only. After all policy/machine gates are acquired, the command captures final SQL UTC and revalidates session, exact action grants and expected epoch, so an expiry during the wait cannot authorize a write. Locks are transaction-owned; any failed acquisition, cancellation or SQL/audit error rolls back all facts. No lock upgrades, nested commits or alternate gate order are introduced. Future policy revocation must acquire compatible exclusive policy resources; activation and its coherent roster cuts remain unimplemented and must prove the same global order.

Submission snapshots canonical bytes before asynchronous work and derives every normalized row from that snapshot. It stores package content, provenance, operation receipt, audit and one security epoch advance atomically. Review likewise commits its decision/result/audit/epoch atomically. The security epoch orders these commands; it is not a universal source/publication revision or proof that all future G3 authorities share a cut.

Fingerprint bytes bind action, exact revision/hash, site, flags, operation identities, expected epoch and reason. Submission retries additionally compare the complete stored package bytes, rather than relying only on their digest. Exact concurrent retries return the original result and append one effect. Changed payloads/reused identities reject. Completed retries and `ReadConfigurationOperationAsync` require the original actor's current workflow permissions; security-administration permission does not substitute for edit, submit or review. A completed result never reapplies an approval or restores revoked access.

## V1 performance safeguards

The old persistence primitive sent individual SQL commands per machine and signal while its caller held authority. Package persistence now uses table-valued batches: one lineage/revision command, one set-based machine/assignment/signal command, and one final submission update. Client-side loops build bounded in-memory rows; they do not make SQL calls. Source bytes and normalized rows remain derived from the same canonical snapshot. No timeout increase, weakened validation or isolation change is introduced.

The authenticated submission limit is 100 machines, 1,000 mappings and 1 MiB canonical content per command. These bound gate work and payload memory for the current pilot; larger configurations need an explicit limit review or multiple packages, not an unbounded loop. Shared-policy rows still require separately provisioned immutable revisions.

The SQL fixture compares full protected submission roundtrips for 1 and 100 machines/100 mappings: equal counts, ceiling 28, and a five-second CI transaction-duration ceiling for the large write. This is a smoke regression budget, not a production authoring SLA or mixed ingestion/reporting benchmark. Existing 128-sample protected inventory p95/p99 and cancellation gates continue to run. Metadata-only security tracing records configuration lock elapsed time/count without resource identities or SQL values.

## Acceptance and remaining gates

Disposable SQL fixtures prove current permission separation, sealed/wrong installation, self-review/hash/stale-epoch denial, independent approve/reject, exact concurrent retries, immutable provenance, unattributed-row denial, SQL actor/separation constraints, audit rollback, policy-before-machine ordering, queued input-buffer immutability, session expiry during a held policy gate, 100-machine batch preservation and a pending submission denied after actual session revocation commits. Concurrency assertions observe SQL application-lock waits rather than treating timestamps or sleeps as commit proof.

This increment closes the authenticated submission/review prerequisite only. G3 activation storage/runtime trust, overlap/supersession, policy revocation, effective-time eligibility and protected active-roster reads remain required before publishing active configuration. No HTTP write route, private key, runtime credential, default production identity, deployment or V1 database change is added.
