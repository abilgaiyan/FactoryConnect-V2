# FC2 — 100-machine performance and correctness acceptance baseline

**Design and test gate, not a measured performance claim.** Target: one installation with at least 100 configured machines. The V1 performance incident showed that full-prefix history scans, repeated verification and prolonged read transactions can make even a two-machine report exceed dashboard timeouts. Do not reproduce these access patterns in V2.

## Query and API requirements

1. **Bounded reads:** machine roster is a current-authority projection, not a reconstruction of all historical observations. Queries must have explicit site/machine predicates and stable ordering. History endpoints require bounded time ranges, keyset pagination, upper evidence frontiers and explicit page limits. Never fetch a full raw prefix for a current-state request.
2. **Security at scale:** the existing installation-wide shared/exclusive gate is a correctness baseline, not evidence of throughput. Benchmark its contention with concurrent site readers and revocation writers; evolve to canonical fine-grained dependency gates only after proving revocation ordering. Do not remove the gate or use NOLOCK to improve throughput.
3. **No N+1 SQL:** fetch machine/activation/configuration facts in bounded set-based queries; do not open a connection or perform a verification transaction per machine. Separate current projections from historical evidence and cache only against explicit source/revision identity.
4. **Ingestion isolation:** per-machine ingestion checkpoints, bounded queues and backpressure; one slow or offline adapter must not stall all 100 machines. Preserve deduplication, ordering and restart/replay proofs.
5. **Reporting:** incremental aggregates and immutable source references. Compute live summaries from bounded current projections, not repeated scans of raw events. Preserve missing-evidence statuses rather than fabricating OEE.
6. **UI:** server-side pagination/filtering or bounded site slices, stable identifiers, request cancellation and visible unavailable/partial state. Do not raise 30-second timeouts as a substitute for repairing slow queries.

## Mandatory performance evidence before claiming 100-machine readiness

- Generate a **disposable synthetic** 100-machine roster, at least two sites, multiple lines, mixed retired/suspended/uncommissioned machines; no factory data in CI.
- Measure p50/p95/p99, SQL command count, logical reads, query plan, lock waits, connection-pool usage, memory, CPU and cancellation behavior. Record machine count, evidence volume, concurrency and hardware with every result.
- Exercise concurrent roster reads, grant/session revocations, runtime rotation and machine assignment changes; prove no unauthorized post-commit read.
- Exercise ingestion burst, one offline machine, restart/replay and slow downstream reporting. Verify no cross-machine head-of-line blocking.
- Require explicit performance budgets for each endpoint and concurrency level **before** marking the milestone accepted; target substantial headroom below the existing 30-second dashboard timeout. No unmeasured speed claims.

## Current PR #14 limitation

PR #14 implements a scoped **inventory** query under an installation-wide security gate. It does not yet have an activation authority, active roster, ingestion, historical reporting, a 100-machine load benchmark or production capacity evidence. The SQL acceptance suite shares a disposable database across slices; tests must assert scope/invariants without assuming an unchanged total machine count.

No production rollout is authorized by this performance note.
