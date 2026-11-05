# Reference-time semantic traversal candidate — 30 October 2026

Development candidate only; no merge, deployment or restored-database replay. This backend work is separate from PR #169. Source/behavior oracle: a91618de8d358474834845f3665012129ddd5273; branch base main 3778bf2d has the same validator implementation.

## Change and invariants

The original semantic loop filters every historical input and outcome for every transition. The candidate filters produced facts once, sorts their positions (without narrowing UInt64), then traverses each validated A/R delta and outcome range in order. The caller still reads/validates the complete chain and outcome revision sequence. Outcomes are never sorted into apparent validity. Empty deltas remain valid only under the existing rules.

The unchanged rejection sequence remains: duplicate source outcomes, contiguous R and terminal R, source dictionary, then per transition outside-delta admission, missing source membership, empty authority consistency, required S, standard inventory and canonical resolution. Dictionary lookup remains TryGetValue. Missing historical authority never becomes an unchecked dictionary index failure. Each standard cut is still loaded through the existing transaction-bound SQL reader and cached only in the existing per-validation dictionary.

Full-publication verification, all retained results, COUNT_BIG cardinality checks, SQL predicates, isolation, gates, request timeouts, period evaluation, transaction-scoped exact-cut caches and defensive evidence copies are unchanged. The new cursor is ephemeral traversal state, not validated proof or a cross-request cache. The existing full phase-A loader remains the sole production constructor of validated cut evidence. Cancellation is checked before evidence is returned/admitted; no immediate per-iteration responsiveness guarantee is added.

The semantic body is extracted into a database-free internal seam. The test-only `ReferenceTimeSemanticOracle` preserves the original LINQ body, substituting only the standard-loading delegate and representing its terminal cancellation check. There is no runtime switch to skip validation or use an unverified fast path. Benchmark access is limited to a friend assembly, as with existing integration tests.

## Equivalence coverage and limitations

New database-free tests compare exact exception class/message/inner exception and standard-load order against the pinned oracle. They also assert that inputs, sources, transitions, outcomes and versions remain unmodified. Coverage includes empty/dense/sparse/multi-fact valid histories, the common A10/A20/A30 → R1/R1/R2 history, permissible reversed input enumeration, near-UInt64-max positions, canonical resolved/missing-identity/missing-standard/ambiguous outcomes, duplicate/missing/reordered outcomes, outside/missing delta membership, incorrect S/canonical outcomes, missing standards, source reconstruction contradictions and cancellation at a deterministic standard-read checkpoint. MP04/MP05 precedence is compared at the semantic seam.

Fixture construction has independent count, position, quantity and deterministic-serialization checks for all three seeds and background populations. Valid multi-fact sources use produced=10, good=8, optional rejected=2 with coherent identity and periods. Duplicate produced contributions remain corruption, not a valid four-fact workload.

Important reachability distinction: database-free semantic mutations cannot replace full-pipeline SQL oracle execution. In particular:

- O05 must set the unchanged-R transition's S to null when materialized through the real transition constructor. Keeping a non-null S with unchanged R would reject during materialization before reaching membership validation. The semantic test supplies a constructor-compatible empty-authority claim while retaining a required produced fact, which then fails membership as intended.
- STD01 directly exercises the defensive semantic branch using record mutation. The SQL materialization constructor may reject such malformed transition state earlier. This test is not a claim about full-pipeline precedence.
- AR01–AR05, PC01–PC03 and MP01–MP03 require SQL/loader-level validation. Existing SQL chain/historical/corruption suites remain mandatory in CI; duplicate-cut feasibility must respect actual uniqueness constraints.
- OWN01 ownership-copy proof, OWN02/OWN03 provider lifetime behavior and CANCEL02 cancellation-before-cache-admission remain governed by the existing production loader/cache and require their SQL-backed acceptance. The new cancellation test confirms semantic rejection, not independent cache-admission proof or instantaneous interruption.

No claim that the entire frozen v1.1 catalogue has completed oracle execution is made. Unexecuted SQL cases and exact checkpoint/oracle observations remain open review gates.

## Reproducible benchmark

`tools/FactoryConnect.ReferenceTimeBenchmark` is database-free and accepts no connection options. Build before execution:

```sh
dotnet build tools/FactoryConnect.ReferenceTimeBenchmark -c Release
dotnet run --project tools/FactoryConnect.ReferenceTimeBenchmark -c Release --no-build
# Longer, full frozen synthetic matrix (not executed for this PR locally):
dotnet run --project tools/FactoryConnect.ReferenceTimeBenchmark -c Release --no-build -- --full
```

Default smoke: T=100/500/1000, seed 16901, b=4, empty/dense1, repeated standards. Full matrix: T=100/500/1000/5000/10000, seeds 16901/16902/16903, b=0/2/4, empty/dense1/dense2/sparse/multi2/multi3, repeated/changing standards. Each dataset has 3 warm-ups and 10 measured passes per algorithm and stage.

Positions follow L=10(i−1), then b background facts followed by quantity contributions; A=10i. Dense2 has two independent produced sources. Sparse admits where i mod 10 = (1 + seed mod 10) mod 10. Multi2/multi3 have one source with 2/3 distinct quantity keys. Outcome R is contiguous; empty transitions retain R and no S. Generator IDs include seed, transition/source ordinal and fact key. The manifest records generator version and SHA256 of each serialized dataset.

Stages are traversal-only and complete **in-memory** semantics. The latter includes canonical resolution and in-memory standard-cut materialization, not SQL loading, HTTP, complete publication verification or snapshot I/O. Smoke outcomes are legitimate MissingIdentity results, not fabricated resolved durations; other canonical statuses have separate equivalence fixtures. Changing standards exercise growing complete version inventories in the full matrix.

Output JSONL records medians/ranges, process-wide allocated-byte deltas, structural collection visits, standard-load/canonical counts, size growth and elapsed growth versus the previous size. Sorting cost is included in timings/allocations but comparator calls are not counted as collection visits. Allocation values can include runtime background work. Baseline/candidate order and three warm-ups do not eliminate all tiered-JIT/GC/order effects: these are exploratory measurements, never timing assertions or a production speed guarantee.

## Local evidence

`reference-time-traversal-smoke.jsonl` contains the completed smoke run (.NET 10.0.100, Linux, DOTNET_PROCESSOR_COUNT=2). At 1000 transitions:

| Pattern / stage | Oracle median ms | Candidate median ms | Oracle / candidate structural visits |
|---|---:|---:|---:|
| Empty traversal | 26.552 | 0.061 | 4,000,000 / 4,000 |
| Empty in-memory semantics | 15.652 | 0.182 | 4,000,000 / 4,000 |
| Dense1 traversal | 27.429 | 0.194 | 6,000,000 / 8,998 |
| Dense1 in-memory semantics | 30.763 | 0.812 | 6,000,000 / 8,998 |

These results support eliminating repeated scans. They do not predict the 29.33-second restored reporting request, the 10.77-second nested semantic span, or sufficient margin beneath the Dashboard deadline. Full matrix, SQL-backed acceptance and restored request comparison remain pending.

## Remaining acceptance

- CI must build/test this separate backend PR; do not change or merge PR #169 as a substitute.
- Complete SQL-backed invariant, corruption, owner/cancellation and full-publication verification tests; retain exact source semantics and rejection precedence.
- Run/review the full frozen matrix where appropriate; no microbenchmark alone closes request acceptance.
- Recover the original October 25 SHIFT-1 request body and freeze release/configuration/processor/database manifest before separately authorized restored replay. Keep Dashboard timeout unchanged; a completed direct-API baseline needs its own approved diagnostic client budget.
- Compare exact metric/evidence payloads, source/publication revisions, request timings and reconstruction counts under equivalent restored state. No production connection, schema mutation, checkpoint/admission change, SQL lock weakening or historical rewrite is included.

## Validation commands and result

Local .NET 10.0.100 builds for the integration project and benchmark tool passed with zero warnings/errors before execution. The non-SQL integration suite passed **862/862**, including **34 new database-free equivalence/generator cases**, zero skips/failures. Structural visit tests include every proposed T through 10,000; this is not a full timing-matrix execution. Smoke timing matrix: six datasets (T=100/500/1000 × empty/dense1), two stages and both algorithms, with 3 warm-ups/10 measured runs. Raw JSONL has one manifest and 24 measurement records. Whitespace checks passed. CI is assessed separately and is not polled by this change.

```sh
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'Category!=SqlServerIntegration'
```

No SQL Server, restored clone or Gajra endpoint was contacted during local validation. Full SQL integration and the frozen remaining oracle/replay gates are not claimed as passed.
