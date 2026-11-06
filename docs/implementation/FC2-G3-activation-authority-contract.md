# FC2 G3 — Activation authority contract v0.2

**Status: proposed implementation boundary; not a frozen security contract or an active roster.**

G1 configuration packages and machine IDs remain authoritative; G2 owns human session/grant revocation and protected reads. G3 adds an independently verifiable relationship between an approved package revision, a machine assignment, a runtime identity **generation**, an effective interval and the runtime's exact acknowledgement. Approval does not mean activation; activation does not mean connectivity.

## Minimum durable facts

| Fact | Required identity | Rule |
| --- | --- | --- |
| Configuration approval | immutable package revision ID **and canonical hash**, site, machine scope, author/submitter, reviewer and review operation ID | A submitter/author cannot approve their own revision; no implicit grant to activate |
| Activation request | activation ID/generation, package revision/hash, machine ID, runtime identity ID, assignment ID/generation, credential generation, effective interval, expected authority versions, operation ID | Append-only intent; exact revision and machine scope; no unbounded implicit activation |
| Runtime acknowledgement | activation ID/generation, exact package revision/hash, machine, runtime identity ID, assignment ID/generation, credential generation, acknowledgement ID and authenticated runtime evidence | Acknowledgement must be bound to the exact activation; cannot be inferred from a healthy adapter or timestamp |
| Revocation/disablement | exact activation or assignment/generation, reason, actor, operation ID, committed epoch | Revocation is append-only and serialized against protected readers |
| Eligibility projection | one verified cut of machine/site retirement, approval, activation, assignment, credential revocation, policy revocation, effective interval | Missing or conflicting authority yields `Unavailable`, not an empty eligible set |

**Generation identities are distinct.** A stable runtime identity, machine-assignment generation, credential generation and per-machine activation generation are separate authorities. Each acknowledgement pins all of them. Reusing a runtime ID after credential rotation cannot make an old acknowledgement valid. An acknowledgement from an earlier credential/assignment generation is ineligible, even when the package bytes are unchanged. Duplicate conflicting acknowledgements and an acknowledgement for a superseded activation reject; no timestamp-based fallback resolves them.

Pending activation requests survive credential rotation only through explicit audited rebinding or replacement, with a new immutable operation identity and expected prior version. Never silently accept another credential generation. Rebinding does not transfer a previous acknowledgement or bypass approval, effective-time, hash or policy eligibility.

## Transaction and consistency rules

1. Validate authenticated actor and exact permission under G2 authority. Use the existing human `configuration.review`, `configuration.activate`, `configuration.read` and security-management permissions. Runtime acknowledgement is a separate authenticated runtime capability; `runtime.acknowledge` is not an existing human G2 grant and must not be added to the human permission set as an impersonation route. No administrator superuser bypass.
2. For mutation: take the operation-ID lock before the security gate, then policy-revision locks before machine locks, preserving the implemented G2 and frozen G1 relative order. Every additional activation gate must have one documented position and key/mode shared by all affected readers/writers before DDL; prohibit lock upgrades and reverse acquisition. Bind operation ID to canonical request fingerprint; check expected authority versions and current approval/assignment; append immutable facts and audit; atomically commit one result. Retry with identical request returns the recorded result only under current retrieval authority; conflicting replay rejects. Registration does not preserve permission after revocation. A stored result never re-applies or reactivates its historical effect.
3. For protected roster read: acquire shared security/activation gate(s) in a canonical order, validate authorization, read a coherent policy/runtime/acknowledgement cut, fully materialize and commit before releasing. Never substitute `NOLOCK` or cached authorization.
4. Acknowledgement may be recorded only from authenticated, generation-bound runtime authority; a human session cannot impersonate a runtime. Runtime certificate/mTLS provisioning remains a separate G2 deliverable, so the acknowledgement writer cannot be exposed before that proof exists.
5. Partial activation is explicit per machine. A mixed result must not be collapsed into all-active or healthy-empty. Use half-open effective intervals `[from,until)`, optionally open-ended; evaluation time is a single server-generated UTC instant captured after the required gates are acquired. Pending/failed replacement retains the last acknowledged configuration only while its interval, policy, runtime assignment/credential and disablement/revocation rules remain valid. Overlapping active/pending intervals require explicit supersession of the exact prior activation and expected version; approval alone reserves no interval.
6. A projection must not assume completeness from a wall-clock timestamp. A verified transaction boundary and the applicable committed security, policy, assignment, credential and activation versions are required. A security epoch alone is not a cross-authority publication revision. Missing revision, ambiguous assignment, revocation gap or conflicting acknowledgement fails closed.

7. Check authoritative safety-critical revocations directly for every pinned timezone/schedule/break revision/hash, even when the activation projection is stale. Immediate revocation applies upon commit visibility; scheduled revocation applies from the later of its scheduled instant and commit visibility. Database ineligibility and runtime stop notification/acknowledgement remain separate facts.
8. Commissioning discovery is a separate permissioned read of approved pending assignments; it cannot depend on the already-active roster. Resolve capabilities from the approved package and exact acknowledgement; configured capability intent is not proof of available metric inputs or live production. The `*Verified` inputs to the pure evaluator are trusted-reader assertions, never client-controlled proof flags.

## Performance and debugging contract

- Index the current activation/assignment lookups by site and machine, with exact revision/generation joins. Do not reconstruct all historical activation or raw observation rows for a roster.
- Materialize one site slice with set-based SQL, not one SQL transaction or verification query per machine.
- Bound the amount of returned data and report explicit source completeness; measure total SQL roundtrips, logical reads, security/activation gate wait and p95/p99 at 100 machines and eight concurrent readers.
- Emit metadata-only stages: authorization, gate wait, activation cut, projection rows and outcome. No session tokens, certificate material, machine observations or SQL payloads in logs.
- Keep acquisition and OEE reporting independent of roster reads; an offline adapter must not block other machine projections.

## Explicitly not implemented in this slice

No SQL activation schema, mutation API, runtime credential issuance, mTLS, HTTP roster, connectivity indicator, migration or production change is authorized by this document. The existing pure `RosterEligibility` remains fail-closed. The next implementation should begin with append-only activation/acknowledgement storage and disposable SQL acceptance for exact-generation binding and read-vs-revocation ordering. Only then should the trusted roster reader be built.

## Acceptance before G3 publication

- Exact revision/hash and distinct assignment/credential/activation generation matching; half-open/open-ended effective dates, site ownership and author/submitter self-review denial.
- One committed effect per operation ID; identical retry, conflicting retry, stale expected epoch and audit failure rollback.
- Runtime rotation/revocation races; explicit pending-request rebinding; stale/superseded/conflicting acknowledgements; disabled/suspended/retired identities and missing/contradictory facts.
- Bounded retain-last-successful, expired/revoked fallback denial, interval-overlap/supersession conflicts, stale activation projections against authoritative policy revocation and independent commissioning discovery.
- Named gate resources, modes and deterministic ordering for every activation, policy/credential/assignment revocation and protected-read path; both commit orders and failed-lock rollback in real SQL. No claim that these locks or authority stores exist yet.
- 100-machine synthetic site: set-based read and query budget, concurrent revocation/read, measured tail latency; healthy-empty only after verified complete authority cut.
