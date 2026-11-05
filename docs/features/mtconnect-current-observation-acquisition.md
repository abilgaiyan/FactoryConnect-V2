# MTConnect Current Observation Acquisition

## Purpose

FC-013 introduces acquisition of the latest MTConnect values through the standard `/current` endpoint.

The slice converts protocol-specific MTConnect stream values into raw `MachineObservation` records. It deliberately stops before FC-011 canonical signal mapping.

```text
MTConnect Agent
      ↓
   /current
      ↓
MTConnectStreams
      ↓
DeviceStream selection
      ↓
MachineObservation
      ↓
FC-011 signal mapping
      ↓
Canonical FactoryConnect signal
```

## Device Selection

One MTConnect Agent can expose multiple devices. Acquisition therefore requires a FactoryConnect `MachineId` and an MTConnect device key.

The device key may match either the MTConnect `DeviceStream` `name` or `uuid`.

This keeps the association between an MTConnect device and a FactoryConnect machine explicit.

## Observation Address

The MTConnect `dataItemId` becomes the raw FactoryConnect observation address.

```text
MTConnect dataItemId: exec
        ↓
MachineObservation.Address = "exec"
```

FC-011 can then configure:

```text
Source  = mtconnect
Address = exec
        ↓
Canonical signal
```

Discovery and semantic mapping remain separate responsibilities.

## Value Types

FC-013 uses the MTConnect stream category to create the raw FactoryConnect signal type:

- `Samples` → `SignalType.Numeric`
- `Events` → `SignalType.Enumeration`
- `Condition` → `SignalType.Text`

Numeric sample values are parsed using invariant culture.

An MTConnect value of `UNAVAILABLE` is represented as a null value with `ObservationQuality.Uncertain`.

## Scope Boundary

FC-013 does not implement:

- `/sample` sequence-based acquisition
- continuous polling
- retry/backoff orchestration
- canonical signal mapping
- machine-state derivation
- persistence
- admin/setup UI

Those concerns belong to later slices.
