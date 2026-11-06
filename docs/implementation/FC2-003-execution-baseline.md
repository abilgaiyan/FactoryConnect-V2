# FC2-003 — Autonomous execution baseline

The user's 6 November 2026 instruction authorizes independent implementation and testing, replacing the earlier blanket implementation hold for development work. It does not supply production endpoints, identity tenants, keys or credentials. No existing production database is an implementation or test target.

G1 remains frozen at `5e170d1`. G2 v0.2 is the working security contract. The following engineering choices settle routine design questions without pretending that customer infrastructure has been provisioned.

## G2 implementation choices

- Use a configurable external OIDC provider with authorization code/PKCE and server-managed sessions. Bind identities by exact issuer plus subject. Do not assume a Gajra Entra tenant exists. Production issuer/tenant/app provisioning is an installation input; absent configuration means sealed access, not a password fallback. Automated authentication fixtures must be unavailable in production mode.
- Use per-runtime mTLS X.509 credentials with an explicit installation trust anchor, runtime identity and generation. Default rotation has no overlap; an explicitly bounded overlap requires an audited operation. Replace pending requests rather than silently rebinding them.
- Bootstrap and recovery require signatures by two independently enrolled custodian keys over the same canonical installation-bound challenge, intended principal, operation ID, expiry and nonce. Provision public keys out of band; private keys stay with custodians. No shipped custodian, default administrator or recovery secret. Actual people/key enrollment remains a deployment input.
- Initial security implementation uses one installation-wide shared/exclusive authorization gate. Protected reads and ordinary operations take shared mode; any security-authority mutation takes exclusive mode at the outset. This deliberately broad gate covers every dependency without dynamic-closure gaps. Split gates only after equivalent behavior is proven by tests.
- Global transaction order: operation-ID lock (mutations) → authorization gate → ordered G1 policy locks → ordered machine locks → entity rows. No upgrades or lower-ranked acquisitions. Security mutations start exclusive; reads do not reserve operation identities. Revalidate current authority before disclosing an idempotent result.
- Parent revocation invalidates descendants; new grants never resurrect old sessions or descendant grants. Permission separation, protected-read boundaries and explicit safety/commissioning permissions follow G2 v0.2.

These choices are implementation direction, not security acceptance evidence. Security write endpoints remain unavailable until bootstrap, session, permission, race and replay tests pass. The additional `security` schema is a separately reviewed G2 extension; it does not change G1 table ownership.

## G3 roster baseline

- `/dashboard/config` contains only machines with acknowledged, currently effective and eligible configuration at a consistent evaluation instant. Master identity alone is insufficient.
- Pending/failed replacements preserve the prior eligible configuration only within its effective period. Revocation, suspension, disablement and retirement prohibit fallback. Never fall back to older historical revisions merely because the current revision became ineligible.
- Commissioning discovery is a separate protected read, independent of active-roster membership.
- Partial activation is evaluated per machine. Unacknowledged machines are excluded from active sources and remain visible in commissioning with their reason.
- Capabilities describe qualified source/configuration support, never successful metric calculation or a guarantee of evidence completeness. Unsupported/unknown inputs must not become fabricated zeros.
- Preserve the existing React decoder contract. SQL unavailable, incomplete schema, unconfigured authority or integrity conflict gives 503 Problem Details; genuinely empty healthy authority may return an empty roster. Scope filtering must not disclose hidden machine existence.
- Real-backend Vite mode must explicitly disable fixtures and proxy the same-origin API/config requests. Fixture mode remains an explicit demonstration mode.

## Executable delivery sequence

1. This PR: first FC2-003A increment — explicit installation identity, transactional migration runner, stable master identities and disposable SQL acceptance harness. No configuration activation, credentials or SQL-backed roster claimed.
2. Configuration package schema and canonicalization fixtures; pinned policy/timezone data; safety revocation and activation concurrency tests.
3. Security authority and sealed bootstrap/session/runtime implementation against G2 acceptance fixtures.
4. SQL roster and fixture-disabled browser/API integration against G3 acceptance fixtures.
5. Isolated two-machine demonstration, installation package and commissioning rehearsal. Production eligibility is assessed using evidence from those checks and explicit target inputs.

Do not equate a partial slice or green master-schema test with a deployable machine-data product. Acquire and report actual validation evidence at each step.
