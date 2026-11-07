# FC2 — Operator-visible accepted machine evidence

Status: implementation slice opened after PR #32. This document is a delivery contract, not an assertion that the API/dashboard integration already exists.

## User outcome

An authorized operator selects a commissioned machine and sees **actual retained V2 MTConnect observations**, including exact raw value (preserving whitespace and case), explicit source quality, original timestamp, source instance/sequence, mapping identity and receipt provenance. Historical evidence is not represented as current connectivity, machine state, quantity or OEE.

## Trust boundary

The existing SQL `ReadMtConnectEvidenceAsync` authority is the only source of evidence. The browser must not supply a trusted site/role/permission, runtime acknowledgement, checkpoint or SQL credential. Establish a real human session on the server, bind machine-scoped `commissioning.read`, then call the protected bounded SQL reader. Never reuse the acquisition runtime's private key, the CLI stdin bearer, or a fixture session as a browser authentication mechanism.

The current `FactoryConnect.Api` host is a readiness shell and has no authenticated human-session middleware. Until a real session-to-API boundary is implemented and acceptance-tested, do not publish a browser evidence endpoint. In particular, no unauthenticated or static-token query route and no mock fallback on API errors.

## API acceptance

- Read-only machine-scoped endpoint with server-derived session authority; unauthorized and revoked sessions fail closed.
- Explicit `no-evidence`, `authority-unavailable`, and `evidence` outcomes. No healthy empty response when SQL authority is unavailable.
- Bounded page <=100 observations and existing SQL byte guard. Exact UInt64 decimal strings and original raw values; no JavaScript numeric conversion of source sequence.
- Evidence response includes provenance and a clear `historical-retained-evidence` designation, never inferred machine connectivity.
- Integration acceptance tests use real SQL, real human-session authorization, revocation ordering, and saved observations from PR #31/32. No per-row SQL or full-history scan.

## React acceptance

- Reuse the existing dashboard routing/layout and runtime client conventions.
- Display the exact raw value with preserved whitespace, signal address, timestamp, quality and provenance; show `UNAVAILABLE` as unavailable, not numeric zero.
- Distinguish loading, no evidence, unauthorized, unavailable and failed states. Never fabricate OEE or current machine status.
- Abort stale requests on machine change/unmount and avoid showing a previous machine's data.
- Browser tests assert literal values from the protected endpoint contract and explicit error states; mock transport tests alone do not establish server integration.

## Delivery order

1. Server human-session integration and protected bounded evidence endpoint.
2. Typed frontend client and operator evidence view with mounted tests.
3. SQL-backed end-to-end browser acceptance and latency/roundtrip measurements.
4. Separate production provisioning and service packaging. V1 production remains untouched.

No V1 historical migration, reporting pipeline import, production deployment or credential bootstrap is authorized by this slice.


## PR #33 review disposition — 2026-11-07

The following integration gates remain **OPEN**. This pull request stays draft and must not be merged solely because CI is green.

- **P1 trusted verifier:** the browser now has a one-use assertion exchange and operator machine-selection screen, but the API intentionally does not register a real `IHumanIdentityVerifier`. A production-trusted provider must validate issuer, audience, signature, freshness, nonce/state and proof binding before registering the service. The manually entered assertion field is a transitional integration seam, **not** a production sign-in protocol or approval to enter credentials into the dashboard.
- **P1 HTTP/SQL/browser acceptance:** direct SQL tests and mocked browser transport do not establish the complete HTTPS API contract. Add disposable SQL plus hosted HTTPS route tests, machine grant/session revocation, true rendered observation values, and measured per-endpoint latency/SQL roundtrips before readiness.
- **P1 machine selection:** `GET /api/commissioning/v2/evidence-machines` enumerates bounded current machine-scoped `commissioning.read` authorization and rechecks grant chains under the SQL security gate. It does not grant machine access or make a machine-current-state claim. API `/dashboard/config` remains a readiness shell with no reporting sources.
- **P2 provenance:** each observation can expand mapping ID, batch ID, acknowledgement ID, source document SHA-256, receipt time and observation time. The original raw value and UInt64 decimal identifiers remain visible unchanged.

No V1 production changes, production credential bootstrap or release/deployment is authorized by these slices.
