# GG-003 bounded historical containment catch-up

This development-only rehearsal resumes the already migrated disposable clone after the accepted revision-534 gate. It does not restore a database, apply migrations, launch Edge, acquire CNC data or activate production. Use a new draft PR after merged #125; P1–P5 contracts and production code are unchanged.

On Windows, set `FACTORYCONNECT_GG003_CATCHUP_CONNECTION_STRING` to the existing LocalDB clone named `FactoryConnect_GG003_STAGEB_REPLAY_<suffix>`. Keep credentials in the process environment. Do not use the older fresh-534 acceptance wrapper: it expects containment to be absent.

```powershell
.\scripts\deployment\Invoke-Gg003BoundedContainmentCatchup.ps1 -MaximumRevisions 50 -TimeBudgetMinutes 30
```

The default target is the aggregation checkpoint captured at startup. An optional `-ThroughPosition` selects an existing exact revision no later than that checkpoint. Positions are source ledger identities, not revision counts: 746 revisions and position 2150 describe different things. MaximumRevisions limits runtime cycles (default 50, maximum 10000); the processing cancellation budget defaults to 30 minutes (maximum 120). Setup, final fingerprints and up to 60 seconds of commit reconciliation are outside that budget; cancellation is cooperative, not process termination.

The harness requires an existing verified containment checkpoint at or beyond 534. Both revision discovery and reference-time convergence use the captured ceiling. It invokes the actual evaluation source, projection runtime, SQL convergence coordinator and atomic containment publisher directly, one revision per cycle. It does not start a hosted worker. Reference-time convergence may legitimately append authority records, independently of the atomic projection transaction.

Evidence is written beneath `artifacts/TestResults/Gg003Catchup/<unique-run>/`: TRX, `catchup.jsonl` and final `catchup.json`. JSONL records the candidate before execution and the verified checkpoint after commit. A failed finalization may leave only JSONL; preserve it and the clone. The report includes exception type without connection strings. Diagnose a reported blocker from the retained clone; an exception type alone does not establish its root cause.

A revision exception stops the run immediately, with no retry or skip. Commit errors trigger durable readback: an already committed revision is classified as interrupted after commit, and failed readback is explicitly unresolved, never a confirmed historical blocker. Candidate/bounds and progress inconsistencies also fail acceptance. Before/after fingerprints cover source facts, aggregation checkpoint/contributions and all legacy publication records. Normal time/count exhaustion is a PARTIAL run, even if the test passes; only `FullCapturedCatchupComplete=true` establishes arrival at the captured source head. The wrapper rejects skipped execution with no report.

Review evidence before resuming another bounded invocation. No live factory activation, deployment or SQL repair is authorized by this tool. This change has not been executed against the developer clone in the authoring environment; .NET/LocalDB are unavailable there. CI and actual clone execution remain required.
