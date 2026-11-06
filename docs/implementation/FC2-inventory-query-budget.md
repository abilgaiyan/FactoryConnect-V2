# FC2 — 100-machine inventory query-budget acceptance

The synthetic 100-machine acceptance test now attaches a local `ActivityListener` to the real protected inventory read. It verifies one completed span, the returned row count, a nonnegative elapsed time and **one inventory data query**, independent of machine count. The listener is disposed before concurrent reads.

This guards against accidentally introducing an N+1 machine-query loop into the inventory implementation. The count deliberately excludes G2 authorization queries, connection opens and lock acquisition; those need separate instrumentation and benchmark evidence. No latency SLA is inferred from CI runner timings.

The G3 active roster remains blocked on authoritative activation acknowledgement, policy/runtime generation and revocation visibility. These facts must be read as a coherent protected cut before publishing a roster or wiring the React dashboard. A synthetic inventory row is not a commissioned machine.
