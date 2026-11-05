# Slice A1 — Configurable execution normalization

A1 is opt-in production normalization before `MachineSignalMappingProcessor`.
It supplies running/not-running evidence to the existing state/activity pipeline.
Source and address are commissioning configuration; the implementation contains no
machine-specific identity. Raw observations are immutable and remain Enumeration
values in durable storage. A2 counter publication is outside this change.

## Explicit semantics

| Good-quality raw value | Digital running evidence |
| --- | --- |
| ACTIVE | true |
| READY | false |
| FEED_HOLD | false |
| OPTIONAL_STOP | false |
| PROGRAM_STOPPED | false |
| STOPPED | false |
| Any other value, including null | null, quality Uncertain |

Values are matched exactly, including case. Source/address matching is case-insensitive,
consistent with the generic mapper. Poor-quality inputs produce null Boolean evidence
and preserve their quality. An input with a configured identity but a non-Enumeration
type fails processing without checkpoint advancement. Unrelated inputs pass unchanged.

This is a running Boolean projection, not a richer activity taxonomy. With only
`state.running`, the existing evaluator produces Running for true, Stopped for false,
and Unknown for absent/uncertain evidence. Unknown values are published as uncertain
rather than omitted, so prior running evidence is not silently retained.
The existing state/activity continuity policy is unchanged; A1 adds no new guarantee
about durations across acquisition gaps. Numeric Availability/Utilization still require
valid schedule/context inputs and completed activity intervals; A1 alone does not
promise populated reports or turn scheduled duration into physical power-on evidence.

## Commissioning configuration

For a single registered stream, merge this into its `ObservationProcessing` section,
retaining BatchSize and PollingInterval:

```json
{
  "ExecutionNormalization": {
    "Source": "mtconnect",
    "Address": "M01_p1_ctl_exec"
  },
  "Mappings": [
    {
      "Source": "mtconnect",
      "Address": "M01_p1_ctl_exec",
      "SignalKey": "state.running",
      "Type": "Digital",
      "Invert": false
    }
  ]
}
```

When `ObservationProcessing:Streams` is present, put `ExecutionNormalization` and
its mapping inside the corresponding stream entry beside MachineId and StreamKey.
Every stream can select a different source/address. A global normalization block
with Streams is rejected. No normalization is enabled by default. An empty mapping
configuration retains its existing valid behavior.

The selected identity must have exactly one non-inverted Digital `state.running`
mapping. Missing/mismatched/duplicate mappings fail at composition. Production
normalization and `DemoCanonicalInputs:Enabled=true` cannot be combined.

## Existing deployments and checkpoints

Changing configuration does not rewind `canonical-mapping` or state/activity
checkpoints. Raw rows already consumed with empty mappings are not replayed by
this change. Newly accepted execution observations after the checkpoint use the new
normalizer; if the execution value does not change, a new event may not arrive yet.
Do not delete checkpoints, reset the database, or fabricate transitions to force
historical replay. A replay/mapping-revision procedure requires separate design and
authorization. No factory-server configuration or checkpoint changes are included.

Keep PART_COUNT as raw evidence. This change emits no produced quantity, good quantity,
or rejected quantity and does not enable demo synthetic quality. Performance/Quality/OEE
retain the existing evidence-dependent statuses.
