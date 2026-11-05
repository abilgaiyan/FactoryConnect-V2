# RBS-14 — Exact-process termination observation

Implementation candidate from `05920907e476ffe4676a4dc7bcb0d905620a2c14`.
Factory commissioning remains on hold until reviewed regression and a new immutable
release are available. No process/task/service change is included here.

`FactoryConnect.ProcessTermination.ps1` is the shared deployment/reconciliation/
failure-cleanup authority. It captures a process instance and handle, validates
PID lookup + executable path + UTC start time, requests the existing `Stop-Process`
termination on that instance, and observes that same instance. It never reacquires
a PID after termination as a substitute for exact-process exit proof. A confirmed
exit does not require the process object to disappear from enumeration.

Outcomes are `Exited`, `Timeout`, `IdentityMismatch`, and `ObservationFailure`.
Already absent/exited is an idempotent success. Query, handle, request or observation
errors cannot become exit proof. Ownership mismatch never requests termination.
There is no graceful protocol, escalation, second kill mechanism, or process-tree
termination. Existing shared locks and unresolved-intent gates remain unchanged.

Both public operations accept `-ShutdownTimeoutSeconds` (1–600). If omitted, they
resolve the single shared default of 120 seconds. This is a bounded operating
choice, not a claim that the factory root cause is established or that 120 seconds
is guaranteed sufficient. The configured interval bounds the exact-process wait;
validation/request overhead is separately included in elapsed evidence. A timeout
still rejects shutdown before migration/selection, even if the process exits later.
Cleanup reports failure and retains unresolved records/intent under existing rules.

Termination observations carry process identity, outcome, phase, configured wait,
elapsed milliseconds and detail. Deployment failure JSON contains these records;
runtime publication includes them too. Final state classification recognizes
confirmed exited instances and refreshes runtime failure evidence, so a late exit
can appear as Absent while the earlier timeout remains honestly recorded.

The release builder requires, copies, declares and manifests the shared helper.
Deployment and runtime startup require it adjacent to their scripts; do not copy
only a changed deployer over the factory. Produce and verify a new immutable
package after acceptance. Existing installed acquisition scripts are unchanged.

Focused controlled proofs execute the production helper and extracted production
shutdown/cleanup/reconciliation statements using injected process observation and
wait outcomes. Scenario 4 still deliberately skips cleanup of its real Edge; its
instrumented deployer now copies the helper adjacent to itself and matches the new
cleanup seam. That test is distinct from the genuine timeout proofs.

On Windows PowerShell 5.1:

```powershell
.\tests\deployment\Invoke-TerminationObservationConformance.ps1 -Executable
```

Without `-Executable`, the controlled proof runs cross-platform and does not claim
a real Windows kill/wait reproduction. The Windows option additionally terminates
one owned disposable process using the production request/wait implementation.
Then run B01–B12, Scenario 4 with disposable SQL, factory boot conformance, .NET and
Dashboard regression, Release build, and immutable package verification. Factory
retry, recovery of the held partial runtime, deployment and reboot remain separate
authorized commissioning operations. A2 remains blocked and unchanged.
