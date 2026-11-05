# Metric Input Derivation

## Purpose

Metric input derivation converts recorded shop-floor facts into the normalized inputs consumed by the Metric Calculation Engine.

```text
Machine Activity + Production Schedule + Production Entries
                         ↓
                Metric Input Derivation
                         ↓
                Normalized Metric Inputs
                         ↓
                Metric Calculation Engine
```

## Derived Inputs

The first implementation derives:

- `apt` — sum of Running machine activity duration, expressed in hours.
- `pot` — planned operating time from the production schedule, expressed in hours.
- `produced-quantity` — sum of production entry quantities within the requested scope.
- `good-quantity` — sum of produced quantity minus in-process rejected quantity.

## Scope

Derivation is performed for one Company + Site + Machine + Shift + Production Date. Production schedule and production entries must belong to the same scope.

## Production Reference Time

`not` is the production reference time used by the Gajra report formula `Performance = NOT / APT`. It is deliberately not derived in FC-009 because it requires a production-standard/reference-time source and may vary by company/site. It remains an extensible metric input rather than an assumed formula.

## Machine Power-On Time

`machine-power-on-time` represents `Tmton` from the Gajra equipment-utilization report: accumulated time during which the machine is energized/control power is on. It is not derived by FC-009. Its source can be a configured canonical `state.power-on` signal supplied by a machine-specific adapter or signal mapping.

Hardware terminals such as DIN4 are not assigned a universal meaning by the domain model. Physical terminal-to-signal mapping remains machine configuration.

## Architectural Boundary

Production facts remain unchanged. Derivation creates calculation inputs from those facts; it does not reinterpret or mutate the source records.

Company/site metric policy continues to decide how the resulting inputs are used by the Metric Calculation Engine.
