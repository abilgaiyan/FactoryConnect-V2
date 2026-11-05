# Containment runtime composition and disposable GG-003 rehearsal

P5 is accepted/frozen at `695733cf324e7073f671bb4b6e8410d0c55e4082`, with
[successful exact-head CI](https://github.com/abilgaiyan/FactoryConnect/actions/runs/39163708083).
Composition and restored recovery remain separate acceptance gates. PR #125 stays
draft; no factory deployment or legacy checkpoint correction is included.

## Explicit composition

The existing Edge worker and OperationalMetricProjectionProcessingRuntime are
retained. With `OperationalMetrics:EvaluationPolicyId` absent they use the
existing legacy evaluator/processor/store/commit contract without changed behavior.
An explicit value `availability-scope-containment/1` selects containment evaluation
at exact aggregation revisions and the distinct canonical identity:

`operational-metrics:<machine-guid>:builtins-v1-availability-containment-v1`

Unsupported policies or a persistence provider without the containment publisher
fail composition. No configuration file is changed to enable this mode by default.
SQL exposes the publisher through PersistenceProviderServices; no additional
provider is selected implicitly and no Core/Edge dependency on SQL is introduced.

The new explicit publisher interface bypasses the guarded V1 *contract*, not its
checks. The public OperationalMetricProjectionCommit guard remains unchanged.
The SQL adapter verifies every runtime projection against an independent P3
reconstruction, including recursive evidence, before preparing canonical envelopes.
It cannot silently replace a forged evaluated result. Preparation is read-only;
P4 then independently verifies every envelope again inside the complete atomic
publication transaction. Memory progress advances only after successful publication.
Same-revision runtime replay goes through the same exact P4 replay transaction.
Existing prerequisite convergence remains before evaluation/publication.

## Automated gates

Four ordinary composition cases cover default/explicit identities, policy
rejection and missing provider support (the identity theory has two cases).
Two real SQL cases use the composed Edge runtime with real revision discovery,
reference-time prerequisite, evaluator, SQL publisher and verified read path.
They cover initial shift/day publication, restart/zero-write replay, retained
public guard, no format-1 processor creation and forged runtime rejection.
The `containment-composition-evidence` artifact retains focused SQL results.

This refreshed execution environment has neither the .NET SDK nor SQL Server.
SDK retrieval was unavailable. These new tests have not been compiled or run
locally; CI must establish build, ordinary and real SQL results. The accepted
P1–P5 results are not claimed for this new composition tree.

## Disposable developer rehearsal

The new opt-in test is executable acceptance, not an already observed recovery.
It is skipped in ordinary CI unless explicitly supplied a Windows LocalDB clone.
It accepts only `(localdb)\...` and catalog names beginning
`FactoryConnect_GG003_STAGEB_REPLAY_`. The original
`FactoryConnect_GG003_RCA_20261016` and all factory hosts are rejected.

1. After composition review/CI, restore the GG-003 backup to a **fresh separate
   LocalDB database**, for example `FactoryConnect_GG003_STAGEB_REPLAY_20261019`.
   Use separate physical MDF/LDF paths. Keep the original restore and backup.
2. In a PowerShell checkout of this PR, set the connection only in the process
   environment and run the repository wrapper:

```powershell
$env:FACTORYCONNECT_GG003_DISPOSABLE_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_GG003_STAGEB_REPLAY_20261019;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
.\scripts\deployment\Test-Gg003DisposableContainmentReplay.ps1
```

The test checks the actual catalog before applying current forward migrations to
this clone, then runs the real Edge-composed runtime sequentially through revision
534. It never seeds or edits a reporting checkpoint to jump there. Normal Edge
processing is stopped at 534 for this probe; it is not started as a background
service and does not acquire CNC data.

Acceptance verifies unavailable Availability / RunningOutsidePlannedProduction,
source revision 534, original contribution position 380, fact ID
`05A4224CDC5CBBD1AC23A19F9DEBD3B62AB450EAE0501D00AB06237037411824`, value
259.637610 seconds, unplanned attribution and absent planned assignment. Verified
OEE reads must contain all three dependencies and the same original Availability
witness; the OEE root witness ID must remain null.

A fresh runtime restores the checkpoint and exercises exact zero-write replay.
Only that replay probe caps revision discovery at 534 because ordinary discovery
would correctly continue to later revisions. It still uses the actual coherent
batch source, SQL snapshot reader, evaluator, runtime, publisher and P4/P5
verification. A forged result must be rejected without mutation. Fingerprints
cover original facts, aggregation contributions/checkpoint and all legacy
reporting rows/evidence/manifests/checkpoints. Publication snapshots prove replay
and conflict do not write. Reference-time convergence has its normal independently
committed authority lifecycle; it is not part of the immutable-input fingerprint.

Preserve the disposable database and the executed test's TRX under
`artifacts/TestResults/Gg003ContainmentReplay`. A skip is not acceptance. A passed
rehearsal still does not authorize production activation or confirm all factory
signal/planning semantics. No restored database or clone connection is available
in this environment, so actual GG-003 recovery remains unexecuted.
