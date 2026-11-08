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

## Executable hosting mechanics — qualified, operational quiet-source gate open

`0260e60f` passed [CI #42453004167](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42453004167):
all three jobs, 225 dedicated accepted-host checks, 245 history checks, 26 acquisition
host checks and 1,152 full SQL checks without skips. Local Release build had zero
warnings/errors; 142 pure checks passed. The hosting fixture includes a fresh
bootstrap plus seven continuation/negative child lifetimes. These are controlled
mechanics, not unattended machine-service or production commissioning acceptance.

Discovery profiles at deep history reported 16 observation rows read across three
calls: each predecessor access read one row and each populated next-signal probe
read two. SQL omitted the empty seek's ActualRowsRead counter; it remains null in
the artifact. The empty seek had four logical reads, versus three for each populated
probe. The regression checks that relationship explicitly rather than inventing a
zero row-read count. Command-level logical reads totaled 4,449 in this fixture.
These workload observations do not establish fleet capacity or production SLOs.

**Open operating gate:** long quiet source windows. A later state transition after
more than 100 retained acquisition batches can exceed the existing source-proof
window even when all intervening batches contain only counter observations. The
host must hold without changing checkpoint, periods or receipts. An additional
101-quiet-batch regression exercises that behavior through actual ingestion and
the executable. Its current-head qualification is tracked in PR #36.

Do not classify this limit as qualified sustained processing or move directly to
quantities. Verified empty-source advancement is the next work item, before
unattended activity hosting is considered complete. PR #36 remains draft while
that operational acceptance boundary is open.

## Next narrow contract — verified no-state frontier advancement (review candidate)

This proposal is not implemented by the current host and does not weaken the
existing nonempty source-cut contract:

1. A no-state cut requires bounded verified retained documents for an exact
   acknowledged machine/stream/source instance. Every document must be parsed and
   prove that the selected sequence window contains zero observations for the
   exact activity source address. Unknown/unavailable/unmapped state values at
   that address are observations, never an empty window.
2. Preserve exact batch IDs, hashes, scope, acknowledgement and source bounds in
   a versioned proof. The writer must revalidate that complete cut in its own
   transaction under existing lock order, current authority and the durable
   acquisition frontier. Missing/altered documents or projected omissions deny
   advancement; SQL metadata absence alone is insufficient.
3. Atomically commit an immutable no-state operation receipt and exact source
   frontier, producing zero periods. Caught-up polls remain read-only. Never
   derive running time or physical completeness from an empty cut.
4. Before the first activity sample, represent absence explicitly; do not invent
   a last observation, anchor or Unknown state. Preserve installation/site,
   machine/mapping/qualification/acknowledgement binding and immutable ancestry.
   Authenticated history remains no-published-history until actual observations
   support a publication; a processor frontier is not an activity interval.
5. After observations exist, retain their original identities. Empty progress
   must preserve or conservatively break anchor continuity; it never proves a
   cross-batch link. New observed source timestamps must still satisfy the exact
   half-open interpretation window; no historical requalification is implied.
6. Existing immutable receipt replay must remain compatible. Version new request
   semantics rather than changing historical request bytes. Reject fabricated
   checkpoints, omitted observations, scope changes and ambiguous retries.
7. Qualify quiet bootstrap, more than 100 quiet batches, later state observations,
   pre/post-commit process termination, exact replay, projection/document
   corruption, expiry/revocation ordering and bounded repeated/deep-history cost.

The original source-proof bounds remain active until this contract has executable
acceptance. No production setup, migration execution or recovery is authorized by
this proposal.
