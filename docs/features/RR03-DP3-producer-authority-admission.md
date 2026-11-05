# RR03-DP.3 — Producer authority trace and admission boundary

Status: proposed design / implementation prerequisites identified. This is not a working producer or reporting activation.

Trace baseline: main `eaf1e08b1fc89a8392214c2ca3b8152f1bcf2838`.

## Verified source seams

| Concern | Existing seam | What it establishes | Remaining boundary |
| --- | --- | --- | --- |
| Exact aggregation revision | `IMetricAggregationRevisionReader.ReadExactAsync`; `SqlServerMetricAggregationRevisionReader.cs` | Exact processor/stream/position exists; persisted shift identities and production-day ownership are validated | No newest-revision substitution; caller's requested shift/day must match the admitted revision context |
| Historical operands | `IRevisionedOperationalMetricComponentSnapshotReader.ReadAtRevisionAsync`; `SqlServerRevisionedOperationalMetricComponentSnapshotReader.cs` | Reconstructs operands from the contribution ledger through the requested revision, after verifying the exact revision exists | An operand value alone is not a full coverage/completeness claim; absent Running must remain absent |
| Schedule | `IShiftScheduleReader`; `EdgeProductionMetricInputServiceCollectionExtensions.cs` | Effective-dated shift assignments and exceptions from startup configuration | No immutable schedule-version/calendar-version exact-read contract or durable revision-to-configuration binding |
| Planned exclusions | `IPlannedProductionScheduleReader`, `PlannedProductionIntervalResolver` | Configured windows, fixed break windows and calendar overrides | No durable assumed-break exclusion manifest; fixed break windows cannot be treated as unrelated exclusions and then charged the default allowance again |
| Demo policy | `AssumedBreakReportingConsumer` | Validates supplied candidate policies | No Edge assumed-break policy source/admission registration or immutable effective-version lookup; policy validation is not policy authority |
| Schedule ownership | `IMachineShiftOccurrenceRosterStore` | Machine/day/line/occurrence ownership and a current roster revision | Current roster reads do not supply an exact historical schedule/calendar snapshot |
| Assessment storage | `SqlServerAssumedBreakAssessmentStore` | V1 validation, immutable publication and exact-target retrieval | Preserving reference strings does not establish source authority |

Do not replace missing authorities with `schedule-v1`, `calendar-v1`, an arbitrary Running reference, a hash of an unverified exclusion list, or the newest configuration. A configuration content hash identifies bytes; it does not prove admission, completeness, or historical applicability.

## Required producer input

The public production request must select an exact durable aggregation checkpoint plus machine/day/shift scope and exact admitted authority references. It must not accept calculated Running duration, arbitrary exclusion intervals, prebuilt assessment targets or caller-supplied evidence fingerprints as authoritative inputs.

Before producer implementation, establish a retrievable immutable authority bundle covering:

- Machine, site, production date, exact shift occurrence and schedule assignment; immutable schedule and calendar versions with exact resolved UTC boundaries.
- The explicitly admitted effective policy version, admission mode, 30-minute allowance and instruction reference. DemoProvisional must be restricted to its admitted machine/effective range.
- Classified exclusions with durable identities and exact source versions, including an explicit complete-empty exclusion manifest. Missing exclusions must not be interpreted as an empty set.
- The requested forecast/completed stage and the authority for that stage. Wall-clock passage alone must not establish machine evidence completeness.
- The exact aggregation revision binding and Running evidence provenance; any full-period coverage claim must be independently validated. Partial observed Running may be retained only with its explicit evidence extent/status.

The authority source must retain earlier admitted versions so restarts can resolve the same references after configuration changes. A new snapshot must never be silently bound to an old work item. This can be implemented as an immutable admitted configuration/authority store; selecting an existing store or adding one requires an explicit contract and tests. The generic AssessmentAuthority verifier is not a persisted Gajra schedule/policy source by itself.

## Fail-closed publication sequence

1. Read the exact requested aggregation revision; validate processor, stream, machine and relevant shift/day ownership. Absence, corruption or incomparable evidence returns a producer diagnostic without publication.
2. Resolve the exact admitted schedule/calendar/policy/exclusion bundle. Validate machine scope, effective dates, source completeness, stage and conflicting authorities. No latest fallback.
3. Read Running operands and provenance at that same aggregation revision. Missing Running may produce a durable assessment with nullable Running when all required planned-time authorities are established. Never turn an absent operand into numeric zero.
4. Invoke the existing calculation/admission consumer. Build V1 target fields and evidence fingerprints only from these resolved sources. Validate the complete durable assessment before publication.
5. Publish through `IAssumedBreakAssessmentStore`. Identical replay succeeds; conflicting content, corruption and unsupported encodings remain explicit failures without mutation.
6. Resume the same pinned work item on restart. If a process dies after insert and before recording progress, replay the same identity. Do not infer progress from a scope's latest assessment row.

## Diagnostic versus durable assessment

A valid schedule/policy/exclusion binding with missing Running can publish `RunningEvidenceNotEstablished` (or the defined forecast/zero-denominator precedence), retaining planned time. A missing policy cannot be solved by inventing mandatory policy fields for the V1 target. Likewise, an absent/corrupt aggregation revision cannot be represented as an authoritative durable target merely because its requested checkpoint is known. These are producer-level unavailable outcomes and must not call PublishAsync.

## Required acceptance tests

- Real SQL aggregation revision and contribution fixtures; exact historical reconstruction still selects the original cut after later revisions are appended.
- Absent revision, wrong processor/stream, mismatched ownership, corrupt source and missing authority: explicit diagnostics and no assessment row.
- Restart with configuration changed: original pinned authority remains retrievable; no newest fallback.
- Schedule/calendar/policy/exclusion changes: distinct appropriate V1 identities and historical exact reads.
- Missing Running versus evidence-backed zero, with full-period coverage distinguished from partial observations.
- Missing exclusion manifest versus admitted empty manifest; overlapping exclusions unioned once; duplicate break authority rejected.
- Forecast/completed identities separate; no stage promotion inferred from current time on replay.
- Process interruption after publication, exact retry, and competing identical/conflicting publications: one immutable winner.
- SQL round trips through RR03-DP.2 and exact-target reads. Test fixtures are evidence for tests only, never factory authority activation.

## Scope and next implementation gate

RR03-DP.3 remains incomplete until an actual authority resolver, exact Running provenance reader and pinned recovery path are implemented against these sources. This trace adds no worker, migration, service registration, fabricated reference, API/Dashboard wiring or factory configuration. Resolve the immutable schedule/calendar/policy/exclusion authority contract first, then implement the producer on that authority. Preserve canonical operational metric projections.

Revision 534 recovery, freshness and restored-Gajra end-to-end validation remain separate release gates.
