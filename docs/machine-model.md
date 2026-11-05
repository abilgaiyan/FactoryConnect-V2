# FactoryConnect Machine Model

## Purpose

The machine model is the canonical representation of factory equipment and its signals. It must remain independent of Modbus, MTConnect, PLC vendors, and gateway-specific addressing.

## Hierarchy

```text
Factory
  └── Production Line
        └── Machine
              └── Machine Signal Definition
```

The initial Gajra pilot contains seven machines across two lines: five machines on Line 1 and two machines on Line 2.

## Canonical signals

A machine describes business-facing signal keys such as:

- `Running`
- `Fault`
- `Cycle`

The model does not contain `DI0`, register numbers, Modbus coils, or other protocol-specific details.

## Signal mapping

`MachineSignalMapping` associates a canonical signal key with an opaque source identifier. Protocol adapters interpret the source identifier.

Example:

```text
Machine: Gajra-L1-M01

Running → DI0
Fault   → DI1
Cycle   → DI2
```

The application therefore consumes `Running`, while the Modbus adapter understands `DI0`.

## Connector contract

`IMachineConnector` returns a `MachineSignalSnapshot`. This creates the boundary between physical acquisition and the rest of FactoryConnect.

```text
Industrial Protocol
        ↓
IMachineConnector
        ↓
MachineSignalSnapshot
        ↓
Future State Engine
        ↓
Production Events
```

## Simulator

`FactoryConnect.Simulator` implements the same connector contract as a physical connector. This allows state-engine and application development without requiring factory hardware.
