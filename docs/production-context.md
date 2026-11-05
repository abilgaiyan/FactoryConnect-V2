# Production Context

FactoryConnect separates observable machine facts from the business context used to interpret them.

## Facts captured by FC-006

- Company and site identity
- Shift definition
- Production schedule and Planned Operating Time (POT)
- Part / operation identity
- Operator identity
- Machine/operator assignment over time
- Production entry
  - Produced quantity
  - In-process rejected quantity
  - Optional job reference

## Boundary

FC-006 does not define customer-specific OEE, utilization, quality or performance formulas.

Those calculations will be resolved later through versioned Company / Site configuration and metric policies.

```text
Machine Activity
      +
Production Context
      |
      v
Historical Facts
      |
      +--> Versioned Company / Site Configuration
                 |
                 v
             Metrics
                 |
                 v
              Reports
```

## Rejection model

`InProcessRejectedQuantity` represents rejection known during production. QE/final-inspection rejection is intentionally not merged into this fact because it may occur later and may participate in different reporting rules.

## POT

`ProductionSchedule.PlannedOperatingTime` stores the effective POT for a specific machine, shift and production date. How POT is derived from shift duration, breaks, planned maintenance or other site rules belongs to versioned configuration rather than the production fact itself.
