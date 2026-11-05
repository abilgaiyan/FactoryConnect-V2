# FactoryConnect Architecture

## Boundary

FactoryConnect is the factory and machine connectivity platform. It is intentionally separate from PulseStackAI, which remains responsible for AI agents, workflows, and AI orchestration.

## Runtime flow

```text
Machine
  ↓
I/O Gateway / Industrial Protocol
  ↓
FactoryConnect Edge
  ↓
Durable Observations
  ↓
Signal Mapping + Current-State Authorities
  ↓
Canonical Machine State / Activity
  ↓
Production Context + Operational Metrics
  ↓
Persistence
  ↓
FactoryConnect.Api
  ↓
FactoryConnect.Dashboard / Analytics
  ↓
PulseStackAI (future integration)
```

## Layering

```text
FactoryConnect.Abstractions
        ↑
FactoryConnect.Core
        ↑
FactoryConnect.Infrastructure
        ↑
FactoryConnect.Edge

FactoryConnect.Protocols.Modbus ──→ Abstractions
FactoryConnect.Api              ──→ Core + Infrastructure + Abstractions
```

Protocol implementations remain adapters. The factory domain must not depend on Modbus register addresses, coils, or vendor-specific concepts.

## Initial connectivity

The delivered connectivity stack includes industrial Ethernet I/O gateways, Modbus TCP, and MTConnect discovery plus current/sequence-aware sample acquisition. Protocol adapters feed the canonical FactoryConnect model without becoming domain authority; additional protocols can be added behind the same boundary.

## Development principle

Physical hardware is not required for core development or conformance. Simulated/fake connectors and provider conformance fixtures exercise the same contracts used by real industrial connectors.
