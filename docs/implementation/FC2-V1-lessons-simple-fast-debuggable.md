# FC2 — Practical V1 lessons and V2 diagnostics boundary

Reviewed V1 reference: `abilgaiyan/FactoryConnect`, including `docs/performance/gg003-multi-shift-reporting.md`, `docs/performance/acquisition-evidence-verification.md` and `src/FactoryConnect.Api/Reporting/ReportingTimingInstrumentation.cs`.

V1 repeatedly verified historical retained evidence for reporting and originally queried individual observations during acquisition verification. The V1 A2 design described a 3-publication/771-observation fixture where point-query count would be 1,547 compared with 14 batched verification calls. These are algorithmic fixture expectations in the V1 document, **not V2 benchmarks**. Reuse V1's discipline for integrity, deterministic evidence identity and trace correlation, but not full-prefix traversal or per-machine verification transactions as V2's default hot path.

## V2 design rules

- **Simple path:** current roster = authorized bounded SQL projection, not aggregation-history reconstruction; no implicit V1 cross-process recovery logic.
- **Fast path:** bounded set-based reads with exact predicates and stable order. Avoid one database connection per machine and N+1 SQL. The site's protected inventory query performs one data command; authorization itself issues additional commands which must be measured separately.
- **Debuggable path:** use structured, metadata-only `ActivitySource` spans and correlation IDs; capture duration, rows, known query count and completion status, without SQL payloads, tokens, machine identities or secrets.
- **Extendable path:** separate identity inventory, G3 configuration eligibility, acquisition adapters and incremental metric projections. One component owns one authority; failures identify their stage.
- **Concurrency:** a short-lived protected read shares the G2 security gate, which serializes against revocation. Future scalability work must benchmark the gate; never replace it with `NOLOCK` or unprotected cached permissions.
- **Performance acceptance:** at 100 machines, test distinct 1/10/100-machine sites, concurrent reader/writer flows, SQL logical reads, command counts, connection pool wait, p50/p95/p99 and cancellation before claiming throughput. Do not infer 100-machine readiness from a green correctness suite.

## Current increment

The internal protected inventory reader now emits the `FactoryConnect.V2.Inventory` activity `inventory.read-site`, with `inventory.result`, `inventory.rows`, `inventory.reader.commands` and `inventory.elapsed.ms`. Activity creation is observational; there is no mandatory tracing infrastructure, public endpoint, factory data or new database schema. A data-command count of one does not mean one SQL command for the entire authority transaction.

Follow-up: supply a bounded 100-machine synthetic SQL acceptance runner with repeatable baseline timings and no credential leakage, then build the trusted G3 active-roster SQL projection. Keep the SQL reader separate from the React display and acquisition workers.
