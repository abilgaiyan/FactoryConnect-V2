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
