# PR #162 design amendment: validated cut evidence, independent period results

Status: DESIGN ACCEPTED; Phase A/Phase B implementation added on the existing PR #162 branch. Runtime correctness and performance acceptance remain pending. This document records the accepted boundary; see `gg003-reference-authority-reuse.md` for current implementation, counters and validation commands. The earlier period-duration cache at `bda2de39` is replaced. PR #162 stays draft. No merge, deployment, acquisition changes, schema changes, lock changes or timeout changes.

## Problem and corrected target

PR #161 preserved report semantics and reduced publication verification to once per source. The supplied four-request log has median reporting.query 27.17044165 seconds, 195 root reconstructions, 78 reference-time validations and 20 instrumented contribution SQL executions per request. All 312 reference-time outcome-read spans report zero rows. The current PR #162 cache excludes null durations and therefore likely has no entries for this workload.

The target is repeated loading and validation of historical authority evidence. Root reconstruction and canonical envelope comparison must remain independent for all retained rows. Do not cache metric outcomes, null durations, errors, or decoded envelope claims.

## Source dependency audit

Reviewed implementation: `SqlContainmentReferenceTimeAuthorityValidator.ReadCoreAsync` at PR #162 implementation head, against main `df1bbac1c0bb5ab26d43af6d71d92c78d28bb9d2`. The proposed extraction point is immediately before `var relevant = sources.Where(s => Belongs(s, period)).ToArray();`.

| Existing operation | Dependencies | Requested-period dependency |
|---|---|---|
| Validate historical contribution membership and aggregates | Full aggregation checkpoint; bound source ledger; owning Serializable transaction | None; the historical scope loads complete incorporated input membership |
| Resolve aggregation processor row | Exact aggregation processor identity in the bound database | None |
| Validate quantity units/source identity; reconstruct source groups | Complete validated input membership and source identities, ordered contributing positions | None; source entries retain their own recorded shift/day |
| Read and validate transition chain | Exact processor/cut, predecessor links, starting/final R, completed state and exact publication-cut records | None |
| Read outcome prefix and validate unique sources, contiguous R and terminal count | Processor and final R independently derived from that chain | None |
| Check transition delta membership against admitted outcomes | Complete inputs, each transition's A/R bounds and S binding | None |
| Load standard authority cuts and validate completeness | Each S revision recorded in the validated transitions | None |
| Validate each outcome against canonical resolution | Source evidence, source's OWN recorded shift/day, historical standard cut, persisted outcome | None: this does not use the requested reporting period |
| Select relevant sources and outcomes | Validated sources/outcomes AND exact requested shift/day | YES |
| Construct period authority binding; calculate duration/completeness | Selected source IDs/outcomes, completed R, machine and exact requested period | YES |
| Reconstruct Performance/OEE and compare canonical envelope | Exact metric key, source cut, derived period result, all other operands/evidence | YES; remains independent |

The existing `ProductionQuantitySourceCut` abstraction is period-specific. Do not repurpose it as a cut-wide evidence certificate or omit its PeriodId while reusing instances. Introduce a distinct internal type.

## Contract A: authority-evidence identity

Proposed identity is the complete aggregation checkpoint plus fixed containment policy and an explicit in-process evidence-contract version. The checkpoint includes aggregation processor, stream identity, machine and position; numeric revision alone is never sufficient.

The connection and exact Serializable transaction are part of scope ownership, not interchangeable dictionary-key values. A scope cannot cross database connections, transactions, requests, commit/rollback/disposal boundaries or stable-read retries. A fresh reporting verification/retry creates a fresh scope. No global/static/DI-singleton cache.

Requested period, root metric and availability status are deliberately absent from the cut-evidence key ONLY because phase A has no parameter-dependent period selection. The extraction must enforce this structurally: phase A takes the complete cut, no requested period or metric key. It may inspect a source's recorded period during independent canonical validation. All source membership used in validation must remain cut-wide.

R and S dependencies are derived and validated from the exact transition history under this owner. They are not caller-supplied authority claims or independently chosen current values. No current standard selection, wall-clock resolution, configuration overlay or mutable policy is consulted on hits. If implementation introduces such a dependency, this identity must be revised before reuse is allowed.

Scope creation remains reporting-only in `SqlContainmentVerifiedRead`. Do not supply it to archive publication/preparation, convergence, acquisition, or a transaction that writes any source/authority dependency. Serializable isolation does not protect a scope from its own transaction's writes. Keep the existing gates and SQL statements/lock modes.

## Contract B: complete independently validated evidence

Proposed internal output: `ValidatedReferenceTimeCutEvidence` (name provisional), containing identity, completed R, the complete canonical source inventory needed for period selection and the complete validated outcome prefix needed for period derivation. It represents successful authority-evidence validation, not period availability.

Construction/admission is private to the full validator. It must finish ALL current checks before publishing an instance:

1. Existing stable-owner and independently validated input-membership checks, source reconstruction, canonical units and required source identities.
2. Every aggregation revision through the exact target has a completed transition; exact predecessor A and R links; final target equals the requested cut; required (A,R) publication-cut records exist.
3. Outcome source uniqueness and contiguous revision prefix through independently established terminal R; no missing prefix rows.
4. Every transition's produced-source delta exactly matches its admitted outcomes. Preserve existing source-reconstruction and quantity-key rules; do not infer extra membership or drop current checks.
5. Empty-delta transitions do not change R or carry an incompatible S binding. Nonempty deltas have the required S authority.
6. Historical standard authority chains/version inventories are complete, referenced standards exist, and outcomes match independent canonical resolution for each source's own recorded period/evidence.
7. All materialization/consistency checks succeed, cancellation is checked, and exact transaction ownership is still valid immediately before admission.

A successful SELECT, row count, aggregation revision, final R value, or decoded envelope is insufficient to construct this type. Missing/pending authority, corruption, SQL errors, cancellation and partial validation never produce an entry. Preserve exception classifications and ordering as far as the split permits; do not reinterpret failure as empty evidence.

Own immutable snapshots of retained collections and nested collection members: contributing positions and conflicting-standard IDs must not expose mutable backing arrays/lists. Read-only interfaces alone are insufficient. Retain only the canonical inputs required by period derivation plus the validated identity/completion proof. Intermediate transitions and standard inventories need not be retained after every check has passed, unless implementation needs them for subsequent derivation. No lazy SQL, deferred enumerables or callbacks that consult changing state may escape phase A.

### Empty outcomes are evidence only after validation

An empty outcome prefix can be admitted as validated evidence when the entire preceding contract succeeds: the exact completed chain establishes R=0, the complete outcome prefix has zero rows, transition delta/admission checks succeed, and all applicable source/standard checks pass. Zero rows alone prove none of this.

Do not infer no production, no relevant sources, a duration of zero, available Performance, or available OEE from empty outcomes. Preserve the validator's existing treatment of all source shapes, including source groups reconstructed from quantity evidence. Every period still runs phase B. Unresolved canonical source outcomes may be retained as validated INPUT EVIDENCE after their canonical status is independently proved; this does not cache an insufficient-evidence metric RESULT.

This is the explicit proposed amendment to the earlier admission rule: permit completed authority evidence even when period evaluation later returns null; continue prohibiting cached period availability/insufficiency results. It requires review, not an undocumented relaxation of the current non-null-duration cache.

## Contract C: independent period evaluation

Phase B takes validated cut evidence and the exact requested period. It checks evidence identity/owner/cancellation, validates the supported period shape, selects sources using the existing exact `Belongs` semantics, selects their outcomes, constructs `ContainmentHistoricalAuthority`, and calls the existing `ProductionReferenceTimeCompleteness.SumIdealDurationSeconds`.

Preserve source identity ordering, standard revision identity sets, exact source-to-outcome completeness, unresolved-outcome behavior, units, input counts, timestamp bounds, checked decimal summation and null-duration semantics. No produced sources still establish no duration. Resolved subsets are never summed as a complete result.

Do not memoize phase B results in the amended design. Performance and the OEE Performance dependency each execute phase B independently, including null/insufficient outcomes. Root evaluation, witness construction, recursive evidence, canonical encoding and per-envelope comparison remain untouched. A forged claim must fail even after phase A is warm. A phase B failure fails the request; a validated phase A object is not a certificate of that period's correctness.

Replace the existing period-result dictionary rather than stack a second cache on top of it. Publication/preparation callers keep the uncached A-then-B path, preserving their independent validations and write boundaries.

## Diagnostics and expected effects

RootReconstructionInvocations retains its existing definition and is expected to remain 195 on the recorded state. For the amended implementation:

- ReferenceTimeAuthorityValidationExecutions counts actual full phase A attempts, including failed attempts, excluding evidence hits.
- ReferenceTimeAuthorityCacheHits counts successful phase A evidence reuse, including complete empty-outcome evidence.
- Add ReferenceTimePeriodEvaluations to count phase B invocations independently.
- Add an explicit evidence-contract/version tag so these counters cannot be confused with the current period-duration-cache implementation.
- Separate authority-evidence validation and period-evaluation spans. Document any renamed/re-scoped historical.reference-time.validate span; do not compare unlike span definitions as if identical.

Actual authority validation should occur at most once per distinct eligible full cut in the reporting scope; period evaluations remain per relevant request. Do not promise 78 -> 20 from the contribution-read count: measure distinct reference-time evidence keys and actual attempts. Maintain full request/server timings and distinguish failure/retry traces. Do not add nested span durations.

## Implementation/release gates after design approval

- Resolved and empty-outcome fixtures: same complete cut reuses phase A across Performance/OEE and multiple requested periods, while phase B executes independently and returns the same canonical results.
- Different source processors, streams/machines, positions, policy/contracts and owners cannot share entries. Different periods may share only phase A; period outputs remain distinct and equivalent to uncached evaluation.
- Completed/rolled-back/disposed owners, cancelled hits and cancellation just before admission reject. Overlapping access fails closed. Failed/partial loads never populate entries.
- R=0 and zero outcomes succeeds ONLY for complete, consistent evidence. Missing/pending transition, missing publication cut, missing required outcomes, inconsistent delta, corrupt standard/outcome and forged canonical envelopes still reject, including after warming an unrelated cut.
- Assert every retained row still reconstructs and compares its canonical envelope; unchanged legacy/containment outcomes and failure classifications.
- Keep current PR #162 cancellation/ownership/forgery tests, adapting admission-specific assertions rather than deleting protections. A null-result test must assert independent phase B evaluation, not prohibition of valid phase A evidence.
- Compile and run reporting plus acquisition/publication regressions. Current bda2de39 compilation and SQL acceptance remain pending; this document adds no passing-test claim.
- On confirmed disposable developer clones only, repeat the same four-request protocol against PR #161 main and the amended candidate, using actual verified payload/configuration/schema/build identity. Compare complete JSON, exact historical revisions, root/evidence/period/hit counts, transaction and server durations, and memory use. Treat concurrent acquisition/reporting as separate validation, not established by fixed-state response equality.
- No production access, protected reference migrations, advanced A2 clone use, CI polling, merge or deployment.

Performance acceptance remains measured lower median reporting latency with complete response equivalence and preserved evidence guarantees. This design amendment is not evidence of an improvement.
