# Company / Site Configuration and Metric Policies

FactoryConnect separates historical production facts from the rules used to interpret those facts.

## Principle

Production facts remain stable. Metric interpretation is resolved from the Company / Site configuration version that was effective for the reporting timestamp.

```text
Production Facts
      +
Company / Site
      +
Report Timestamp
      ↓
Effective Configuration Version
      ↓
Metric Policy
      ↓
Calculation Strategy
      ↓
Metric / Report
```

## Configuration Version

A site configuration version contains:

- Company identity
- Site identity
- Version
- Lifecycle
- Effective-from timestamp
- Optional effective-to timestamp
- Metric policies

Published versions are eligible for effective-date resolution. Draft versions are never used for reporting. Superseded versions remain available for historical traceability but are not resolved as active configurations.

`Effective` is intentionally not persisted as a lifecycle value. It is derived from lifecycle plus the effective date window for the timestamp being evaluated.

## Metric Policy

A metric policy selects a calculation strategy for a canonical metric.

Examples:

```text
availability → apt-over-pot
performance  → reference-time-over-apt
quality      → in-process-good-over-produced
oee          → availability-performance-quality
```

Another company or site may select different strategies without changing the production facts or FactoryConnect core domain.

Policies may also carry strategy parameters. The concrete metric calculator implementations are deliberately deferred to the metric-engine slice.

## Historical Reproducibility

A report for a historical date resolves the configuration version that was effective for that date. Publishing a new configuration version must therefore not change the rules used to reproduce older reports.

## Boundaries

This slice defines configuration identity, lifecycle, effective dating, policy selection and historical resolution.

It does not yet implement:

- Metric calculators
- Free-form formula expressions
- Configuration persistence
- Publishing workflow enforcement
- Authentication / authorization
- Report rendering
