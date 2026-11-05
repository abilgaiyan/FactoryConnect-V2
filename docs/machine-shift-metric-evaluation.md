# Machine Shift Metric Evaluation

## Purpose

FC-010 composes metric input derivation and metric calculation for one Company + Site + Machine + Shift + Production Date.

```text
Machine Activity + Production Schedule + Production Entries
                         ↓
                Metric Input Derivation
                         ↓
              Derived Normalized Inputs
                         +
       Additional Normalized Inputs (NOT, Tmton)
                         ↓
              Machine Shift Evaluation
                         ↓
        Availability / Performance / Quality
                  ELR / ELRE / OEE
```

## Responsibilities

`MachineShiftMetricEvaluator`:

- derives APT, POT, produced quantity, and good quantity through `MetricInputDeriver`;
- accepts additional normalized inputs that are not yet derivable from recorded production facts;
- evaluates metric policies in the order supplied by the caller;
- makes successful metric results available as inputs to later dependent metrics;
- preserves unavailable metric results and their reasons;
- returns a canonical scope-aware result without mutating source production facts.

Additional inputs cannot replace values already derived from historical facts.

## Gajra Metric Vocabulary

The current report-backed formulas are:

- Availability = `APT / POT`
- Performance = `NOT / APT`
- Quality = `Good Quantity / Produced Quantity`
- OEE = `Availability × Performance × Quality`
- ELR = `APT / POT`
- ELRE = `APT / Tmton`

Canonical normalized inputs:

- `apt` — actual production/manufacturing time.
- `pot` — planned operating/work time according to schedule.
- `not` — production reference time.
- `machine-power-on-time` — `Tmton`, accumulated machine power-on time.
- `produced-quantity` — manufactured quantity.
- `good-quantity` — produced quantity less rejected quantity.

## Tmton Boundary

FC-010 does not infer `Tmton` from hardware terminals.

Machine-specific connectivity maps physical signals such as PLC bits, MTConnect data items, gateway digital inputs, or configured current thresholds into canonical signals. The canonical power signal is `state.power-on`. A later derivation capability can accumulate power-on periods into `machine-power-on-time`.

DIN1/DIN2/DIN3/DIN4 meanings are therefore configuration, not protocol-wide assumptions.

## Metric Ordering

Metric policies are evaluated in the supplied order. Successful results are added to the working evaluation input set so dependent metrics can execute later in the same evaluation.

For example:

```text
Availability
Performance
Quality
OEE
```

allows OEE to consume the three earlier metric results.

A generic metric dependency graph is deliberately deferred until the metric catalog requires it.

## Architectural Boundary

FC-010 orchestrates existing capabilities. It does not define new business formulas, load data from persistence, aggregate line/site results, or render reports.
