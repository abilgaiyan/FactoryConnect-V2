# FC2 G3 — Durable per-machine activation requests

Baseline: `main` `2354332` after authenticated submission/review PR #20. This increment records pending activation intent, revokes that exact intent and retrieves its private operation receipt. It implements no runtime acknowledgement, active roster, dashboard route, runtime connection or deployment.

## Exact binding and ownership

`RequestActivationAsync` requires current `configuration.activate` authority for the exact site/machine. Package authorship, review permission and security administration do not imply it. Machine-only grants work without site-wide activation permission. The command pins approved revision ID + SHA-256, exact approval ID, machine, runtime principal ID, assignment ID/generation, credential generation and effective interval. SQL composite foreign keys independently enforce package machine scope, approved decision/hash and generation identity. Missing or conflicting facts deny the operation; no nearest timestamp or default generation exists.

Runtime identity, assignment generation, credential generation and activation generation are distinct. Current assignment and credential pointers must match the requested immutable rows. An old credential or assignment generation cannot be silently rebound, even when package bytes match. Runtime/human principal kinds are separated by a SQL foreign key and caller session validation. All externally reusable generation values in fingerprints/results use invariant decimal strings.

## Intent lifecycle and intervals

There is initially one current **request head** per machine. First creation expects activation generation zero and no predecessor. Every replacement explicitly names the exact head and expected generation; the next generation increments once under the machine gate. This conservative slice does not schedule multiple independent pending candidates per machine. Overlapping candidate intervals are permitted only as that explicit replacement; the previous request remains immutable history.

The head means latest requested intent, never active configuration. Replacing or revoking an intent does not publish a configuration, transfer an acknowledgement, stop a runtime or rewrite the last successfully acknowledged state. A future acknowledgement writer must separately enforce current request/generation, trusted runtime proof and current eligibility; a future active-configuration reader must preserve eligible fallback when a replacement is pending/failed. No such writer/projection is supplied here.

Intervals are explicit, half-open and optionally open-ended, within both the immutable package machine interval and recorded assignment interval. The assignment must be current/effective when requesting. Credential validity must cover current creation and the first intended use; a future start at/after known credential expiry rejects. Open-ended intent does not extend credential validity. Already ended intervals reject. A past requested start is not proof that the runtime applied anything in the past: application still requires a future authenticated acknowledgement and its own effective-time rules.

Committed safety revocations are checked directly for the exact pinned timezone/schedule/break revisions and hashes, not inferred from a request head. Known scheduled safety revocation at/before first intended use rejects; later scheduled revocation does not silently extend future eligibility. Historical receipts and records remain retained.

## Runtime prerequisite boundary

Migration 010 defines immutable runtime identity/credential/assignment facts, current generation pointers, prerequisite revocations and activation requests/intent revocations. References to security operations establish durable provenance links. These runtime tables are an interface for the still-pending G2 enrollment/assignment producers, **not a runtime provisioning implementation or proof of mTLS trust**. No enrollment, credential rotation, assignment write, policy-revocation or approval-withdrawal API is added. The application seeds none of them. Without independently provisioned committed facts, requests fail closed.

SQL acceptance explicitly seeds synthetic upstream facts using privileged fixtures. Their digest values are not real certificates. Production producers must validate identity/certificate trust and permissions, append audited events, advance expected generation pointers atomically and take the common gates. In particular, installation-wide credential changes must take the security gate so requests for all assigned machines serialize against them; a single machine lock cannot qualify global credential rotation. Fixture-maintenance tests of committed rows do not close that G2 producer acceptance gate.

## Transactions, expiry and retries

Order: operation-ID lock → exclusive installation security gate → preliminary session/permission/current-version checks → exact package/approval load → canonical policy locks in ordinal order → target machine lock → final SQL UTC/session/permission/epoch revalidation → current prerequisites/revocations/interval/head checks → request + head + operation + audit + epoch → commit. The common `ConfigurationLockResources` keys remain unchanged. Only the selected machine's pins and machine gate are acquired; a 100-machine package does not cause 100 machine-lock SQL commands. Failed lock/cancellation/audit/SQL operations roll back the entire effect.

One captured final server UTC instant drives eligibility predicates after waiting for all gates. Permission revocation and session/grant expiry cannot be bypassed by an initially admitted request. This security epoch orders requests with current G2 changes; it is not a cross-authority publication revision.

Exact concurrent retries return one pending receipt. Expected security epoch, expected prior activation generation and exact predecessor protect competing updates. Completed retries reauthorize the original actor and compare immutable request bytes before returning; they never move a newer head backwards, un-revoke an intent or transfer acknowledgement. A new credential generation requires a new explicit replacement operation. `ReadActivationOperationAsync` checks current activation permission and original actor.

`RevokeActivationRequestAsync` records an append-only revocation of one exact intent under current machine-scoped activation authority and the machine gate. Revoking an old intent cannot revoke a replacement head. The result explicitly says it is **not runtime stop**. Future acknowledgement and roster code must check this authoritative revocation table.

## Acceptance and performance

Disposable SQL fixtures use the real submitted-package and independent-review commands. They verify exact approval/hash/scope, no implicit administrator activation, machine-only access, independent generations, missing/revoked prerequisites, retirement, interval/expiry rules, scheduled policy revocations, monotonic head replacement, concurrent winner/retries, immutable history and audit rollback. A real G2 grant-revocation barrier makes a queued activation request wait, then deny after commit. A machine-gate maintenance fixture separately tests fresh prerequisite-row visibility; it does not certify runtime enrollment writers. A SQL-clock test expires a waiting session before the machine gate is released.

A measured request selecting one machine from a 100-machine package must have the same full-path SQL roundtrip count as a single-machine package, at most 24, exactly four configuration locks when all three shared policy types are distinct, and below five seconds in the CI smoke fixture. Existing 100-machine inventory p95/p99 gates continue to run. These are pending-intent SQL regression budgets, not runtime activation throughput, HTTP or production SLAs. No full activation history or raw observations are replayed for request creation; current pointers have keyed lookups.

Remaining gates: trusted runtime producers and acknowledgement, disabled/suspended machine authority, approval/policy mutation producers, overlap/acknowledged-state reconciliation, coherent roster cuts and HTTP/dashboard integration. Requests are necessary intent records, not sufficient eligibility proof. V1 evidence and production remain unchanged.
