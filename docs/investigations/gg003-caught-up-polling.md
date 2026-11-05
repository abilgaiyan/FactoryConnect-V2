# GG-003 caught-up polling correction

Worker polling uses RunPollingCycleAsync. Existing RunCycleAsync and the original
three-argument batch request retain exact-current-revision replay behavior.
The polling request disables current-revision fallback in both the SQL prerequisite
and coherent source. With no next revision, the prerequisite returns before exact-cut
lookup, convergence, evaluation or publication. A source returning a nonadvancing
batch despite polling intent fails closed.

Startup still restores through ReadCheckpointAsync, including the existing complete
containment retained-publication verification. Restoration failures are retried and
never mark startup complete. New revision publication and explicit replay validation
are unchanged. No schema, isolation, gate timeout or reader verification changes.

## Timing collection

Subscribe an ActivityListener or OpenTelemetry tracing provider to ActivitySource
`FactoryConnect.Reporting`. No spans are allocated without a listener.
`processor.gate.acquire` duration measures the gate SQL call (including SQL round trip);
`gate.mode` identifies Shared/Exclusive and `gate.result` records the SQL return code.
`containment.publication.verify` begins AFTER the verified reader's shared gate call,
and measures complete retained-publication verification. A successful populated read
has `verification.completed=true` and `retained.result.count`.
The stable reader also acquires Shared before entering this reader, so collect all
acquisition spans in the request trace, including the small nested acquisition.
Durations are inclusive: do not add nested spans to their parent request duration.
Timeout/cancellation spans without a return code are failed attempts, not zero waits.
No connection strings, SQL text or evidence payloads are tagged.
Total API/request time should come from the enclosing HTTP trace. Verification time
includes subsequent SQL waiting and execution; it is not CPU-only time.

## Validation

New core tests cover repeated idle polls with zero additional publisher/snapshot calls,
retained explicit replay calls, restart-before-poll failure/retry, next revision after
idle, and rejection of a source ignoring polling intent. Existing conflicting replay
and manifest tests remain unchanged.

New SQL composition test publishes through the real runtime, restarts, then runs
repeated idle polling alongside verified reporting while a shared processor gate is
held. It asserts no Exclusive acquisitions and completed verification spans. This
proves compatibility under controlled overlap, not a latency improvement.

Run Release core tests and SQL integration tests using the repository's existing
FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING disposable test-database fixture.
The fixture creates/migrates test databases; do not target factory production.

Prepared here without a .NET SDK or developer SQL connection. Build, analyzer and
execution results are pending CI/developer validation. Factory deployment, production
checkpoint changes and acquisition freshness investigation are outside this slice.
