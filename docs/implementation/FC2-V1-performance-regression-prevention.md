# FC2 — Prevent V1 performance failure patterns

Reviewed baseline: PRs #12–#17; main after PR #17 is `10719d2`. PR #17's one data-query assertion is retained. Current evidence covers internal protected inventory only; the API still has no reporting/acquisition provider and G3 activation authority is incomplete.

## Failure patterns and required gates

| V1 finding / limitation | V2 rule | Executable evidence / remaining gate |
| --- | --- | --- |
| Full-prefix raw observation traversal for short counter windows | Bounded time range, pinned upper frontier, exact predecessor/evidence closure, keyset pages; current reads never replay the complete raw prefix | Required before counter/history implementation; no current V2 counter provider |
| Repeated per-retained-row archive/evidence verification, often before filtering | Verify bounded selected dependencies; batch archive/evidence retrieval and reuse only within the same protected revision/cut; preserve integrity failures | Required before reporting; compare exact semantics/corruption outcomes and SQL reads as unrelated history grows |
| Caught-up workers replayed/revalidated without new input | Compare frontiers and return idle before reconstruction/publication; validate startup, advancement and explicit replay | Required before worker import; idle tests must prove no repeated history reads/writes |
| Slow work under processor gates delayed reporting and caused 30-second 499/504 failures | Short protected transactions; distinguish gate acquisition, authorization/verification, SQL work and total request duration; never fix this by increasing the UI timeout | Current inventory instrumentation and cancellation test below; reporting/writer contention still unqualified |
| Individual SQL/connection calls multiplied with machines | Set-based bounded projection reads, one connection lease per request, no N+1 machine loop; include authorization overhead in the budget | PR #17 checks one data query; this increment checks actual full-path SQL roundtrips for 1 versus 100 machines |
| Raw acquisition failures and reporting contention were not one proven root cause | Separate acquisition, processing, reporting and recovery measurements and faults; one slow/offline machine must not stall others | Required multi-machine backlog/offline/restart acceptance before ingestion readiness |
| Reporting processor identity mismatch produced missing/stale reports despite published output | Keep observation processor, metric/reporting processor and source/publication revisions explicit and consistent from roster through response validation | Required before active roster/reporting UI integration; inventory is not a live roster |
| Synthetic speedups were mistaken for production proof | Report dataset, hardware, build, concurrency, warm/fresh process state, sample count and limitations; cancellations are not successful latency samples | Structured CI artifact below; HTTP, replay, cold SQL and production capacity remain open |

Historical observations include 30-second dashboard failures, direct requests lasting minutes and separate warm runs near the timeout. They are context from V1 investigations, not a single established universal cause. Preserve evidence, checkpoints, status meanings, exact identities, isolation and revocation ordering. No NOLOCK, skipped validation, manual checkpoint advancement or timeout inflation is introduced.

## Current executable full-path inventory gate

`FactoryConnect.V2.Security` emits sampled `security.transaction` spans. Tags contain only stage timings, outcome, gate mode, SQL roundtrips/received-byte counts and connection-open attempts. No SQL text, parameters, names, identifiers, tokens, connection strings, row values or exception messages are emitted. Trace parentage connects the span to `FactoryConnect.V2.Inventory`.

Connection-open duration includes any pool wait/open handshake. Gate acquisition duration includes the application-lock command and transport, not just SQL's lock scheduler wait. Read authorization duration covers current state/session/scope/ancestor queries. Transaction duration includes begin, gate, identity, authorization, data materialization and commit/rollback. Full elapsed is measured until the transaction completes, before returning the pooled connection. SQL client statistics are enabled only when the security span is sampled; counters reset after opening and cover the entire transaction, including business data queries and transaction commands. They are TDS server roundtrips, not logical reads or a query-plan substitute. No extra diagnostic SQL is added to the product path.

A dedicated disposable database contains two sites, 100 machines in the measured site (10 retired), and one machine in the comparison site. Fixture authentication/grants are synthetic and never provisioned by the product. A fixed eight-connection pool is warmed; 128 reads run at concurrency 1 and another 128 at concurrency 8. Each response must preserve site identity, count and retirement semantics.

Regression budgets established for this **CI SQL fixture**:

| Quantity | Budget |
| --- | --- |
| Inventory data commands per successful read | Exactly 1 |
| Full protected-path TDS roundtrips | Positive, equal for 1 and 100 machines, at most 12 |
| Connection opens/leases per read | Exactly 1 |
| Warm latency p95 / p99 | Below 2,000 / 5,000 ms at concurrency 1 and 8 |
| Benchmark application pooled SQL sessions | At most 8 |
| Cancellation during observed security-gate wait | Completes within 3 seconds, executes no inventory data query; next read succeeds |

These are smoke regression ceilings with headroom under the existing 30-second dashboard timeout, not production SLAs or a claim that 100 active machines are supported. Future legitimate additional authority queries require an explicit budget review, not silent removal of the budget.

The artifact `inventory-performance` contains build/SQL/runtime metadata, p50/p95/p99/max, stage p95s, maximum full-path roundtrips, inventory-data query-cache logical-read totals, observed execution counts, inventory-data worker CPU and pool session counts, plus client allocations/CPU/working set. Inventory logical reads require exactly 128 matching executions in the dedicated database; a reset/eviction makes the delta unavailable and fails the probe. Worker CPU uses the same inventory query-cache execution boundary. Full authorization logical reads/worker CPU remain unmeasured. SQL process CPU, query plans, continuous peak pool occupancy, HTTP percentiles, retained-evidence scaling and production hardware are not measured. Warm SQL state is not claimed cold because a new API process was used. Sampled tracing overhead is included.

## Required next workload acceptance

Reporting must be tested against increasing unrelated retained history and selected evidence volume, with bounded SQL reads/commands, semantic equivalence and corruption detection. Ingestion needs 100-machine sustained/burst traffic, independent checkpoints, backpressure, one offline source and restart/replay tests. Reporting must run concurrently with ingestion and publication; capture gate waits and p95/p99 with cancellation and failed requests separate. Current availability/configuration gates must remain authoritative under revocation. Readiness remains blocked until those actual workloads and HTTP endpoints exist and their budgets pass.
