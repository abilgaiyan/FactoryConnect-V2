# FC-030 P0-D — Standalone factory recovery tooling

Entry baseline: `348e6167f9a8b7e8784732ca8cc8a14559837d08`.

Implementation authorization covers this tool and tests. Factory preview, apply, deployment and merge require separate authorization. A2 remains held. No schema, metric, aggregation, prerequisite, HTTP or dashboard changes.

## Fixed targets

M01 `de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed`, aggregation processor `metric-aggregation:<MachineId>`, stream `metric-inputs`, projection processor `operational-metrics:<MachineId>:builtins-v1`:

| Production Day | Exact source revision |
|---|---|
| CAMPUS-1 / 2026-10-04 | 1211 |
| CAMPUS-1 / 2026-10-05 | 1911 |

Other targets are rejected. Neighbouring affected periods are not republished.

## Composition and authority

The non-hosted executable adapts P0-B reconstruction against merged main. It reads exact revisions, contribution→fact stream/position binding and complete persisted input prefix, selects the day, reconciles aggregate/component values, captures completed historical reference-time and standard cuts, and runs two fresh normal evaluator sessions. Historical input reads repeat. There is no prerequisite preparation, publication or Edge/worker startup.

Observed persisted evidence completeness does not prove upstream evidence was never omitted. Definition applicability remains established **within the identified credible candidate set by source/registration equivalence**. Exact historical execution SHA remains **UNESTABLISHED**. A new credible candidate requires re-verification. Current compiled definition content is fingerprint-bound. Recovery does not improve that historical qualification.

Normal `OperationalMetricProjectionFactory` constructs complete durable projections, including its rounding policy and recursive evidence. Preview default never constructs/invokes the recovery writer. The only mutation invocation is `IOperationalMetricProjectionRecoveryStore.RecoverAsync` using the merged SQL store.

One additional supporting path, SQL persistence `Properties/AssemblyInfo.cs`, grants this tool friend access to the existing shared-gated read helpers. No gate encoding is duplicated; complete authority captures reuse the existing stable-read transaction and exact checkpoint/manifest/projection/evidence materializers. P0-C persistence behavior is unchanged.

## Commands (not factory execution authorization)

Set `FACTORYCONNECT_RECOVERY_CONNECTION_STRING` in the operator process; never pass credentials on the command line. Preview can use a read-only SQL login where available. `ApplicationIntent=ReadOnly` is routing intent, not a permission barrier. Apply needs write permissions suitable for the recovery store and a connection to the writable primary.

```powershell
.\FactoryConnect.HistoricalRecovery.exe 2026-10-04 > Oct04-preview.json
.\FactoryConnect.HistoricalRecovery.exe 2026-10-05 > Oct05-preview.json
```

The full preview bundle must be retained and reviewed. Fingerprint alone is insufficient for apply.

```powershell
.\FactoryConnect.HistoricalRecovery.exe 2026-10-04 --apply Oct04-preview.json --deployment-root D:\FactoryConnect --approved-release <reviewed-40-hex-SHA> --approved-manifest-sha256 <reviewed-payload-manifest-SHA256>
```

Apply on Windows invokes the packaged deployment verifier freshly, reconstructs SQL input and re-evaluates rather than trusting preview projections. A malformed or changed bundle / changed reconstruction refuses apply as expired or invalid, not a semantic recovery conflict. Preview reports Recoverable, AlreadyEquivalent, Conflict, BlockedByLatestManifest or InvalidRevisionOrBinding. Apply requires a permitted classification; RecoverAsync revalidates then-current authority under its exclusive transaction. Outcomes Recovered, Equivalent and Conflict remain distinct.

Exit codes: 0 completed preview or successful/equivalent apply; 1 operational/validation failure; 2 cancellation; 3 store conflict. A preview exit 0 does not imply an admissible classification. If an operational failure happens after apply starts, commit outcome may be uncertain; inspect/re-preview rather than assuming rollback. Credentials/SQL exception detail are not printed.

## Fingerprint V1

`FC-P0D-1` payload binds target+processor+exact revision/source binding; recorded fact row IDs/positions/content; all persisted-prefix membership/facts; selected reconstructed aggregates; component snapshot; completed prerequisite/reference-time/standard cuts; definition IDs, semantic content and candidate qualification; all logical outcomes; five durable projections; direct and recursive dependency evidence.

Encoding is deterministic UTF8 JSON with ordinal object-property order, preserved authoritative array order, invariant exact decimal `G29` numeric representation, no insignificant whitespace. Duplicate properties and unsupported values fail. SHA256 is uppercase 64-hex. Apply checks bundle hash, recomputed hash and canonical byte equivalence. Live checkpoint/manifest, diagnostic timestamps and deployment observations are deliberately excluded from historical fingerprint authority.

## Deployment verification

Before apply, independently review release source/tree to retain P0-C and approve the distributed package's MANIFEST.sha256 hash. Neither a release-directory name nor ancestry alone authorizes a descendant. Explicit approved SHA/hash are trust inputs supplied from that review; the verifier does not prove the correctness of the review itself.

The read-only PowerShell verifier requires exactly one `current` Junction target under `releases/<approvedSHA>`, release.json source identity, pinned manifest hash and each manifested file's hash. It checks successful runtime.json selectedRelease/releasePath, Owned Edge/API records, live exact PID/path/start time, and expected app paths. This is observed process/release provenance, not memory inspection. All live publishers/readers must be gate-aware and remain on that reviewed release during apply; operator deployment exclusion is required. The check is an observation, not a lease preventing subsequent process replacement or file tampering.

The verifier changes no process, task, file, SQL row or deployment configuration. Fake-capture conformance tests do not claim factory process verification.

## Concurrent live movement and evidence

Preview and after-apply records capture processor/source binding, current checkpoint, exact latest manifest membership, every retained projection and full recursive evidence, including the target and unrelated periods. The before/after captures each have a shared-gated local transaction, but are separate observations. Live publication may change checkpoint, manifest and affected unrelated periods between them. Equality is **not** apply acceptance criteria and no causal non-interference is inferred from differences.

P0-C exclusive-gated recovery is the non-interference authority: no checkpoint/manifest DML, no unrelated writes, target membership excluded, R<=then-current checkpoint, exact existing member equivalence, missing members only, atomic post-write validation. Tool post-read verifies complete target equivalence for successful outcomes. A conflict is not overwritten. Operational timeout, cancellation, SQL failure remain operational failures.

## Tests and build

```text
dotnet test tests/FactoryConnect.HistoricalRecovery.Tests/FactoryConnect.HistoricalRecovery.Tests.csproj
pwsh -File tests/deployment/Invoke-HistoricalRecoveryDeploymentVerificationConformance.ps1
dotnet publish tools/FactoryConnect.HistoricalRecovery/FactoryConnect.HistoricalRecovery.csproj -c Release -r win-x64 --self-contained true
```

Focused tests cover inherited P0-B reconstruction boundaries, full recursive serialization, deterministic and modified fingerprints, no-write preview, apply reconstruction/mismatch refusal, deployment refusal, outcome preservation, live movement and operational failure propagation. A disposable real-SQL tool integration test (and its project reference) exercises adapted reconstruction, gated reporting capture, preview no-write, recovery and equivalent retry. A dedicated feature-branch CI workflow runs the focused tests, Windows PowerShell conformance, win-x64 publication and SQL regressions. Existing P0-C real-SQL gate/recovery/concurrency/publication readers remain regression proofs for the sole mutation seam. There is no factory invocation in tests.
