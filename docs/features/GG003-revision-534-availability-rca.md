# GG-003 Availability RCA: revision 534

Read-only investigation on 2026-10-17. No correction implemented.

## Exact failure

Database verified: disposable working RCA database. Deployment host identity is retained only in local evidence.
Machine: 11111111-1111-1111-1111-111111111111.
Site: rca-site.
Processor: metric-aggregation:11111111-1111-1111-1111-111111111111; stream metric-inputs; source revision 534.

The revision ledger identifies exactly one affected shift and one production day:
- SHIFT-1, assignment rca-shift-1-schedule, 2026-10-16 00:30–08:30 UTC (06:00–14:00 India time).
- Production day 2026-10-16 at the same site.

Both historical component snapshots have identical values:

| Operand | Durable key | Seconds | Count | Input positions |
|---|---|---:|---:|---|
| ActualProductionTime | duration.running | 2683.614157 | 31 | 380–523 |
| PlannedOperatingTime | duration.planned-production | 2452.013355 | 3 | 527, 530, 532 |

Ratio: 1.0944533199738628666645251612. Excess: 231.600802 seconds. Both scopes fail the domain; the prior stack trace does not identify which was evaluated first. Snapshot revision remains 534 even when an operand's last input position is earlier.

Historical extraction follows SqlServerRevisionedOperationalMetricComponentSnapshotReader: committed contributions for processor/stream through position 534, joined to exact facts and grouped by occurrence/day. Current aggregate tables were not substituted. All 167 exported facts belong to this one occurrence/day; therefore the day totals equal the shift totals.

## Contribution explanation

All 31 running contributions have IsPlannedProductionTime=false and no planned assignment. Their individual nonoverlapping intervals range from first start 02:32:25.791360 UTC to final end 05:50:11.751475 UTC. The span is not continuous running; the sum is 2683.614157 seconds.

All three planned contributions have IsPlannedProductionTime=true and assignment rca-planned-1:

| Position | Start UTC | End UTC | Seconds | Source eligibility ID |
|---|---|---|---:|---|
| 527 | 06:00:00 | 06:39:33.149119 | 2373.149119 | rca-eligibility-527 |
| 530 | 06:39:33.149119 | 06:39:34.599802 | 1.450683 | rca-eligibility-530 |
| 532 | 06:39:34.599802 | 06:40:52.013355 | 77.413553 | rca-eligibility-532 |

Persisted eligibility payloads confirm states Stopped, Unknown, Stopped for these fragments; a sampled running payload confirms Running outside planned time. Exports retain every fact identity, source contextualized activity ID, eligibility ID, assignment, interval, unit and period ownership.

Checks: zero same-key overlapping pairs for running/planned facts; zero duration/value discrepancies at microsecond resolution; zero occurrence-containment violations among all 167 facts. Exported boundaries have no finer nonzero precision. Scheduled total is 22252.013355 seconds, stopped total 19549.434881 seconds; Unknown time explains why scheduled need not equal running plus stopped.

The immediate cause is incompatible operand eligibility. DurableMetricInputFactDeriver emits running duration regardless of planned eligibility, but planned duration only for eligible fragments. OperationalMetricComponentKeyMapping maps all running duration to ActualProductionTime. MetricInputContributionAggregator faithfully sums these keys by period. This is not an arithmetic or double-counting error. Shift/day ownership is consistent. Numerator and denominator evidence are entirely disjoint.

Configuration discrepancy: durable planned eligibility starts at 06:00 UTC, 5.5 hours after shift start. The repository sample Edge configuration defines its sample planned-production window as 06:00–14:00 UTC, consistent with the observed boundary, but for a sample machine/site. This does not prove the effective production configuration, its origin, or factory approval. Obtain effective deployment configuration and approved calendar before conclusively attributing the configuration error. No 30-minute break deduction appears in these operands; do not infer one.

## Proposed policy and narrow correction

Preserve the [0,1] validated domain. Only positively evidenced incompatible eligibility should produce typed Unavailable with a specific reason, null numeric result, both operand values, exact source revision, period, and contribution references. Without that evidence, retain domain rejection. See the containment proposal for the refined reason and contract extension. Preserve out-of-plan running as real activity evidence. Propagate unavailability to dependent OEE. Keep missing evidence, inconsistent evidence and valid numeric zero distinct. No clamping or invented numeric value.

First correction: an Availability-specific consistency assessment before numeric publication through the typed outcome pathway, extending reason/provenance contracts as necessary. Do not broadly catch all evaluator errors or weaken other domains. Commit the complete versioned batch atomically and advance only through normal processing after successful publication. No manual checkpoint actions.

A later numeric semantic correction requires an approved contract. If Availability means running within planned production, introduce a separate versioned planned-running operand from eligibility intersections; retain total running for utilization/other consumers. Do not globally remap ActualProductionTime or silently reinterpret definition 1.0. The observed intersection is empty, but that alone does not authorize publishing zero under an inferred new definition. Correct rca-planned-1 timezone/calendar only after effective configuration and approved schedule establish the intended window. Preserve historical lineage on recalculation.

## Regression-test plan

1. Freeze the 167 facts at cut 534. Assert exact shift/day identities, sums, counts and full failing decimal ratio. Prove later facts/current aggregates cannot leak into historical snapshots.
2. Assert all 31 running intervals are unplanned, nonoverlapping, and outside the planned window. Assert positions 527/530/532, exact boundaries, contiguous planned coverage, duration conservation and occurrence ownership. Preserve the Unknown fragment without inventing activity.
3. Assert typed Unavailable and reason/provenance for this mismatch, null numeric value, and dependency-unavailable OEE. Separately test missing operands, zero denominator, valid zero, valid one and valid fraction.
4. Test eligibility mismatch even where the accidental ratio is below one; bounds alone cannot prove compatible evidence. Test overlaps/duplicate sources separately from this reproduction.
5. Disposable SQL regression: predecessor 524, actual next ledger revision 534, atomic typed batch publication, then next available revision above 534. Verify idempotence, restart, exact-version selection and no partial batch on failure. Do not assume 535 exists or manually advance a checkpoint.
6. If later authorized by an approved metric contract: test planned-running intersections, window boundaries, timezone conversion, overnight shifts, calendar exceptions, unchanged all-running utilization, and versioned history. Test assumed breaks separately without fabricated timestamps or double deduction.

## Evidence and safety

Root workspace files:
- gg003-revision-534-evidence.txt: raw SELECT output (JSON wraps across lines), payload samples and SQL check results.
- gg003-revision-534-contributions.json: normalized 167-fact export.
- gg003-revision-534-components.csv: all 34 operand contributions with provenance and boundaries.
- gg003-revision-534-eligibility-samples.json: four persisted source payloads.

Final checkpoint SELECT: 524. Only SELECT statements ran against the disposable database. No production/original RCA database changes, runtime cycle, schema migration, checkpoint reset or advancement. No application code changed during the original RCA. Its regression plan below is distinct from the offline Stage A characterization tests added later; see the containment proposal for verification results.


## Stage A identity sanitization

Deployment IDs in this review artifact are deterministic aliases, not production routing identities. Contribution positions, exact decimal values, UTC boundaries, business date, eligibility flags and identity relationships remain unchanged. The original identifiers, raw exports and identity mapping are retained locally and excluded from the Stage A commit. The TestData provenance manifest binds the sanitized fixture to the locally retained original source digest. No deployment host or connection string is included.
