# RR03.1 — Assumed break duration: integration contract and expected results

Status: **PROPOSED / REVIEW REQUIRED**. This contract does **not** authorize reporting integration, release, or migration. PR #107 remains draft.

## 1. Effective-dated policy provenance

A result MUST identify a policy using `PolicyId`, `PolicyVersion`, `EffectiveFrom`, optional `EffectiveTo`, `SiteId` and optional `ProductionLineId`. Select the single applicable policy by factory production date, with line-specific precedence over site-wide and no ambiguous competing effective policies. A request without a matching *admitted and effective* policy returns **Unavailable(PolicyNotEstablished)**; no implicit 30-minute production fallback.

**One admission rule:** Select exactly one effective-dated policy explicitly admitted for this reporting scope; admission mode must be either `FormallyApproved` (with verifiable approval reference) or `DemoProvisional` (explicitly enabled for GG-003 only). Both modes require a nonempty `PolicyId`, immutable `PolicyVersion`, site/machine scope and `EffectiveFrom`; there is no global implicit policy. `DemoProvisional` uses source `CustomerRelayedGajraDemoDefault` and `PolicyAssumed`, must appear as provisional on the report, and must not be represented as formal Gajra sign-off. No overlapping effective policies of equal specificity are allowed. 

**Proposed versioned demo configuration to record before admission:** `PolicyId=GG003-DEFAULT-BREAK`, `PolicyVersion=demo-v1`, `MachineId=677b030b-1ede-4a67-82ae-6a494791357f`, `BreakAllowance=PT30M`, `AdmissionMode=DemoProvisional`, `InstructionReference=FactoryConnect GG-003 conversation, 2026-10-16: customer relayed Gajra's 30-minute flexible-break default`, `EffectiveFrom=2026-10-16` (proposed demonstration activation date, **not a claimed historic Gajra approval date**). These values must be explicitly activated in configuration for the demonstration and limited to that effective date forward; pre-effective historical data must remain unavailable or use its own independently authorized policy.

Use `PolicyAssumed` as provenance, **not** as a substitute for policy identity or approval. Identical replay returns the same policy binding; a changed version triggers an explicitly versioned re-evaluation and never silently changes historical assessments.

## 2. Exclusions and conservation

`otherExcludedDuration` is not an untrusted aggregate from independent sources. It MUST be formed from a validated union of half-open, timestamped intervals within the exact scheduled shift. Exclusions must have source identity and classification. Duplicate or overlapping intervals are unioned once. An exclusion representing an actual break already deducted by this allowance is rejected or marked unavailable rather than charged again.

The existing duration-only calculator assumes **the caller already supplied** that validated duration. Integration MUST add a typed validation boundary and never pass a raw sum of intervals. Ambiguous classification or overlapping break authority returns **Unavailable(ExclusionAuthorityAmbiguous)**. No exclusion is inferred from CNC Stopped/Idle machine activity.

Example expected results (completed scheduled shift):
- 480 scheduled, zero other exclusions, 30 assumed break => **450 planned minutes**.
- 480 scheduled, two overlapping shutdown intervals 10:00–10:20 and 10:10–10:30 (union 30), 30 break => **420 planned minutes**.
- 60 scheduled, 45 other excluded, 15 assumed break => **0 planned minutes**.
- 20 scheduled, no exclusions, capped 20 assumed break => **0 planned minutes** (Availability unavailable when denominator is zero).
- 480 scheduled, an interval explicitly classified as actual break => **unavailable pending precedence**, not an additional 30-minute deduction.
- An unscheduled period => **zero default deduction**.

## 3. Forecast consumption safety

A result with `IsForecast=true` is display-only and MUST NOT enter elapsed Availability evaluation, published numeric planned-time facts or authoritative completed-shift metrics. The consuming API must require a validated **CompletedShiftPlannedTime** value (or equivalent discriminated completed/forecast result), not merely inspect a caller-optional flag.

Current-shift forecast may be shown distinctly, but numeric *elapsed* Availability is **Unavailable(BreakTimingNotEstablished)** until the shift closes or approved actual-break evidence supports an exact interval calculation. The shift-start and mid-shift cases cannot inherit a future 30-minute deduction as already elapsed.

Expected results:
- Shift start, eight-hour schedule: forecast **450**, elapsed Availability **unavailable**.
- Mid-shift, eight-hour schedule: forecast **450**, elapsed Availability **unavailable**.
- Completed eight-hour shift: planned **450**, calculation eligible only with coherent activity/evidence and policy binding.
- Missing CNC evidence after shift end: Availability **unavailable**, never zero.
- Completed 20-minute shift: planned **0**, Availability **unavailable (ZeroDenominator)** rather than an unsupported percentage.
- Replaying a completed shift under the same policy and exclusion evidence reproduces identical output.

## 4. Completed-shift Availability numerator admission

The planned duration is a duration-only denominator; an assumed break does **not** identify any timestamps that may be subtracted from Running evidence. Consequently `ObservedRunningDuration / AssumedPlannedDuration` is **not** by itself an admissible Availability formula. A completed shift with assumed break and no approved alignment or attribution rule returns **Unavailable(BreakAttributionNotEstablished)** for numeric Availability, even if both durations are independently numeric. Preserve the measured Running duration separately as useful machine evidence.

An explicitly versioned future numerator rule may admit measured eligible Running duration only if it establishes, with coherent interval evidence, that numerator and denominator use the same excluded time basis. No silent clipping, denominator substitution, or percentage capping. For a 480-minute shift with 480 measured Running minutes and a 30-minute assumed break, 480/450=106.67% is **not** published as Availability and must return `Unavailable(BreakAttributionNotEstablished)`; display `Running=480` and `Planned=450 (PolicyAssumed)` independently. A zero Running duration is also not automatically a valid 0% Availability until the attribution/coverage contract is established; genuine numeric zero must remain distinguishable from unavailable when that contract *is* established.

Expected-result regressions:
- Completed 480-minute shift, 480 observed Running, 450 assumed planned: **Availability unavailable (BreakAttributionNotEstablished)**, not 106.67%, not a capped 100%.
- Completed 480-minute shift, 300 observed Running, 450 assumed planned, no aligned break evidence: **Availability unavailable (BreakAttributionNotEstablished)**, not an unsupported 66.67%.
- Same shift with qualifying exact, approved aligned break evidence and validated exclusion basis: numerical Availability may be calculated under its separate explicit rule/version; missing evidence remains unavailable.
- Observed Running=0 without validated coverage/alignment: unavailable, not zero. With validated coverage/alignment, numeric zero is preserved.

## 5. Acceptance gates

Before wiring into SQL reporting or Dashboard:
1. Add policy selection/validation and provenance to the actual authoritative consumer contract.
2. Validate non-overlapping *union* of classified exclusions and break-duplication rejection at that consumer.
3. Make forecast-to-elapsed-Availability admission impossible by type or checked invariant.
4. Add executable expected-result tests for all cases listed here, especially the 480/450 counterexample, and full policy/evidence lineage.
5. Confirm CI and restored-Gajra-data behavior. No simulated timestamps or manufactured CNC evidence.

This document specifies integration behavior; it is not evidence that the existing PR #107 helper enforces these guarantees.
