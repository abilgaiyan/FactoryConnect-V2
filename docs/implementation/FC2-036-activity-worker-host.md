# FC2-036 — Explicit single-machine activity worker host

Executable qualification: `0836f324` passed [CI #42457523666](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42457523666).
Final integration/merge disposition is tracked in [PR #36](https://github.com/abilgaiyan/FactoryConnect-V2/pull/36).
This is controlled retained-source hosting qualification, not production commissioning.

## Boundary and configuration

`FactoryConnect.ActivityHost` is a separately launched console process over retained
accepted MTConnect evidence. It does not acquire data, apply migrations, provision
identities, start from HTTP or register automatically in the API/acquisition host.
One exact installation/site/machine/mapping/stream/source-instance scope is configured.
Customer service credentials, installation, recovery and commissioning are separate.

Run the Release tool with `--config path` or `--stdin` (one JSON line). Actual input
is bounded to 32 KiB; unknown fields are rejected. Secrets are not command-line
arguments or diagnostics. Subsequent stdin lines may supply `stop`.

```json
{
  "version": 1,
  "installationId": "<existing installation GUID>",
  "siteId": "<existing site GUID>",
  "machineId": "<existing machine GUID>",
  "mappingId": "<explicit approved activity mapping GUID>",
  "streamKey": "mtconnect:uuid:<exact device UUID>",
  "instanceId": "<canonical UInt64 decimal string>",
  "bootstrapAfterSequence": "<canonical UInt64 decimal string>",
  "sqlConnectionString": "<explicit existing V2 database, not a system database>",
  "cutSize": 100,
  "pollIntervalMilliseconds": 250,
  "operationTimeoutSeconds": 15
}
```

Cut size is 1–100, poll interval 50–60,000 ms and deadline 1–30 seconds. Bootstrap
must equal the first acknowledged retained batch's FromSequence minus one; sequence
zero bootstrap remains unsupported. After a checkpoint exists its exact frontier
governs resume. Bootstrap is not a reset or timestamp-search command. Source-instance
or mapping changes hold for review rather than being automatically adopted.

The loop has one in-flight operation and no queue/prefetch. Ctrl+C, SIGTERM or `stop`
cancels it. Unsafe/unavailable/ambiguous operations exit held (20), reporting stage,
operation ID and SQL error number without source values/credentials. The worker
never guesses whether a SQL commit happened; restart reconstructs SQL truth.

## Discovery, ancestry and publication

Discovery joins security Shared → machine Shared → processor Shared ordering. The
existing ingestion machine Exclusive gate serializes accepted-frontier changes.
Current approved mapping authority is resolved after waits. Checksum, qualification,
generation, producing receipt, original predecessor/anchor and retained frontier
witness are checked even when caught up. Checkpoint/receipt payloads have explicit
8 MiB preflights. Migration 020 adds an exact-signal metadata index and a unique
scope/generation ledger index; a genuine older receipt cannot rewind the checkpoint.

Each candidate pins exact scope, qualification, instance, generation and after/through
cut. Its versioned operation identity is deterministic across process lifetimes.
Discovery is not publication permission: `ApplyAcceptedWorkAsync` delegates nonempty
work to the unchanged version-2 request contract in `ApplyAcceptedAsync`. The original
writer-owned transaction revalidates current authority, interval, complete accepted
source cut and predecessor before atomic checkpoint/receipt/period commit. Historical
receipt replay may return an older original result without replacing newer progress.

Caught-up polls write nothing. Pending source windows are bounded to 100 batches and
4 MiB of document-plus-metadata budget before proof reading; the existing 8 MiB source
verification limit remains active. Exact-signal sequence seeks avoid traversing an
unrelated raw prefix. Work and protected reads remain bounded by the selected new cut
and necessary predecessor/witness, not cumulative history.

## Verified no-state advancement

Quiet source windows require evidence, not metadata absence alone. The source reader
hashes and parses every selected retained document, binds exact scope/header values
and checks projected observation completeness. A no-state cut must contain zero
observations for the exact activity address. Unknown/unavailable/unknown-value samples
at that address are observations and cannot be skipped as empty.

Accepted header coverage must cover the complete cut without gaps, use the exact
current acknowledgement and end at a verified batch frontier. The writer revalidates
the complete batch-ID set, header/hash proof (including raw SHA-256), projected absence
and durable acquisition cursor in its own transaction after lock waits. Appended or
altered overlapping evidence invalidates a stale proof. No per-document SQL client
loop or historical-prefix reconstruction is introduced.

A successful version-3 no-state operation atomically commits an immutable receipt and
source frontier, zero periods and a conservative anchor break. It retains the original
last observed sample when one exists. Before the first sample, LastObservation and
LastConsumedSample remain null; site/scope, qualification and a retained frontier
witness are explicit. The history API reports no-published-history until actual
observations support a series. No Unknown state, running time or connectivity is
invented. A later real sample starts its own anchor under the existing same-batch
policy. Nonempty samples still satisfy the exact half-open interpretation window.

Restart validates both immutable producing ancestry and the exact retained frontier
document. Historical no-state replay returns its original receipt without rewinding
newer progress. Older nonempty request fingerprints and pre-frontier JSON receipts
remain replay-compatible; their request bytes are not rewritten.

## Acceptance evidence

At `0836f324`, all three CI jobs passed:

| Suite | Result |
| --- | --- |
| Full SQL regression | 1,152 checks, no skips |
| Dedicated activity host | 233 checks |
| Quiet bootstrap | 91 checks |
| Deep-history processing/discovery/host | 291 checks |
| Acquisition host | 26 checks |
| Dashboard | 479 tests |

Local Release builds had zero warnings/errors; 142 pure checks passed. Actual
MTConnect HTTP/mTLS ingestion and generated disposable SQL databases feed the shipped
host executable through stdin. Acceptance includes fresh bootstrap, serial bounded
cuts, observed SQL-barrier pre-commit termination, same-batch anchor restart, later
input, cross-batch breaks, graceful/idle restart, missing predecessor, forged ancestry,
checkpoint rewind, exact legacy replay and authorized publication HTTP readback.

Quiet bootstrap crosses 101 no-state batches before any sample and emits no periods.
A fresh process then preserves the first real anchor across repeated state and closes
one exact interval. A currently eligible no-state cut with no prior checkpoint passes
preflight, waits on a held processor lock, expires and rejects with no effects. A
projection altered after preflight is also rejected inside the writer transaction.

After observed progress, 101 quiet batches commit as two bounded proof-bearing cuts.
Historical empty replay does not rewind the second cut. An altered frontier document
fails even when its SQL hash is updated consistently. Later observations produce only
their own exact qualified interval. The entire hosting fixture repeats at deep history.

The final integration additionally exercises termination after no-state checkpoint/
receipt insertion and before commit, verifies full rollback, then independently
observes committed no-state progress before abrupt termination and process restart.
Its current-head qualification is recorded in PR #36.

Deep discovery profiled three calls. The accepted run reported 22 observation rows
read and no unavailable row counters, with 3,632 command logical reads. Each populated
next-signal probe read two rows; individual predecessor accesses stayed bounded.
Plans may choose the new signal index for predecessor checks too, so the regression
identifies the two-row next-input probes without assuming one fixed join plan. Earlier
empty seeks omitted ActualRowsRead; raw null counters are preserved and only qualified
with measured logical-read bounds, never fabricated as zero. Artifacts retain actual
plans, statements and command totals. These are controlled single-machine observations,
not fleet capacity, statistical latency comparisons or production SLOs.

## Remaining boundaries

No-state proof establishes absence in the accepted source cut, not physical sensor
completeness. Same verified batch continuity remains qualified; unproven cross-batch
links remain breaks. Stronger retained-source cross-batch acquisition assurance must
be qualified before presenting sustained activity across those boundaries. No software
receipt alone proves physical completeness.

Unprovable overlaps, missing coverage, oversized payloads, source/mapping changes and
authority expiry hold. Automatic historical requalification, source-instance recovery,
fleet scheduling, service installation/rollback and production commissioning remain
separate. Quantities and shift/OEE inputs retain their own evidence contracts. No
production setup, migration execution or recovery is performed by this PR.
