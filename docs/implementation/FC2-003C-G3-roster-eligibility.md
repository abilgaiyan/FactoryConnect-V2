# FC2-003C — G3 active-roster eligibility precondition

Status: **implementation increment, not a G3 design freeze**.

The pure `RosterEligibility` evaluator consumes a coherent, authorized, installation-scoped authority cut; it does not manufacture or verify one. Its `*Verified` inputs are **assertions by a future trusted reader**, not client-controlled flags. Never bind this DTO directly to HTTP input or expose the evaluator as a public roster endpoint.

- Missing authority, missing permission, unknown revocation or unverified runtime assignment is **Unavailable**, not an empty roster.
- Retired machine/site, disabled/suspended, missing exact acknowledgement, revoked policy or credential, or an ineffective package is **Excluded**.
- Effective intervals are half-open: `[EffectiveFrom, EffectiveUntil)`; a null upper bound means open-ended.
- Eligibility establishes an acknowledged, effective configuration, **not** that the machine is connected, producing, or reporting.
- Commissioning discovery is a separate permissioned read; it must not appear in the active roster merely because a machine identity exists.
- The future SQL reader must use a shared authorization gate across the protected read, authoritative revocation checks and a consistent machine/policy cut; a recorded timestamp is not commit visibility.
- Empty eligible results may be labelled **healthy-empty** only if the source cut and permission are proven and the query completed successfully.
- Runtime assignment, policy revocation and exact acknowledgement are not yet supplied by a trusted SQL authority. Until then, the V2 API continues to return its existing unavailable/empty-shell responses.

Acceptance: pure fixtures cover all exclusions, missing proofs and exact effective-time boundaries. Follow-up integration must demonstrate protected read vs revocation commit order, stale projection rejection, runtime credential generation and source/capability filtering in disposable SQL before HTTP publication.

No migration, credential, runtime connection, V1 evidence import or production change is part of this increment.
