# FC2-034 — Accepted MTConnect evidence to machine activity

**Status:** Implementation contract, draft PR #34. No production enablement or inferred activity from counter-only observations.

## Customer outcome and boundary

An authenticated operator selects a machine already authorized through the PR #33 `commissioning.read` roster. The operator can inspect **closed, derived machine activity intervals** traceable to immutable accepted V2 observations, or an explicit **not available / no qualified state signal / insufficient continuity** outcome. The machine's current network connectivity, physical state, production quantity, OEE and shift attainment are **not** inferred from this view.

PR #33 provides trusted OIDC/PKCE login, actual HTTPS/SQL evidence and revocation-tested browser access. FC2-034 consumes that authority without creating another login/session/grant model.

## Evidence/semantic qualification

The durable input is `acquisition.MtConnectObservation` (migration 017) with exact `(MachineId, StreamKeyBytes, InstanceId, Sequence)`, immutable `MappingId`, `AddressBytes`, original `RawValue`, `Quality`, `ObservedAt` and linked `BatchId`/acknowledgement. The source cursor represents **retained-window acquisition progress**, not historical/physical completeness.

A machine is activity-capable only when an **approved, exactly pinned activity-state mapping** identifies a source address and explicit value-to-state meaning. Initial target is a qualified MTConnect `EXECUTION` signal. Other signals—including `PartCount`/quantity, availability assumptions, unrelated `ControllerMode` and unidentified strings—must not be interpreted as `Running`. Unknown/unavailable input is not automatically `Stopped`.

Define and version the state vocabulary and source mapping explicitly, including at least `running`, `idle`, `stopped`, `fault` and `unknown` as **interpretations of mapped evidence**, not an automatic truth for every source. Preserve exact source value and original timestamp on each transition. An unqualified value yields an explicit source-mapping gap, not a guessed state.

## Activity processing contract

1. A machine/stream/mapping source is selected from the exact **approved and runtime-acknowledged** effective configuration for the observed period. Active runtime authorization and historical source interpretation have distinct identities; later configuration changes do not rewrite old evidence.
2. Read accepted observations with a **bounded, indexed** source-specific seek on exact machine, binary stream, source instance and UInt64 sequence. Pin the input frontier; never walk from sequence zero or run an unbounded prefix reconstruction. Fetch a proven predecessor when necessary and retain its source identity. Do not join a separate source/instance or silently bridge acquisition continuity failures.
3. Deterministically map each eligible sample to an activity transition. Preserve exact timestamp precision and tie behavior, reject out-of-order or contradictory source time, and record gaps, unavailable/missing mapped samples and resets. Repeated same-state observations are not new periods.
4. A **closed activity period** requires an eligible predecessor and successor transition with an established contiguous source interpretation and positive elapsed time. Until then the processor records an **open / unqualified** status, never constructs a false closed period. Source polling gaps are not silently labelled off/idle, and evidence before the first retained observation remains unrepresented.
5. Process checkpoint and emitted periods are committed **atomically**, idempotently bound to exact source transition identities and mapping revision. On restart/retry, a committed batch cannot double-publish periods. No mutable overwrite of historical closed periods.
6. Operator reads are a separate, bounded projection path with explicit machine/site authorization. SQL returning no periods is distinct from disabled processing, no qualifying signal, incomplete coverage, unauthorized access or unavailable authority.

## Increment plan and acceptance

### Slice A — semantic domain + source qualification

- Define exact activity mapping/value semantics and a pure deterministic evaluator with small source fixtures.
- Tests: `ACTIVE→READY→STOPPED` mapping only when approved, same-state repetition, unknown/unavailable, source-instance reset, retained-prefix boundary, timestamp order/precision, identical retry.
- Counter-only fixture (the existing first-machine ingestion sample) must remain **activity-unavailable**; it must not generate synthetic working time.

## Mandatory checkpoint state for Slice B

The deterministic processor's current executable contract is `ActivityQualificationIdentity`,
`ActivityProcessorCheckpoint`, `ActivityCheckpointEvaluator.Advance` and
`ActivityProcessorBatch`. No activity SQL migration/writer is present in Slice A.

The **durable database row must persist all of these together** rather than merely
storing the last source sequence:

| Persisted authority | Contents |
| --- | --- |
| Qualification | Stable `MachineId`, exact case-sensitive `StreamKey` and `SourceAddress`, immutable `MappingId`, canonical SHA-256 of exact value→state map, independently established `ContinuityIdentity` |
| Last consumed sample | Source instance + UInt64 sequence, exact source time and raw value, quality, trusted continuity decision and exact signal identity |
| Open anchor | The original source instance + sequence/time/raw value from which the unclosed state period began; nullable only with nullable open state |
| Interpreted open state | Exact enum state, nullable when no qualified open anchor remains |
| Commit identity | Processor generation (expected prior generation + 1 for a nonempty accepted batch), exact bounded input frontier and idempotent operation/effect identity |
| Closed periods | Each exact anchor→successor transition, mapping/qualification identity, source positions, original timestamps/values, positive duration, immutable unique effect identity |

**Atomicity invariant:** a single SQL transaction must verify the expected generation
and exact input cut, validate retained-source identity and qualification, append any
new immutable closed periods, and advance all checkpoint fields together. A failure
must roll back **both** the periods and checkpoint; replay must return the prior
effect without duplicating periods. A different mapping digest or continuity
qualification cannot silently resume the old anchor. A revoked permission prevents
operator reads regardless of whether periods were historically committed.

**Bounded work invariant:** the resume operation reads exactly one checkpoint
and a bounded newly eligible source cut with its proven predecessor. An open
duration must survive batch boundaries without rereading the historical prefix.
The current pure tests exercise JSON roundtrip of the whole checkpoint and prove
single-batch, two-batch and reconstructed-worker equivalence, including conflict
detection across the checkpoint boundary. **They do not yet prove SQL durability,
transaction isolation or whole-process SQL restart.** Those are mandatory Slice B
acceptance gates. No API is allowed to present these candidate intervals as
persisted activity before those gates pass.

### Slice B — SQL processor and durable projection

- New independent V2 activity tables and versioned migrations, per-machine/stream processor cursor, exact source identities and immutable closed periods.
- Protected, bounded frontier/paging and correctness under concurrency, restart, provenance changes, mapping revocation and retention-window gaps.
- Acceptance: many observations, few transitions; bounded SQL commands/rows/latency and no per-row validation or full historical replay in HTTP. Measure 100-machine load/restart independently from PR #33's evidence-read benchmark.

### Slice C — operator activity read + browser

- Reuse the existing `MachineActivityEvidence` presentation only after adapting its contract from the *legacy* `/api/machines/v1/{id}/activity/query` to a new V2 read model. No implicit fallback to V1 responses or demo samples.
- A scoped HTTPS route returns exact, bounded closed intervals and provenance (source instance/sequence/mapping/acknowledgement and processor checkpoint), verification/coverage status and no false OEE.
- Full sign-in → HTTPS → real SQL accepted evidence → durable processing → rendered browser intervals, followed by grant/session revocation and access denial. Include a fresh-processor restart test and measured SQL roundtrips.

## V1 regression exclusions

Do not copy V1 SQL migrations or historical reporting traversal, per-retained-row containment checks, lock-bypassing reads or implicit shift/break assumptions. Do not derive machine activity from a counter value, extrapolate a state beyond an observed transition, infer missing planned production, or present a historical interval as live connectivity.

**Separate deployment gate:** customer OIDC/PKI provisioning, exact Gajra machine signal qualification, production infrastructure and commissioning remain unperformed. No V1/production data changes are authorized.

## Slice B qualification checkpoint — transactional durability implemented, evidence authority outstanding

Migration `018-activity-checkpoint-periods.sql` and `SqlActivityProcessorStore` now commit a full serialized checkpoint, immutable operation result and exact closed periods in one SQL `SERIALIZABLE` transaction protected by a per-machine/mapping lock. Disposable SQL regression cases verify atomic rollback after period insertion, stale generations, conflicting operation identities, competing writers, restored checkpoint and retry of closed periods. CI run [42376127501](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42376127501) passed **1,018 SQL acceptance checks with no skips**.

**Do not treat these fixtures as evidence-qualified activity publication.** The present `ApplyAsync` accepts `ActivityInputSample` objects directly from its caller. It has not yet been bound to immutable `acquisition.MtConnectObservation` through an independently validated, revision-pinned activity signal interpretation and bounded source-cut continuity proof. The disposable fixtures create synthetic typed samples under an isolated test machine and verify persistence mechanics only; they do not verify provenance/effect ancestry from real ingestion rows. The current `ReadPeriodsAsync` is a store-level API with no operator session check, **not an authorized HTTP route**.

**Still mandatory before Slice B closure:** pin a real approved mapping and continuity source revision; verify exact observation identities, timestamps, values and evidence frontier under the correct security/processing gate; reject invented and conflicting source samples, absent predecessor and unproven gaps; integrate a bounded processor driving the store, with a whole-child-process restart reading real committed observations; demonstrate SQL count/latency under larger retained histories. Keep the store unregistered from production hosting, API and operator UI until these authority gates pass. Nothing is deployed to Gajra/V1.

## Slice B next gate — accepted-observation binding contract (unqualified until implemented)

The batched write optimization is qualified on disposable SQL ([CI #42383237547](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42383237547)): 120 transitions, two INSERT commands, nine SQL roundtrips, 17.34 ms apply time; 1,031 SQL checks, zero skips. These figures do not establish factory capacity or source authority.

**Source authority:** a production worker must not call `SqlActivityProcessorStore.ApplyAsync` using independently constructed `ActivityInputSample` values. It must resolve a bounded, immutable, committed `acquisition.MtConnectObservation` cut with exact machine, case-sensitive stream/address, mapping ID, instance and decimal UInt64 sequence. Verify `CanonicalBytes` against `ContentHash`, the owning accepted batch/document identity and source timestamp/quality. Preserve source identities in checkpoint and emitted effects. An absent, conflicting, changed or unverifiable row blocks advancement; do not silently skip or reinterpret it.

**Mapping authority:** `configuration.PackageSignalMapping` is not, by itself, proof that a particular execution-state value map was approved and activated. Resolve the exact approved package revision, activation acknowledgement, machine/site scope, source address and value-map digest before processing; bind these to the checkpoint's `ActivityQualificationIdentity`. A new mapping/revision cannot silently resume an old qualification. Reject retired, ambiguous, unapproved or mismatched configurations. Distinguish immutable historical mapping from current runtime activation.

**Continuity authority:** the existing `ActivityInputSample.ContinuityFromPrevious` is caller-supplied and is not source proof. Establish a bounded, retained, revision-pinned source frontier and explicit predecessor/instance/sequence continuity under a protected SQL read. For unknown gaps, instance changes or unprovable retention, fence the open anchor rather than infer physical completeness. Verify the boundary between the checkpoint's last consumed sample and the first newly fetched observation, including across process restarts. A sequence difference of one is not sufficient by itself to prove complete acquisition.

**Atomicity and authorization:** the accepted evidence cut, its qualification identities, the expected checkpoint generation, emitted immutable periods and operation receipt must be bound to the same protected transaction/lock ordering, or use a proven immutable source frontier plus a commit-time revalidation protocol. Avoid a TOCTOU gap between validation and `ApplyAsync`. Operator reads remain separate: use the existing human-session permission gate and revocation ordering; do not expose the raw store directly through HTTPS.

**Acceptance before closing Slice B:** (1) positive real accepted-observation fixture from ingestion to durable periods; (2) forged sample, mismatched canonical hash, changed mapping/revision, missing predecessor, missing row, conflicting timestamp, unproven gap, stale generation and revoked activation fail closed; (3) single-batch vs split-batch vs independent child-process restart yield identical period identities, checkpoint and source-cut proof; (4) competing processors and retry/rollback retain one logical effect; (5) retained-history stress tests prove bounded read count, query plan, command count, duration and memory. Record explicit measurement limits and no skips. Keep PR #34 draft, worker/API unregistered and production unchanged until these tests pass.

## Accepted-source reader increment (implementation in progress)

`SqlAcceptedActivityEvidenceReader` introduces a bounded (maximum 100) forward-only SQL read over committed `acquisition.MtConnectObservation` joined to its immutable accepted batch. It pins exact binary stream/address, mapping, machine/site, instance and sequence window; verifies observation canonical SHA-256 and batch raw-document SHA-256, UTC source time, batch instance and declared sequence frontier. It returns source provenance and never constructs an `ActivityInputSample` or infers continuity. Empty source history returns no observations. The read is a standalone serializable transaction, **not** yet transactionally coupled to `SqlActivityProcessorStore.ApplyAsync`.

This is a verification primitive only. It does **not** establish approved/activated mapping semantics, completeness across omitted source rows, acquisition continuity, historical authorization, or a single source-cut/write transaction. No worker calls it, and no activity is published through it. Additional positive ingestion-to-activity fixtures, forged-source cases, continuity qualification and whole-process restart acceptance remain required before enabling the processing path.

## Slice B — approved mapping, bounded continuity and atomic accepted-source commit candidate

Implementation now includes three separately identifiable controls:

1. `SqlApprovedActivityMappingResolver` verifies the current machine's enabling state, acknowledged activation head, active assignment, independently approved immutable package revision and exact signal bytes under the machine configuration lock. It rejects revoked approval/activation/assignment/credential/policy and disallows a case-folded stream. Machine activity requires `SemanticKey=machine-activity`, MTConnect, UTF-8 address and a strict `activity-state-v1|SOURCE=State|...` normalization profile **inside the approved package**; `identity-v1` counter mappings cannot authorize machine state. The existing lossless ingestion path recognizes this profile without interpreting or rewriting source evidence.
2. `AcceptedActivityContinuityQualifier` derives `ContinuityFromPrevious` from exact accepted batch/document provenance. Only observations within the *same verified immutable batch* earn a continuity link; transitions across batches and first samples are conservatively marked unproven and reset the open anchor. The assurance label explicitly does **not** claim physical completeness, historical continuous generation or proven restart-boundary bridging.
3. `SqlActivityProcessorStore.ApplyAcceptedAsync` resolves an approved mapping, reads verified accepted source observations, then enters the **same serializable SQL transaction** used for checkpoint, immutable operation receipt and periods. It acquires the shared machine configuration lock before the activity processor exclusive lock, re-resolves current approval, rechecks exact persisted source row identities/content SHA-256/owner batch SHA-256 and the complete selected source-cut cardinality under `HOLDLOCK`, and binds the exact source proof into immutable operation request bytes. A rejected proof or injected fault rolls back checkpoint and periods together. The arbitrary typed-sample `ApplyAsync` entry point is now internal and accessible only to disposable SQL acceptance fixtures.

The real-MTConnect fixture includes an explicitly approved `execution` state mapping and actual accepted ACTIVE/READY source rows. It first demonstrates source-bound interpretation without publication, then exercises failed atomic commit, successful verified commit and idempotent receipt replay. This does not assert the production worker has been enabled.

**Outstanding release gates:** verify latest CI on the atomic integration; complete cross-batch source-window continuity and checkpoint-predecessor proof across whole-process restart (a conservative break is not yet equivalent to full operational continuity); run larger-history measurements including page/SQL lock/bytes budgets; confirm historical versus current approval eligibility and production-scale source completeness guarantees; add authenticated operator HTTP/browser acceptance. Keep PR #34 draft and unmerged, and do not register a production activity worker until these gates pass.

## P1 remediation — mapping-lock and effective-time review

**Writer transaction:** `SqlApprovedActivityMappingResolver.ResolveInTransactionAsync(SqlConnection,SqlTransaction,...)` runs on the same transaction as `SqlActivityProcessorStore.ApplyAcceptedAsync` after machine Shared → activity processor Exclusive locks are acquired. It does not create/commit its own transaction and obtains the SQL `@now` clock only after lock waits. The current acknowledgement/operational and assignment heads are read under protected SQL locks through publication.

**Interpretation interval:** `ApprovedActivitySignal` carries the exact intersection `[EffectiveFrom,EffectiveTo)` of the acknowledged runtime applied interval, activation request, runtime assignment, and the immutable canonical package's machine assignment interval. Closed end is exclusive. Both discovery and transaction-bound revalidation reject any retained observation preceding the start, at/after the end, or missing UTC source instants. The interval is compared for exact equality between preflight and revalidation. Current-active eligibility is distinct from historical permission: **retained pre-activation observations fail closed**, and a separate approved historical-revision eligibility contract is required before retroactive replay can be enabled.

**Testing and disposition:** A real-ingestion fixture uses source timestamps intentionally pinned to the original bootstrap time. That source may predate a later acknowledgement and is now correctly rejected with no activity checkpoint/effects; the former positive atomic commit fixture must be moved to a truly eligible source window before it can demonstrate successful persistence. A separate, deterministic *currently eligible* fixture is also required to prove expiry during the processor lock wait. A pre-activation request rejected in discovery cannot be used as a valid lock-wait race. Keep P1 lock-wait expiry acceptance open until such a race is shown, and retain draft PR #34; no worker, HTTP endpoint or production change.

## Eligible-window and post-processor-wait regression increment

The ingestion acceptance now retains the pre-activation negative fixture and generates a separate HTTP MTConnect frame with execution timestamps starting exactly at the acknowledged interpretation `EffectiveFrom`. It ingests the original document over mTLS, exercises the accepted-source writer's controlled rollback, successful atomic commit, checkpoint/qualified period readback and identical operation replay. Additional ingested documents exercise one tick before the start, the exclusive end and one tick after the end without editing retained source.

A separate expiring activation fixture begins with eligible accepted observations, holds the exact activity processor application lock from another SQL transaction, and requires an observed `sys.dm_tran_locks` wait for the activity writer application before waiting for SQL time to reach expiry. After release it requires the resolver's post-wait expiry rejection and unchanged checkpoint JSON, period count and receipt count. This is independent of the existing ingestion machine-lock expiry test.

**Validation pending for this increment:** these are executable regression cases, not yet qualified SQL evidence. No closure is claimed until the new head's disposable SQL acceptance passes. Broader security/policy/configuration lock-order review, source-cut/predecessor binding, cross-batch continuity, whole-process restart, larger histories and authenticated activity display remain open. PR #34 stays draft and unmerged.


**Lock-order review correction:** standalone mapping resolution and the accepted-source writer now acquire the existing installation security gate Shared and verify the installation identity before the machine gate. All inspected authority mutations take that gate Exclusive before policy/machine gates. The activity path acquires no policy gate after a machine gate. Writer ordering is security Shared → machine Shared → processor Exclusive → fresh authority/source/checkpoint validation, with all gates retained through commit. The expiry fixture also verifies that the waiting processor owns the security Shared gate. This closes the implementation omission, pending the new head's SQL regression evidence; it does not establish the remaining source-cut or restart gates.

## Predecessor and source-cut binding increment — accepted at b81f30cd

The accepted-source checkpoint now persists `ActivityAcceptedFrontier`: exact committed instance and inclusive through-sequence, package/acknowledgement/hash pins, and complete accepted-row provenance for the last consumed sample and open anchor. Both are bound to the existing qualification/checkpoint hash and operation result. A resumed accepted writer rejects missing legacy provenance, changed qualification, instance, acknowledgement, package, source frontier, predecessor values/identities or an anchor crossing an unqualified batch boundary. It rechecks retained predecessor/anchor rows and immutable batch hashes on the writer transaction before evaluation. There is no nearest-time lookup or historical prefix reconstruction.

The request format is version 2 and binds the exact exclusive-after/inclusive-through cut as well as its source proof. An identical completed request retains its receipt replay semantics even after later advancement; the same operation ID with a different cut is a conflict. New continuation must start exactly at the persisted through-frontier. The selected frontier must already be behind the durable acquisition cursor on the same instance, and selected rows are revalidated through commit. An initial checkpoint starts exactly one sequence before the first accepted batch of its current acknowledged source instance, preventing caller-selected initial omissions. A source epoch starting at sequence zero has no representable exclusive predecessor in the current entry-point contract and fails closed; it requires an explicit inclusive-bootstrap extension before support is claimed. Existing checkpoints without accepted provenance are not silently backfilled or joined.

Accepted-document discovery now enumerates the bounded intersecting batch metadata independently of observation projections, including batches with no surviving signal rows. Each verified document's exact-address observations in the cut must equal the projected sequence set, so removing one transition or all signal rows in a batch fails closed. Limits remain 100 observations, 100 distinct batches and an aggregate 8 MiB source payload; a larger cut is rejected. Migration 019 adds metadata seek indexes for accepted-window discovery and initial acknowledged-boundary selection. Larger-history query-plan, memory and latency qualification remains open.

Continuity is unchanged in assurance: the first sample of a new batch fences the old anchor. A split cut within the same verified batch may link only after its exact persisted predecessor has been revalidated. Adjacent sequence numbers, a reconstructed client and matching timestamps do not establish cross-batch or whole-process continuity.

The disposable ingestion fixture adds independently ingested continuation documents, reconstructed SQL clients, split same-batch success, cross-batch fencing, exact receipt replay and negative cases for initial omission, skipped/overlapping cuts, unaccepted future frontiers, unrelated instances, checksum-consistent checkpoint/provenance/policy corruption, and partially/wholly missing projections. These cases are CI-qualified at `b81f30cdd6c139ddcaf8aa78cb4c3f51439634bd`; all three jobs in [CI #42397602730](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42397602730) passed, including the full SQL regression. They do not claim whole-child-process restart, source physical completeness, activity HTTP/browser delivery or production readiness. PR #34 remains draft.


Checkpoint provenance also pins its producing operation ID. Continuation requires exact equality with that immutable operation's result at the applied generation before revalidating predecessor/anchor rows. A checksum-consistent fabricated checkpoint naming other genuine retained rows cannot manufacture progress or source ancestry. SQL negatives include that multi-field forgery and removal of the exact retained checkpoint predecessor independently of the newly selected source cut.


## Whole-child-process restart acceptance — implementation, SQL qualification pending

`ActivityProcessRestartAcceptance` runs an acceptance-only child mode of the SQL acceptance executable. Child dispatch precedes any bootstrap or fixture creation. The parent passes scoped connection/signal/cut configuration through redirected stdin, never process arguments or artifacts. Both parent and child require the generated `FactoryConnect_V2_Test_<guid>` database naming contract. No application worker, production crash hook, API, migration or deployment is added.

The existing real HTTP/mTLS ingestion fixture independently accepts three original documents under the approved and acknowledged mapping: `ACTIVE → ACTIVE → READY` within one batch, a separate `ACTIVE → READY` batch, and a pending batch for negative restarts. Eight distinct OS child processes call the existing `ApplyAcceptedAsync`; they receive no in-memory checkpoint from the parent and reconstruct authority, predecessor and committed operation ancestry from SQL.

| Process boundary | Required evidence |
| --- | --- |
| After open-checkpoint commit | Parent independently reads exact committed checkpoint; child is killed abruptly; SQL retains original anchor, later repeated-state sample and one receipt |
| Before closing-period commit | Disposable `AFTER INSERT` period trigger waits on parent-held application lock after checkpoint/receipt/period writes; SQL DMV confirms child wait; live child is killed before release |
| Fresh process after rollback | Checkpoint/period/receipt snapshot is unchanged; same operation ID safely executes once and emits the exact original-anchor-to-successor period |
| After closed-period commit | Parent reads exact period/receipt/checkpoint, kills the live process, and confirms durability |
| Cross-batch restart | New child emits only the next batch's internally proven period; no bridging interval is constructed |
| Historical receipt replay | Another child retries the earlier committed operation after newer advancement; original receipt/result is returned and newer checkpoint/period counts remain unchanged |
| Invalid durable provenance | Fresh children reject missing checkpoint provenance, altered ancestry and missing exact retained predecessor, with unchanged checkpoint/period/receipt snapshots |

The parent waits for killed-child SQL sessions and rollback to drain before asserting durable state, drains child output and reaps children on failure. The temporary SQL trigger and intentional checksum-consistent/projection corruption exist only in the disposable database and are restored. `artifacts/ingestion/activity-process-restart.json` records checks, process IDs, kills, sanitized results and durable snapshots; the existing CI ingestion artifact upload includes it.

**Disposition:** build/dispatch checks and executable fixture implementation are not SQL crash acceptance. Keep this section pending until the new head's disposable SQL run succeeds. Same-batch restart recovery and cross-batch *break preservation* are the intended qualification; continuous acquisition across batches and physical MTConnect completeness remain unproven. Receipt replay retains current authority/source revalidation and is not a new historical-receipt retrieval API. Larger-history SQL work, latency and memory, activity API/UI and FC2-034 merge readiness remain open. No production changes or merge.

## Larger-history measurement increment — implementation, SQL measurements pending

The restart harness at `03ac8cc5658b49cfe77646334bcdadcc1b094ff0` is now fully CI-qualified: [CI #42399116377](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42399116377) passed backend, dashboard and the complete SQL job. This establishes same-batch recovery and cross-batch break preservation across actual process lifetimes, not sustained cross-batch or physical acquisition completeness.

`ActivityHistoryMeasurementAcceptance` extends the full SQL suite with one-machine, serial history depths of 16, 128 and 1,024 newly retained batches. Each batch has 100 original HTTP/mTLS-ingested observations and two explicit execution states. Setup processes each bounded cut normally, without manufactured checkpoint jumps or bulk-inserted synthetic evidence. At each minimum depth, five ordinary equivalent tail-cut trials and one separate profiling trial append additional batches. Final processing covers 1,030 new batches / 103,000 observations beyond the existing fixture baseline. The standalone ingestion/browser paths and full regression retain their existing workloads. A dedicated `--activity-history-only` command/CI step creates and drops its own uniquely named disposable database and runs history measurement once.

Diagnostics add tracing spans around mapping discovery, commit-time source verification, predecessor verification and transactional publication. Reader spans retain payload/parse/roundtrip counters. These optional spans do not change SQL statements, locks, request format, evaluation, replay or publication ordering.

Ordinary trials record end-to-end elapsed time, stage spans, all-thread allocated bytes, managed bytes before/after collection and process working set. Five repeats are a disposable baseline, not a statistical production SLO or fleet capacity proof. Allocation includes runner/oracle/driver overhead. Setup-only security traces are cleared per ingestion batch, and the oracle uses an incremental digest instead of retaining historical observations/results.

A separate ad-hoc SQL Extended Events session, filtered to the profiling application's unique name, captures RPC/language-batch command logical reads, SQL/stored-statement completions and `query_post_execution_showplan` actual access counters. Command totals are kept separate from nested statement counters so the latter are not added twice to the logical-read total. It is removed in `finally`; profiling time is kept separate from ordinary latency trials. Dropped/truncated evidence or missing actual observation-access plans fails the fixture. Statements are correlated with the narrowest client UTC stage span; unmatched events remain explicit. Statement row counts mean rows affected/returned, not rows examined. Actual `ActualRowsRead` values and missing-counter coverage are retained per access operator, table and index.

Method references: [Microsoft's showplan profiling guidance](https://learn.microsoft.com/en-us/shows/sql-workshops/extended-event-query-post-execution-showplan-in-sql-server) and [ring-buffer target/overflow behavior](https://learn.microsoft.com/en-us/sql/relational-databases/extended-events/targets-for-extended-events-in-sql-server). Both profiling overhead and ring-buffer XML truncation must remain visible when interpreting results.

The regression assertions require constant reader/writer roundtrips and profiled SQL statement count, exactly two selected state rows / one parsed document within the existing byte budget, exact per-cut period/checkpoint/provenance output, one receipt per cut, and replay without new effects. A separate bounded 100-row page walk hashes all newly emitted periods and compares with the streaming oracle. At deepest history, missing predecessor, omitted selected observation and corrupt predecessor-document cases must fail closed with unchanged checkpoint/period/receipt snapshots and restored fixture evidence.

**Evidence:** `artifacts/performance/activity-history-measurement.json` contains trials, stage summaries, retained sizes and exact-output digests. `activity-history-profile-{depth}.json` retains raw XE XML plus extracted statements/access operators. Existing performance artifact upload includes these files. Logical-read, actual-access, latency and memory envelopes remain measured evidence for review; no arbitrary production threshold is invented and this implementation does not yet close the larger-history release gate. The first profiler attempt stopped for missing required evidence. The collector now retains raw failure captures and includes RPC/stored-statement events; SQL execution/measurement is pending for the corrected head. PR #34 remains draft; no production, worker/API/UI or merge action.
