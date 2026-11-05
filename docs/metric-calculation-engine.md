# Metric Calculation Engine

## Purpose

The metric calculation engine converts production facts into business metrics by applying the metric strategy selected by the effective Company / Site configuration.

Production facts remain immutable. Calculation rules can evolve independently through versioned metric policies.

```text
Production Facts
      +
Effective Site Configuration
      |
      v
Metric Policy
      |
      v
Metric Calculation Engine
      |
      v
Metric Strategy
      |
      v
Metric Result
```

## Canonical Metric Inputs

The typed calculation vocabulary includes:

- `apt` - Actual Production Time
- `pot` - Planned Operating Time
- `not` - Production Reference Time
- `machine-power-on-time` - Tmton / Machine Power-On Time
- `produced-quantity`
- `good-quantity`
- `availability`
- `performance`
- `quality`

Time-based ratio inputs must use the same unit within one calculation. The engine does not prescribe minutes, seconds, or hours because ratios are unit-independent when inputs are normalized consistently.

## Built-in Strategies

### Availability / ELR

Strategy: `apt-over-pot`

```text
Availability = APT / POT
ELR          = APT / POT
```

Availability and ELR remain distinct canonical business metrics even though the current approved formulas are identical.

### Performance

Strategy: `reference-time-over-apt`

```text
Performance = NOT / APT
```

### Quality

Strategy: `good-over-produced`

```text
Quality = Good Quantity / Produced Quantity
```

### ELRE

Strategy: `apt-over-machine-power-on-time`

```text
ELRE = APT / Tmton
```

`Tmton` is represented by the normalized input `machine-power-on-time`.

### OEE Composition

Strategy: `availability-performance-quality`

```text
OEE = Availability x Performance x Quality
```

The engine returns ratios as decimal values rather than percentages. Presentation layers decide whether to render `0.75` as `75%`.

## Availability of Results

A metric result can be unavailable when required facts are missing, a denominator is zero/non-positive, the selected strategy is not registered, or the policy does not match the requested metric.

Unavailable metrics retain a reason instead of silently producing a misleading zero.

## Extensibility

`IMetricCalculationStrategy` is the extension point for company-independent calculation implementations. A Company / Site configuration selects a strategy through `MetricPolicyDefinition.StrategyKey`.

This allows different sites to calculate the same canonical metric with different approved strategies without adding customer checks to FactoryConnect Core.

## Deferred

The calculation engine itself deliberately does not introduce:

- free-form expression execution
- report aggregation
- persistence
- publishing workflow enforcement
- report rendering

Input derivation and machine-shift orchestration are separate layers built above this calculation boundary.
