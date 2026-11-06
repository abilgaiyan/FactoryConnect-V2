# FactoryConnect Dashboard

Industrial IoT manufacturing intelligence and factory machine operational reporting dashboard, rewritten as a modern React and TypeScript application running on Vite.

## Overview

FactoryConnect provides authoritative industrial reporting and machine monitoring for factory operations:
- **Production Day Reporting**: Cross-line operational metrics including Availability, Utilization, Performance, Quality, and OEE.
- **Shift Performance**: Shift-by-shift operational evaluation across configured production lines and shifts.
- **Daily Reports**: Print-ready daily manufacturing performance summaries.
- **Machine Current State**: Real-time evidence-backed machine states (Running, Idle, Stopped, Fault) with freshness, usability, and authority indicators.
- **Activity History & Counter Inspection**: Detailed interval-level operational timelines and raw counter sample audits.
- **Management Overview**: Multi-site and multi-line fleet status with reconciliation counters.
- **Commissioning Status**: Edge acquisition, SQL ledger authority, and stream binding verification.
- **Supervisor Desk Preview**: Interactive screen-review preview for operations supervisors.

## Getting Started

### Prerequisites

- Node.js 22+
- npm

### Installation

```bash
npm install
```

### Development

Run the Vite development server on port 3000:

```bash
npm run dev
```

### Production Build

```bash
npm run build
```

### Preview

```bash
npm run preview
```

### Tests

```bash
npm test
```
## V2 backend foundation

Requires the .NET 10 SDK. Build with `dotnet build FactoryConnect.slnx`.
Run with `dotnet run --project backend/FactoryConnect.Api -- --urls http://127.0.0.1:5080`.
The host has no database or evidence providers: liveness succeeds, readiness and evidence APIs return 503.
Vite dev/preview still use mock middleware; the dashboard is not connected to this host yet.
See [the dependency review](docs/architecture/FC2-002-backend-foundation.md).

Initial security bootstrap is an explicit offline, two-custodian operation; see [the bootstrap contract and commands](docs/implementation/FC2-G2-initial-bootstrap.md). No default administrator or automatic bootstrap is provided.

Human session issuance boundary: [G2 human session authority](docs/implementation/FC2-G2-human-session-authority.md). No sign-in endpoint or identity-provider adapter is enabled by this increment.

Scoped human access grants: [G2 grant issuance](docs/implementation/FC2-G2-scoped-access-grants.md). The server-side command requires current management and delegation authority; it does not enable configuration writes or enroll users.

Human registration and offboarding: [G2 identity lifecycle](docs/implementation/FC2-G2-human-identity-lifecycle.md). Enrollment requires installation administration and the committed bootstrap issuer; it grants no permissions.

Recovery prerequisite: [G2 recovery trust provisioning](docs/implementation/FC2-G2-recovery-trust-provisioning.md). Current administration, bootstrap custody approval and separate recovery-key possession proofs are required; recovery execution remains unavailable.

V1 performance safeguards and current measurements: [regression prevention and full-path SQL inventory acceptance](docs/implementation/FC2-V1-performance-regression-prevention.md).

Configuration prerequisite for activation: [authenticated submission and independent review](docs/implementation/FC2-configuration-submission-review.md), including batched SQL writes and explicit policy-before-machine locks. No active roster or runtime acknowledgement is enabled.

G3 pending activation intent: [durable per-machine requests](docs/implementation/FC2-G3-durable-activation-requests.md). Exact approval/generation checks and intent revocation are server-side; runtime mTLS authentication, acknowledgement and active roster publication remain unavailable.

Runtime key custody prerequisite: [controlled signing-key enrollment, rotation and revocation](docs/implementation/FC2-G2-runtime-credential-authority.md). Two independent custodians and new-key possession proofs are required. This does not supply mTLS transport or X.509 issuance.

Runtime assignment prerequisite: [scoped assignment, explicit replacement and revocation](docs/implementation/FC2-G2-runtime-assignment-authority.md). Assignment and credential generations remain independent; these commands create no activation or acknowledgement.
