# G3 machine enable/suspend authority

This increment adds explicit machine operational permission state: `unknown`, `enabled`, or `suspended`. It controls configuration/runtime eligibility, not physical machine power or emergency-stop hardware. No production listener, control notification, runtime HTTP endpoint or active roster is enabled. Existing master IDs remain authoritative.

## Permission and approval boundaries

`ProposeMachineEnableAsync` requires current exact-scoped `machine.enable`, expected state ID/generation, security epoch, explicit reason and a bounded expiry. A proposal changes no operational state. `ReadMachineEnableProposalAsync` lets another currently authorized enabler inspect the exact scoped proposal, proposer, expected/current generations, expiry and original reason before deciding; it never returns a session hash. No proposal queue endpoint is introduced.

`ApproveMachineEnableAsync` requires a different authenticated principal with current `machine.enable` for the same machine. Every initial enable and every lifting of suspension uses this independent approval profile; there is no caller-asserted non-safety-critical shortcut. The proposer’s original session and current complete grant ancestry must still be valid at execution. Revocation or expiry while awaiting approval denies execution; the proposal is not a permission lease. Retired machine/site identities cannot be enabled. Proposals expire within fifteen minutes and pin the exact prior state, so any newer suspension invalidates them.

`SuspendMachineAsync` requires only current `machine.suspend`, exact state/epoch and reason. It acts immediately at commit and may hold an unknown or already suspended machine; a new hold advances the generation and invalidates pending enable proposals. It does not require a package, a working runtime connection, approval from the enabler or a policy decision. The receipt explicitly does not claim that an offline runtime stopped. Suspension and enable permissions imply neither one another nor editing, reviewing or activation privileges. Bootstrap administration alone supplies no use permission.

`ReadMachineOperationalStateAsync` requires scoped `commissioning.read`. Private operation receipts require the original actor and the current action permission. Completed retries return original receipts without changing any state: replaying an old successful enable after suspension cannot re-enable the machine. Permission changes do not rewrite already committed history; a new transition requires new current authorization.

## Durable state and safe resumption

Migration 015 introduces immutable enable proposals, original proposing-session identity, independently approved/suspended transitions and one exact current pointer. SQL constraints enforce authoritative machine/site, human principal kinds, distinct enable proposer/approver, exact actor-operation provenance, same-machine predecessor links and one enable transition per proposal. There is no seeded enabled state. A missing pointer with no history is `unknown`; missing pointer with retained history fails closed instead of being silently adopted or reconstructed by timestamp.

Each committed suspension or enable advances a separate operational generation. New activation requests pin the current enabled generation. Runtime authority and acknowledgements require current enabled state; acknowledgement fingerprints, stored rows and composite foreign keys bind the same operational generation. Migration defaults existing activation/acknowledgement rows to **zero/unverified**, never to a newly inferred enablement. Legacy or stale operating-generation receipts cannot establish current runtime authority.

Re-enabling returns `enabled-not-activated`. It changes no package approval, assignment, credential, activation request or acknowledgement. Old intent and acknowledgement generations remain invalid after a suspension/resume cycle, even if all package bytes match. A controller must issue a new explicit activation request and the runtime must acknowledge that exact request under the new enabled generation. Current package, policy, time, retirement and other generation eligibility are rechecked by those existing activation/acknowledgement paths; enabling the administrative gate cannot restore any prior configuration automatically. A last acknowledgement pointer remains historical, not an active-roster verdict.

## Ordering, audit and performance

Human mutations acquire operation ID → exclusive installation security gate → exclusive common machine gate. They make no policy decision and need no policy lock. Current SQL time, both applicable human authorization closures, expected epoch and exact state are checked after waiting. Existing activation/acknowledgement writers retain policy-before-machine ordering. Protected runtime reads take the compatible shared gates and validate enabled state in their indexed exact authority query.

Operation, proposal/transition, pointer, audit and epoch commit atomically. Audit failure after state insertion rolls back every effect. Read-first may finish at its held enabled cut before suspension can commit; suspension-first denies queued runtime work after commit. SQL application-lock observations prove both relevant orders. Physical stop notification/acknowledgement is separate work, not inferred from database ineligibility.

Lookups use current/exact keys and bounded existence checks. No full history reconstruction, raw observation scan, per-machine protected read loop, network work under SQL gates or timeout increase is introduced. Activation creation adds one explicit indexed operational-state query; its constant one-/100-machine SQL budget is updated for that required authority check. Runtime operational checks share the existing one exact authority query. Existing 100-machine package/activation/acknowledgement and inventory regressions remain required; synthetic CI is not production capacity evidence.

## Executable evidence and remaining gates

Dedicated disposable SQL tests use actual signed bootstrap, human enrollment/grants, two distinct operators, proposal inspection, real certificate enrollment, assignment, mTLS and package review. They prove unknown-state denial, no superuser bypass, permission separation/scope, self-approval denial, original-session and proposer-grant revocation, expiry during an observed machine wait, exact retries, post-insert enable/suspension audit rollback, immutable history, missing-head integrity, explicit unknown holds, stale proposal invalidation, both suspension/runtime ordering cases and no acknowledgement revival after resume.

Older feature suites explicitly seed labelled synthetic enabled upstream facts in one set-based fixture command, preserving their independent purpose; those rows are not claimed as proof of the operational approval workflow. The dedicated suite supplies that producer proof. No fixture enables a production identity.

Full roster eligibility, policy/approval revocation mutation commands, protected commissioning discovery, HTTP composition and operational deployment remain separate acceptance gates. This increment does not authorize production changes or certify industrial safety control.
