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
