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
