# FC2 G3 — Activation authority contract v0.1

**Status: proposed implementation boundary; not a frozen security contract or an active roster.**

G1 configuration packages and machine IDs remain authoritative; G2 owns human session/grant revocation and protected reads. G3 adds an independently verifiable relationship between an approved package revision, a machine assignment, a runtime identity **generation**, an effective interval and the runtime's exact acknowledgement. Approval does not mean activation; activation does not mean connectivity.

## Minimum durable facts

| Fact | Required identity | Rule |
| --- | --- | --- |
| Configuration approval | immutable package revision ID, site, machine scope, approver and approval operation ID | A submitter/author cannot approve their own revision; no implicit grant to activate |
| Activation request | activation ID, package revision, machine ID, runtime assignment ID/generation, effective interval, expected authority epoch, operation ID | Append-only intent; exact revision and machine scope; no unbounded implicit activation |
| Runtime acknowledgement | activation ID, exact package revision, machine, runtime identity/generation, acknowledgement ID and authenticated runtime evidence | Acknowledgement must be bound to the exact activation; cannot be inferred from a healthy adapter or timestamp |
| Revocation/disablement | exact activation or assignment/generation, reason, actor, operation ID, committed epoch | Revocation is append-only and serialized against protected readers |
| Eligibility projection | one verified cut of machine/site retirement, approval, activation, assignment, credential revocation, policy revocation, effective interval | Missing or conflicting authority yields `Unavailable`, not an empty eligible set |

**Runtime identity generation is mandatory.** Reusing a runtime ID after credential rotation must not make an old acknowledgement valid. An acknowledgement from an earlier generation is ineligible, even when the package bytes are unchanged.

## Transaction and consistency rules

1. Validate authenticated actor and exact permission under G2 authority. Separate `configuration.approve`, `configuration.activate`, `runtime.acknowledge`, `configuration.read` and security grant permissions. No administrator superuser bypass.
2. For mutation: acquire the appropriate exclusive authority gate; bind operation ID to canonical request fingerprint; check expected authority epoch and current approval/assignment; append immutable facts and audit; atomically commit one result. Retry with identical request returns the recorded result; conflicting replay rejects.
3. For protected roster read: acquire shared security/activation gate(s) in a canonical order, validate authorization, read a coherent policy/runtime/acknowledgement cut, fully materialize and commit before releasing. Never substitute `NOLOCK` or cached authorization.
4. Acknowledgement may be recorded only from authenticated, generation-bound runtime authority; a human session cannot impersonate a runtime. Runtime certificate/mTLS provisioning remains a separate G2 deliverable, so the acknowledgement writer cannot be exposed before that proof exists.
5. Partial activation is explicit per machine. A mixed result must not be collapsed into all-active or healthy-empty. Use half-open effective intervals `[from,until)`; evaluation time is a single captured UTC instant per cut.
6. A projection must not assume completeness from a wall-clock timestamp. A verified transaction boundary and committed authority epoch are required. Missing revision, ambiguous assignment, revocation gap or conflicting acknowledgement fails closed.

## Performance and debugging contract

- Index the current activation/assignment lookups by site and machine, with exact revision/generation joins. Do not reconstruct all historical activation or raw observation rows for a roster.
- Materialize one site slice with set-based SQL, not one SQL transaction or verification query per machine.
- Bound the amount of returned data and report explicit source completeness; measure total SQL roundtrips, logical reads, security/activation gate wait and p95/p99 at 100 machines and eight concurrent readers.
- Emit metadata-only stages: authorization, gate wait, activation cut, projection rows and outcome. No session tokens, certificate material, machine observations or SQL payloads in logs.
- Keep acquisition and OEE reporting independent of roster reads; an offline adapter must not block other machine projections.

## Explicitly not implemented in this slice

No SQL activation schema, mutation API, runtime credential issuance, mTLS, HTTP roster, connectivity indicator, migration or production change is authorized by this document. The existing pure `RosterEligibility` remains fail-closed. The next implementation should begin with append-only activation/acknowledgement storage and disposable SQL acceptance for exact-generation binding and read-vs-revocation ordering. Only then should the trusted roster reader be built.

## Acceptance before G3 publication

- Exact revision and runtime-generation matching, half-open effective dates, site ownership and approval/self-approval denial.
- One committed effect per operation ID; identical retry, conflicting retry, stale expected epoch and audit failure rollback.
- Runtime rotation/revocation races, disabled/suspended/retired identities, missing or contradictory facts.
- 100-machine synthetic site: set-based read and query budget, concurrent revocation/read, measured tail latency; healthy-empty only after verified complete authority cut.
