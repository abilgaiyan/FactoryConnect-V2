# FC-030 P0-C: forward-safe historical retained-projection repair

Entry baseline: `9ab1fa455f184a7fc779682fe3aed025b6e456f7`.
R1 recovery semantics, R2 transactional observation and R3 processor gate are frozen.
This slice contains persistence and conformance tests only. No factory executable,
recovery execution, deployment, merge, migration, aggregation, prerequisite or
metric-definition changes are authorized by its implementation.

## Processor publication gate

`SqlServerOperationalMetricProjectionProcessorGate` is the sole resource encoder
and acquisition entry point. Callers supply a typed processor ID and Shared or
Exclusive mode. The resource is exactly:

`FactoryConnect:OperationalMetricProjection:Processor:v1:` + uppercase hexadecimal
SHA-256 of `StringOrderKeyV2Codec.Encode(processorId.Value)`.

It has 120 ASCII characters. It does not normalize, fold case, truncate or include
SQL row identity. Hash collisions could conservatively serialize unrelated
processors; they confer no data authority. Exact persisted processor key, canonical
binary and aggregation/stream binding validation remain independent requirements.

`sys.sp_getapplock` executes in the current database, with `public` principal,
Transaction owner and an explicit 15,000ms timeout. Normal publication and recovery
acquire Exclusive before mutable publication rows; readers acquire Shared before
source/checkpoint/manifest/projection/evidence reads. Locks last through commit or
rollback. Timeout, cancellation, deadlock return codes and SQL failures escape as
operational failures, never semantic conflicts. Transaction rollback preserves the
primary exception. No session-owned locks or premature explicit release are used.

Existing child-row locks remain downstream of the application gate. Normal writer
Serializable isolation and live checkpoint/CAS/replay semantics are preserved.
Reporting uses a ReadCommitted transaction under the Shared gate: all materializing
commands use that same connection/transaction. No snapshot database option is needed.
The gate, rather than checkpoint equality, is the concurrency authority. Existing
revision/manifest validation remains an integrity check. Bounded checkpoint-change
retries are retained defensively for non-participating external writers; they do not
replace the Shared gate or make checkpoint-neutral external writes safe. Checkpoint reads return
checkpoint plus latest manifest from the same transaction.

## Separate recovery contract

`OperationalMetricProjectionRecoveryRequest` carries exact processor, Production
Day, historical source revision, and all five v1.0 unpartitioned projections with
recursive evidence. The store accepts already evaluated projections; it does not
perform evaluation or validate commissioning authority on a caller's behalf.
The future operator workflow must separately establish the P0-B proof, historical
input/definition qualifications, and execution authorization. A diagnostic JSON
record is not itself an import/publication command.

`SqlServerOperationalMetricProjectionRecoveryStore` validates exact revision/period
membership read-only before entering the short Serializable publication transaction.
Under Exclusive it validates the persisted source binding and current checkpoint,
requires historical revision <= checkpoint, rejects any target-period membership in
the latest manifest, and reads all retained target-period keys and evidence.
Unexpected definitions/contexts, differing revision/result/evidence or inconsistent
expected recursive dependencies are conflicts. Existing corrupt identity/codec data
fails operationally closed through the existing materializers, not by replacement.

- Every existing target member must be equivalent under
  `OperationalMetricProjectionEquivalence`.
- Only absent members may be inserted, using existing compilation/evidence codecs.
- No existing row/evidence is rewritten, deleted, assigned a new revision, or repaired
  by guessing. Any conflict rolls back the entire transaction.
- Complete post-write materialization must match all five expected results exactly.
- Fully equivalent retry returns Equivalent without row mutation. Successful insertion
  returns Recovered with the inserted-member count. Semantic rejection returns Conflict.
- Checkpoint, latest manifest, neighboring periods and unrelated retained evidence are
  unchanged by recovery. Live writers may advance them before/after recovery.

Mechanical compilation/insertion helpers are reused. Normal commit classification,
manifest replacement, checkpoint writing, prerequisite coordination and normal
publication execution are never called by recovery. No provider activation, worker,
HTTP endpoint or recovery CLI is introduced.

## Conformance

The focused real-SQL suites cover full absence, equivalent no-write retry and exact
latest-batch replay, partial repair without rewriting equivalent members, value,
revision, evidence and definition conflicts, latest-manifest exclusion, revision
beyond checkpoint, injected rollback/cancellation, and invalid incomplete requests.

Barrier tests cover Shared/Shared compatibility, Shared/Exclusive and Exclusive/
Exclusive exclusion, deterministic bounded resource identity, operational timeout,
transaction release and pre-cancellation. Recovery schedules cover duplicate writers,
reader-first and writer-first (paused after the first inserted member), and both
normal-publication/recovery orders. Protected-row snapshots include SQL row IDs,
values, original revisions, complete evidence content, checkpoint and manifest.

A pre-existing partial target may be visible before recovery. Participating reads
never observe intermediate recovery inserts. Direct ad-hoc SQL writers that ignore
the gate are outside this cooperative authority; no proof against arbitrary external
writers is claimed. Live processes must run the gate-aware build before recovery is
executed. Old processes/readers do not acquire this gate merely because this code
has been committed or merged.

P0-B factory proof remains conditional on the identified credible definition candidate
set, exact historical execution SHA remains unestablished, and completeness concerns
observed persisted evidence. P0-C does not strengthen those qualifications.
