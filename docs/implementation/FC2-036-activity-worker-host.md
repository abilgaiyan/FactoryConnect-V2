# FC2-036 — Explicit single-machine activity worker host

Status: implementation candidate; real SQL/process/performance CI qualification pending.
Baseline: PR #35 merged at `164b726e`; authenticated publication history is available.

## Boundary

`FactoryConnect.ActivityHost` is a separate, explicitly launched console process.
It consumes retained accepted MTConnect observations using `ApplyAcceptedAsync`;
it does not acquire machine data, run migrations, provision identities, start from
an HTTP request or register automatically in the API/acquisition host. One exact
installation/site/machine/mapping/stream/source-instance scope is configured.
Customer service credentials, installation and production commissioning are separate.

The worker runs one discovery/apply operation at a time, with no input queue or
prefetch. Each operation has a deadline. Ctrl+C, SIGTERM or `stop` on stdin cancels
work. Any unsafe/unavailable/ambiguous operation exits held (20); it never guesses
whether a SQL commit happened. A new process reconstructs progress from SQL.

## Configuration

Run the Release-built tool with `--config path` or `--stdin` (one JSON line).
Configuration is limited to 32 KiB; unknown fields are rejected. Secrets are not
command-line arguments or diagnostics. `--stdin` leaves subsequent lines available
for `stop`. Example shape, with credentials supplied separately by the installer:

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
  "sqlConnectionString": "<explicit existing V2 database; never a system database>",
  "cutSize": 100,
  "pollIntervalMilliseconds": 250,
  "operationTimeoutSeconds": 15
}
```

Cut size is 1–100; poll interval 50–60,000 ms; deadline 1–30 seconds. Bootstrap is
an explicit retained-window boundary, not a timestamp search. It must equal the
first batch's FromSequence minus one for the acknowledged source instance; the
existing writer rejects sequence-zero bootstrap and later caller-selected starts.
After a checkpoint exists, its exact frontier governs resume; the bootstrap option
is not a recovery/reset command. Source-instance or mapping changes require review,
not automatic adoption. Existing interpretation-window rules remain unchanged.

## Discovery and publication contract

Discovery joins the existing security Shared → machine Shared → processor Shared
lock order, obtains approved mapping authority after the waits and reads the SQL
checkpoint under the same transaction. It verifies checksum, qualification,
generation, immutable producing receipt and retained predecessor/anchor even when
there is no new work. Receipt/checkpoint payloads have explicit 8 MiB preflights.

Migration 020 adds a metadata-only exact-signal index. `TOP(cutSize)` sequences are
selected after the checkpoint and at/below the pinned durable acquisition cursor.
Discovery does not load or reconstruct historical raw documents. It does not filter
away rows with unexpected addresses/acknowledgements to make a cut pass. The existing
accepted-source reader subsequently checks document completeness, projections and
provenance. Sparse tails use the mapping/source index, rather than traversing all
unrelated retained observations.

A candidate pins exact generation, instance, after/through cut and qualification.
Its operation ID is deterministic over that complete scope and a versioned worker
contract. Discovery is not publication authorization: `ApplyAcceptedAsync` rechecks
mapping, source cut and immutable predecessor inside its original atomic writer
transaction. Concurrent changes are rejected or return the original exact receipt;
no checkpoint is replaced with a stale result.

An empty poll writes no checkpoint/receipt or extrapolated interval. It is not a
connectivity or physical-completeness claim. The worker does not skip empty history
by changing its durable frontier. Existing 100-batch / aggregate byte source-proof
bounds still apply: an excessively sparse pending cut holds for review instead of
silently dropping evidence or relaxing proof. This first host does not implement
historical requalification, source-instance recovery, cross-batch continuity or
fleet scheduling.

## Acceptance gates

Dedicated `--activity-host-only` uses a generated disposable SQL database and real
MTConnect HTTP/mTLS ingestion. It exercises the shipped executable through stdin:

- A fresh worker commits the initial acknowledged eligible cut, after the existing
  injected rollback; stored receipt, exact period and operation replay are checked.
- A continuous host resumes SQL state, consumes bounded one-observation cuts and
  idles without extra effects.
- A controlled SQL trigger blocks closing publication after period insertion.
  Actual lock evidence precedes termination. Rollback preserves the original anchor;
  a new process discovers and closes that same-batch interval once.
- Later ingestion is discovered, cross-batch links remain breaks, graceful shutdown
  and post-commit restart preserve checkpoint/receipt/period state.
- Wrong instance/mapping, checksum-consistent forged ancestry and missing retained
  predecessor hold even on idle restart. Synthetic corruption is restored in finally.
- Authorized activity HTTP readback remains consistent with the published results.

The larger-history suite additionally profiles idle and nonempty next-cut discovery
at deep retained history, requiring bounded observation/index access and deterministic
candidate replay. Existing exact-output, processing-memory, crash/restart, API/browser
and complete SQL regressions remain required. CI evidence, measured limits and final
review must be recorded before readiness/merge. No production action is performed.
