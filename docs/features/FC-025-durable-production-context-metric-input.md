# FC-025 — Durable Production Context and Metric Input Derivation

## Purpose

FC-025 interprets durable machine activity within historically correct factory operations and produces durable metric-input facts.

The feature does not calculate final KPIs and does not classify downtime reasons.

## Durable flow

```text
DurableMachineActivityPeriod
        +
Effective-dated ProductionContextAssignment
        +
Resolved ShiftOccurrence
        ↓
ContextualizedActivityInterval
        +
Resolved PlannedProductionInterval
        ↓
ProductionTimeEligibilityInterval
        ↓
DurableMetricInputFact

DurableProductionQuantityEvidence
        ↓
DurableMetricInputFact
```

Activity-derived time facts and quantity-derived facts use independent durable processor identities and checkpoints because their source positions may advance independently.

## Temporal invariants

- Production context is resolved for the historical activity interval, never from current configuration.
- Effective intervals and allocated intervals use half-open semantics: `[start, end)`.
- Shift definitions are recurring local configuration; `ShiftOccurrence` is an absolute UTC interval.
- Line-specific schedules override site-wide schedules for the requested line and date.
- Planned-production replacement overrides may activate an otherwise inactive recurring day.
- Allocation preserves source duration and cannot introduce gaps, overlaps, or zero-length fragments.
- Missing production context does not discard machine activity.
- One machine timeline cannot contain overlapping eligibility intervals even when shift or production context differs.

## Shift resolution

Shift count and duration are configuration, not application assumptions. A site may use two, three, four, or any other non-overlapping set of recurring shifts.

DST resolution is deterministic:

- an invalid spring-forward boundary advances to the first valid local instant;
- an ambiguous shift start selects the earlier UTC instant;
- an ambiguous shift end selects the later UTC instant.

Resolved duration therefore represents actual elapsed time.

## Planned production eligibility

A shift describes when factory operations exist. Planned-production intervals describe which portions of operational time are eligible as planned production time.

Planned breaks and shutdowns can remove eligibility. Calendar replacement windows can replace recurring planned windows for a specific factory date.

`IsPlannedProductionTime` is an eligibility fact only. It does not classify a stopped interval as planned or unplanned downtime.

## Metric input facts

FC-025 derives durable evidence facts such as:

- scheduled duration;
- planned-production duration;
- running duration;
- idle duration;
- stopped duration;
- alarm duration;
- offline duration when `MachineState.Offline` is explicit;
- part-count increments;
- good quantity;
- rejected quantity.

Facts are not percentages or final metrics. Availability, performance, quality, OEE, ELR, and similar calculations remain downstream.

Quantity facts require explicit quantity evidence. Running time never implies a produced quantity.

Every duration fact preserves planned-production eligibility, shift-schedule lineage, planned-production schedule lineage when applicable, source contextualized-activity lineage, hierarchy, and production context.

## Durable runtime composition

`ProductionContextProcessingRuntime` owns an independent `ObservationProcessorId` and checkpoint for one configured machine/activity-stream scope.

A cycle performs:

```text
restore FC-025 activity checkpoint
        ↓
read durable activity after checkpoint
        ↓
resolve historical production context
resolve shift occurrences
        ↓
allocate contextualized activity
        ↓
resolve planned-production intervals
allocate planned/non-planned eligibility
        ↓
derive durable duration metric-input facts
        ↓
atomic commit
    contextualized intervals
    eligibility intervals
    duration metric facts
    next activity checkpoint
```

`ProductionQuantityFactProcessingRuntime` owns a separate processor identity and checkpoint for one durable quantity-evidence stream:

```text
restore quantity checkpoint
        ↓
read durable quantity evidence after checkpoint
        ↓
derive part / good / rejected quantity facts
        ↓
atomic commit
    quantity metric facts
    next quantity checkpoint
```

Activity progress and quantity progress are intentionally independent. Both runtimes write the same durable metric-fact model through the same provider-neutral store contract.

The checkpoint is not advanced until all outputs for that processor cross the durable commit boundary.

## Provider-neutral contracts

FC-025 uses provider-neutral boundaries:

- `IProductionContextReader`
- `IShiftScheduleReader`
- `IPlannedProductionScheduleReader`
- `IProductionContextActivityReader`
- `IProductionQuantityEvidenceReader`
- `IProductionContextProcessingStore`

The in-memory implementations are reference/conformance providers. SQL persistence for FC-025 outputs is outside this feature slice.

## Restart, replay, and durable identity

Output identities are deterministic. A restarted runtime restores its own processor/stream checkpoint and resumes after the last committed durable source position.

Identical replay of an already durable output identity is idempotent. Reuse of the same durable output identity with different content is rejected before checkpoint advancement.

The in-memory conformance scenarios prove:

- independent processor checkpoints on the same stream;
- independent activity and quantity processor progress;
- multiple machines and lines;
- multi-batch processing;
- restart/resume without duplicate outputs;
- context-boundary splitting;
- shift-boundary splitting;
- planned-production boundary and planned-break splitting;
- partial missing-context survival;
- duration conservation through contextualized activity, eligibility, and scheduled-duration facts;
- quantity fact derivation from explicit durable evidence;
- activity-runtime failure propagation from checkpoint restoration, durable activity read, production-context read, shift assignment read, shift override read, planned-production assignment read, planned-production override read, and final durable commit;
- quantity-runtime failure propagation from quantity checkpoint restoration, durable quantity-evidence read, and quantity-fact durable commit;
- no output or checkpoint mutation after a failed provider/durable boundary;
- retry after recovery consumes the same activity or quantity evidence exactly once;
- restart after successful recovery produces no duplicate facts;
- inconsistent replay collisions are rejected.

## Scope boundaries

FC-025 does not include:

- final KPI aggregation;
- OEE or utilization percentage calculation;
- downtime reason classification;
- reporting queries;
- SQL persistence for FC-025 projection outputs;
- production master-data editing workflows;
- UI or authorization.

### Edge planned break configuration

Configure optional `BreakWindows` under each machine's `ProductionProcessing:Machines:<index>:PlannedProduction` section (or `ProductionProcessing:PlannedProduction` for the single-machine configuration). Omit `BreakWindows` when no breaks apply; omission preserves the existing uninterrupted planned window. Non-null section values are rejected, including empty strings. The JSON configuration provider represents an empty array as an empty string, so explicit `BreakWindows: []` is also rejected; use omission instead.

```json
"PlannedProduction": {
  "AssignmentId": "POT-LINE-1",
  "TimeZoneId": "Asia/Kolkata",
  "StartsAtLocal": "22:00:00",
  "EndsAtLocal": "06:00:00",
  "EffectiveFrom": "2026-01-01",
  "BreakWindows": [
    { "StartsAtLocal": "02:00:00", "EndsAtLocal": "02:30:00" }
  ]
}
```

This is an illustrative configuration, not approved factory timing. Breaks use the planned schedule's local time zone and must have distinct start/end times, lie wholly inside its planned window, and not overlap. Adjacent breaks are allowed. Invalid configuration fails during composition. For an overnight planned window, breaks starting after midnight belong to the following calendar day; a break may itself cross midnight. Resolved intervals retain the originating factory date and exclude the break duration. An eight-hour window with a thirty-minute break therefore contributes seven and a half hours of planned time.

Machines sharing a planned assignment ID must configure the same breaks. Shift boundaries remain configured separately in `ShiftSchedules`; breaks alter planned production eligibility, not shift identity. No production quality assumption is enabled by this configuration.
