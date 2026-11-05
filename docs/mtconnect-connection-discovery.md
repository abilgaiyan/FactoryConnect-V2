# MTConnect Connection & Discovery

## Purpose

FC-012 introduces the first protocol-specific discovery implementation for FactoryConnect.

The slice connects to a configured MTConnect Agent endpoint, requests `/probe`, and converts the returned MTConnect device model into typed FactoryConnect discovery descriptors.

```text
Configured Agent URL
        ↓
MTConnectDiscoveryClient
        ↓
      /probe
        ↓
MTConnect Devices XML
        ↓
Devices + DataItems
        ↓
Discovery Result
        ↓
FC-011 Signal Mapping Configuration
```

## Endpoint Configuration

An installation can bootstrap an MTConnect endpoint from configuration such as:

```json
{
  "FactoryConnect": {
    "Connectors": {
      "mtconnect-main": {
        "Type": "MTConnect",
        "BaseUrl": "http://192.168.100.50:5000"
      }
    }
  }
}
```

`MtConnectEndpoint` normalizes the configured base URL and exposes the standard probe endpoint.

For example:

```text
http://192.168.100.50:5000
        ↓
http://192.168.100.50:5000/probe
```

A deployment with an Agent hosted below a path is also supported:

```text
http://server:5000/mtconnect
        ↓
http://server:5000/mtconnect/probe
```

## Discovery Model

The discovery result preserves protocol-specific information required for commissioning and later mapping:

- Agent instance id
- Agent version
- Device id
- Device name
- Device UUID
- DataItem id
- DataItem name
- DataItem type
- category
- subtype
- units
- owning component id/name/type

XML namespaces are resolved by element local name so compatible MTConnect namespace versions do not require hard-coded namespace strings for this discovery slice.

## Relationship to FC-011

Discovery answers:

> What devices and DataItems does this MTConnect Agent expose?

FC-011 mapping answers:

> What does a discovered source signal mean to FactoryConnect for this machine?

For example:

```text
MTConnect DataItem
id = exec
Type = EXECUTION
        ↓
commissioning / mapping decision
        ↓
Canonical FactoryConnect signal
```

Discovery does not silently assign `EXECUTION`, `AVAILABILITY`, or any other MTConnect DataItem to a business meaning. Suggested mappings can be introduced later, but the published machine configuration remains authoritative.

## Architectural Boundary

FC-012 deliberately stops at discovery.

It does not implement:

- `/current` observation collection
- `/sample` streaming/history collection
- automatic canonical signal mapping
- database persistence
- setup/admin UI
- authentication or authorization configuration
- polling orchestration
- retry/backoff policy

Those concerns build on this connection and discovery boundary.

## Error Behavior

- Non-HTTP/HTTPS Agent endpoints are rejected.
- HTTP failures remain explicit through `HttpRequestException`.
- Malformed XML remains explicit through XML parsing errors.
- MTConnect Device/DataItem elements missing required identifiers are rejected rather than silently inventing identities.

## Next Steps

The next MTConnect slices can build incrementally on this model:

```text
/probe discovery
        ↓
Mapping configuration
        ↓
/current collection
        ↓
Canonical observations
        ↓
Machine state / activity
        ↓
Production metrics and reports
```
