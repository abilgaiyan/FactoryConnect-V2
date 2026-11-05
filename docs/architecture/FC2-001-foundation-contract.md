# FC2-001 — Architecture and Database Foundation Contract

Status: **DRAFT — review required; implementation not authorized by this document**  
Repository: `abilgaiyan/FactoryConnect-V2`  
V2 starting commit: `470236a3a7b54a5d9900b8328a4d51a6b46af3cc`  
Reference FactoryConnect commit: `088bf52492546a2a79029cf366a22f7a4d7e8d44`

## Decision and boundaries

- FactoryConnect-V2 is the product repository. Preserve its existing React/TypeScript/Vite dashboard and tests.
- Build a **new SQL Server database with fresh operational entries**. Do not migrate historical raw observations, production checkpoints, publication revisions, or prior recovery state.
- Existing FactoryConnect and its production database remain independent and authoritative for their own commissioned period. V2 must not write to them.
- The original repository is a selective code/reference source, **not** a wholesale replacement of the V2 tree.
- No production deployment, machine cutover, database mutation, or original-repository modification is authorized by FC2-001.

## Target runtime boundaries

```text
MTConnect / Modbus / future adapters
    -> durable acquisition and idempotent observation ingestion
    -> new V2 SQL raw evidence + checkpoint authority
    -> incremental machine activity / quantity processing
    -> provisional shift summaries and finalization worker
    -> versioned reporting read models
    -> lightweight .NET reporting API
    -> existing V2 React dashboard
```

Ordinary dashboard requests must not traverse full historical observation prefixes, reverify every historical contribution, or execute per-retained-row containment queries. Explicit bounded evidence verification is a separate API/workflow.

## Selective reuse inventory (candidate only)

Review source, dependencies, tests, and contracts before copying:
1. Machine/domain identifiers, exact stream/position semantics, signal normalization, activity-state and OEE evaluator rules.
2. MTConnect and Modbus protocol adapters and conformance fixtures.
3. Shift/production-day and missing-evidence status tests.
4. Supervisor exact-event reference and permission rules, as design inputs.

Do **not** import unchanged: old SQL migration history, original database bootstrap, legacy report reader traversal, historical containment verification in HTTP, or production-specific configuration/recovery state.

Every imported module must record its source path and original commit, include its relevant tests, and identify deliberate behavioral changes.

## Proposed V2 data authority

Design and review tables and keys before migrations:
- Master: Site, ProductionLine, Machine, Shift, shift schedule, break policy, signal mapping.
- Configuration: effective-dated revisions, approval, activation acknowledgement, audit.
- Evidence: raw machine observations, acquisition instance/stream/position, ingestion checkpoint, activity periods, quantities.
- Reporting: machine current state, shift accumulator, shift summary revisions, current published summary pointer, daily summary.
- Supervisor: exact-event explanation threads, revisions, submission/review, access assignments, durable operation identity.

Preserve stable physical machine identity independently from adapter address and display name. No name-based identity reconciliation. Maintain separate approval, runtime activation, and historical recovery operations.

## Shift summary contract — unresolved until review

- Provisional metrics update during a shift; finalized summaries are published only after a defined evidence watermark/completeness policy.
- Finalization is schedule- and timezone-aware, not hardcoded to 06:00/14:00/22:00.
- Missing reference time, good quantity, planned production, or continuity must remain explicit; do not fabricate zero metrics.
- Late evidence and corrections publish new revisions without silently rewriting the historical publication.
- Define source-specific watermarks, idempotent effect identities, correction semantics, current-publication selection, and verification assurance levels before implementation.

## Performance and physical SQL decisions

Planning volume: 100 machines × 56,000 raw rows/day = 5.6 million/day (~65/s fleet average; burst behavior unknown).

- Benchmark bounded batch rowstore ingestion and exact predecessor lookups first.
- Monthly partitioning and columnstore are **conditional experiments**, not approved baseline requirements.
- Raw partition keys, SQL edition/licensing, retention, compression, indexes, log growth, backup and archival need measured evaluation.
- Indexed summary reads should be cheap and bounded; a 15 ms SQL seek is a hypothesis, not a guaranteed API SLA.
- Acceptance requires repeatable 100-machine load/restart/replay tests, reporting latency distributions, SQL read/CPU/log measurements, and semantic equivalence tests.

## Delivery sequence

1. **Foundation:** clean .NET solution + V2 dashboard preserved; new database schema and versioned master/config seed; combined .NET/Node CI.
2. **Acquisition:** selectively imported protocol adapters; durable ingestion, deduplication, checkpoints, restart/replay proof.
3. **Reporting:** incremental live shift accumulators, finalization and versioned projections, lightweight API, OEE conformance and load tests.
4. **Operations:** supervisor explanations and reviews, commissioning/permissions, controlled per-machine/site cutover acceptance.

## FC2-001 review gates

- [ ] Confirm latest source and V2 baselines before imports.
- [ ] Inventory dashboard API contracts and mock/test dependencies.
- [ ] Identify importable .NET projects and transitive dependencies; record source provenance.
- [ ] Approve new database name, SQL Server edition/target hardware and schema boundaries.
- [ ] Freeze identity, checkpoint, replay, shift and summary publication invariants.
- [ ] Define performance, failure, late-data and scale acceptance fixtures.
- [ ] Approve implementation slices individually; no production cutover implicit in a green build.
