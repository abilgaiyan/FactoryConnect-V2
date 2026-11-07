# FC2 — Operator-visible accepted machine evidence

PR #33 implements the operator evidence slice. Final qualification and merge identity
are recorded in the pull request. Production commissioning is a separate gate.

## User outcome

An enrolled operator signs in at the configured identity provider, selects a machine
currently authorized for `commissioning.read`, and sees actual retained SQL observations.
The view preserves original value whitespace/case and source quality, timestamp and
UInt64 decimal identifiers. Expandable provenance includes mapping, batch,
acknowledgement, document SHA-256, observation time and receipt time. Historical
observations never establish current connectivity, activity, production quantity or OEE.

## Trust and authorization

The platform OIDC middleware uses authorization code with S256 PKCE, HTTPS metadata,
exact configured issuer/client audience, signed/lifetime validated ID tokens,
correlation/state and nonce checks. Only after the validated ticket event does a
server-owned `IHumanIdentityVerifier` proof issue a durable SQL session for the exact
enrolled issuer/subject. Provider roles never grant FactoryConnect access. No manual
assertion/password field, fixture identity verifier or fallback provider is registered.

The secure HttpOnly `__Host-` cookie contains an ASP.NET Data Protection encrypted
authentication ticket. No provider access/refresh tokens are saved. The browser gets
its SQL bearer from the same-origin no-store session endpoint and holds it in memory;
credentials never come from URLs, dashboard config or persistent browser storage.
Every protected read checks current SQL session/grant authority. Logout requires a
same-origin HTTPS POST, atomically revokes only the caller's session with audit, then
clears the ticket. It grants no access-management permission. Revocation does not erase
observations already legitimately viewed; subsequent reads fail closed.

Machine discovery loads grant ancestry once inside the protected transaction. Validated
machine IDs filter SQL directly; site-wide access returns at most 101 eligible rows to
enforce the 100-visible-machine limit after authorization. It does not page unrelated
inventory. Retained evidence reads select at most 100 observations with the existing
SQL byte guard. SQL failures never become a successful healthy-empty result. The UI
cancels stale reads, hides previous-machine/session values, and distinguishes loading,
empty, denied, unavailable and failed results.

## Installation inputs

Supply `Identity:Authority` (exact HTTPS issuer pinned by installation enrollment),
`Identity:ClientId`, `Identity:ClientSecret` from a protected secret source, plus
`ConnectionStrings:Evidence`, `Evidence:InstallationId`, `Evidence:SiteId` and
`Evidence:StreamKey`. Register the exact HTTPS `/signin-oidc` callback at the provider.
Missing identity configuration fails closed. No customer tenant/client is provisioned
by this increment.

Serve built `dist` assets from the API's web root on the same HTTPS origin. Persist
protected Data Protection keys under the installation service account. API TLS, IdP
trust, callback registration, secret/key rotation, least-privilege SQL and production
service packaging remain target commissioning responsibilities. No forwarded-header
trust is implicitly enabled. The API `/dashboard/config` remains a reporting-readiness
shell; operator selection uses its separate protected endpoint.

Real dashboard mode has no mock fallback. Demonstration mode requires explicit
`VITE_ENABLE_FIXTURES=true` at build/dev startup and is not production acceptance.

## Executable acceptance

CI builds React, installs Chromium and invokes:

```sh
FC2_BROWSER_ACCEPTANCE=1 dotnet run --project tests/FactoryConnect.Sql.Acceptance --configuration Release -- --ingestion-only
```

The disposable SQL fixture first ingests actual controlled HTTP MTConnect observations.
An isolated signed-token OIDC provider and the product API then listen on actual HTTPS
Kestrel sockets. Chromium follows code/PKCE redirects, renders literal SQL observations
and provenance, and proves session/grant revocation and durable logout denial. Negative
identity cases reject invalid signature, issuer, audience, nonce and expiry. Provider code consumption,
redirect/client binding and PKCE are checked. The pinned installation issuer is retained;
only its discovery address is redirected to the test TLS listener. No token-validation
checks are disabled. Temporary fixture CA trust is removed; Chromium trusts only the
exact fixture leaf SPKI, never arbitrary certificates. No secrets enter artifacts.

TestServer handler coverage and mocked mounted UI tests remain separate, narrower checks.
The existing 100-machine and sparse-access SQL performance fixtures remain in CI.
Browser screenshot/qualification artifacts are uploaded with ingestion evidence.
These results qualify controlled workloads and protocol integration, not customer IdP,
factory network, fleet capacity or production deployment.

Next: accepted-evidence activity processing and operator activity visibility before
quantity/OEE expansion. No V1 history migration, production configuration change,
production database write or cutover is authorized by this slice.
