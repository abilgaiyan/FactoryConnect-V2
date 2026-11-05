# MOXA status normalization through MTConnect

The commissioned `DEWAS-SR-MOXA-001` adapter reads MOXA ioLogik E1210 discrete
inputs using Modbus/TCP function 02, start address 0, quantity 16, Unit ID 1.
The separate MTConnect agent exposes its data on localhost port 5002; its
SHDR adapter listens on localhost port 7889. The verified module address is
192.168.127.254:502 (MAC 50:0B:88:0B:BA:C4).

This change adds opt-in processing for the adapter's `moxa001_status`
Enumeration event. It does not implement direct Modbus acquisition, install
or supervise the commissioning adapter, edit production configuration, or
add part count/OEE assumptions. `FactoryConnect.Protocols.Modbus` currently
contains only a project file.

## Source contract and interpretation

Exactly one of DI00/DI01/DI02 may be active. The adapter must compute a single
status from each successful read. It must not retain the previous status after
a failed read. The state processor consumes the status event, not independent
DI events: duplicate filtering and batches can otherwise expose intermediate
combinations or retain a previous active Boolean signal.

| DI00 / DI01 / DI02 | Adapter status | Normalized `state.operational` | Existing MachineState |
| --- | --- | --- | --- |
| 1 / 0 / 0 | RUNNING | RUNNING | Running |
| 0 / 1 / 0 | IDLE | IDLE | Idle |
| 0 / 0 / 1 | ALARM | FAULT | Fault |
| 0 / 0 / 0 | OFF | STOPPED | Stopped |
| Multiple active | INVALID | null, Uncertain quality | Unknown |
| Read failure | UNAVAILABLE | null, non-Good quality | Unknown |

OFF is Gajra's agreed interpretation of all-zero inputs; it is not proof of
physical power isolation. The current domain has no distinct Off state, so it
maps to Stopped. The raw OFF event is retained. Unknown is not Offline: the
existing current-state contact/continuity/freshness authority continues to
determine whether trustworthy current state is available.

Exact, case-sensitive adapter values are accepted. Unknown, null, INVALID,
lower-case, or poor-quality input removes operational state evidence. It must
never fall back to old Running/Idle/Fault Boolean evidence. A raw `UNAVAILABLE`
event is parsed as null with Uncertain quality by the existing MTConnect parser.

The adapter's AVAILABLE observation means the source read succeeded, even for
OFF or INVALID; it does not mean the machine is producing. Preserve raw DI00,
DI01, DI02 and status observations in ingestion. This feature maps only the
single status observation. Positions, instance IDs, sequence numbers, source,
address and timestamps are preserved; no mapped fan-out or synthetic raw
observations are introduced.

## Opt-in configuration

For a single-stream processing configuration:

```json
{
  "ObservationProcessing": {
    "BatchSize": 100,
    "PollingInterval": "00:00:01",
    "Mappings": [
      {
        "Source": "mtconnect",
        "Address": "moxa001_status",
        "SignalKey": "state.operational",
        "Type": "Enumeration",
        "Invert": false
      }
    ],
    "MoxaStatusNormalization": {
      "Source": "mtconnect",
      "Address": "moxa001_status"
    }
  }
}
```

For GG-003 plus MOXA, use `ObservationProcessing:Streams` entries for **both**
registered streams. Each entry has its actual `MachineId`, exact `StreamKey`,
`Mappings`, and applicable normalization. Keep GG-003's existing Digital
`state.running` mapping and `ExecutionNormalization` inside its entry. Put the
MOXA mapping and `MoxaStatusNormalization` inside the MOXA entry. Remove the
old global `ExecutionNormalization` from the effective configuration when
migrating to Streams. Do not merely append the new machine to the current
single-stream settings.

The MOXA stream key is `mtconnect:DEWAS-SR-MOXA-001`; its MachineId must be a
new, recorded identity, not GG-003's identity. Acquisition uses BaseUri
`http://127.0.0.1:5002` and DeviceKey `DEWAS-SR-MOXA-001`. These are only the
acquisition/normalization inputs: review the complete application inventory,
production processing, context, metrics and API/dashboard configuration before
running a two-machine Edge. This document is not a deployable production overlay.

Composition rejects:

- A global MOXA normalization when per-stream configuration is present.
- `state.operational` without explicit MOXA normalization.
- Missing, mismatched, duplicate or inverted status mappings, or a non-Enumeration type.
- MOXA and FANUC execution normalization in the same stream.
- Boolean Running/Idle/Fault mappings mixed with the atomic status in that stream.
- Demo normalization combined with the production MOXA profile.

Do not change an already-processed stream's mapping semantics in place and
expect its durable processing checkpoints to replay automatically. Introduce
this profile with the new machine/stream and validate restoration separately.

## Validation and commissioning boundary

The automated tests exercise raw preservation, identity/type/quality checks,
strict status values, invalid configuration, and mixed FANUC/MOXA durable
processing across Running, Idle, Fault, Stopped, Unknown and recovery. Existing
FANUC normalization tests must continue to pass.

```powershell
dotnet test tests/FactoryConnect.Edge.Tests/FactoryConnect.Edge.Tests.csproj `
  -c Release --filter "FullyQualifiedName~MoxaStatusNormalizationTests|FullyQualifiedName~ExecutionNormalizationTests" `
  --results-directory artifacts/TestResults/moxa-normalization `
  --logger "trx;LogFileName=edge-normalization.trx"

dotnet test tests/FactoryConnect.Core.Tests/FactoryConnect.Core.Tests.csproj `
  -c Release --filter "FullyQualifiedName~MachineStateEvaluatorTests" `
  --results-directory artifacts/TestResults/moxa-normalization `
  --logger "trx;LogFileName=state-evaluator.trx"
```

Factory observations already establish Modbus reads, MTConnect publication of
RUNNING/OFF, adapter disconnect to UNAVAILABLE, and reconnection to valid input.
A failed-endpoint adapter published UNAVAILABLE while SHDR was established, but
those values were already unavailable: a fresh valid-to-failure transition in
one adapter session is still pending. Alarm verification against the actual
machine, sustained acquisition, direct SQL persistence acceptance, invalid-input
validation on hardware, and automatic startup are not established by this PR.

The temporary 192.168.127.250 computer address was added to ActiveStore; access
to the current MOXA subnet must be made durable or the module moved to a
network-admin-reserved plant IP before unattended operation. Do not replace the
existing 192.168.1.59 computer address or GG-003's 192.168.1.101 endpoint.
