# Local MTConnect simulator (development only)

A loopback-only Python standard-library source for exercising the **real** V2 MTConnect sample parser without factory access. It never connects to SQL or creates authority, runtime acknowledgements, machine configuration or evidence.

## Start

From the repository root, in PowerShell (Python 3 required):

```powershell
python .\scripts\dev\mtconnect_simulator.py --port 5055 --observations 120
```

The agent listens only on `127.0.0.1:5055`. Its exact device-scoped base URI is `http://127.0.0.1:5055/device/`; UUID is `fc2-local-sim-0001`; stream key is `mtconnect:uuid:fc2-local-sim-0001`; source instance is `2026111101`; initial retained sequence is `1`. It exposes an immutable bounded 120-observation window by default. The values are synthetic `READY`, `ACTIVE`, `STOPPED` and are not production evidence. Observation timestamps are anchored at 2026-11-11T00:00:00Z and remain byte-stable across process restarts for the same sequence. This is a fixed synthetic source, not a live production clock; changing its fixture identity or observations after SQL admission requires a new explicit source-generation commissioning/recovery process.

## Verify

```powershell
Invoke-RestMethod http://127.0.0.1:5055/health
[xml]$frame = (Invoke-WebRequest 'http://127.0.0.1:5055/device/sample?from=1&count=5').Content
$frame.MTConnectStreams.Header
$frame.MTConnectStreams.Streams.DeviceStream.uuid
```

Expected header: `instanceId=2026111101`, `firstSequence=1`, `lastSequence=120`, `nextSequence=6`; exact UUID as above. To check the idle edge, request `?from=121&count=5`: no observations and `nextSequence=121`. The API does not expose a public network port.

## Commissioning boundary

The acquisition host requires a **separately provisioned** V2 site, machine, approved mapping package, enabled operational generation, activated and acknowledged configuration, runtime/server certificate pairs, trust anchor and CRL URI. The development SQL installation remains sealed; this simulator alone cannot meet those prerequisites. Do not point the acquisition host at it until those authorities have been established. Do not use `--read` without a legitimate SQL session bearer.

Stop with Ctrl+C. No database cleanup is needed.
