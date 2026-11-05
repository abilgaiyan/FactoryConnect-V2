# FactoryConnect Dashboard — Factory LAN Deployment

## Purpose

This document defines the production-LAN deployment boundary for `FactoryConnect.Dashboard`, including the FC-029 reporting surfaces and FC-031 current-state consumption.

The governing factory-demo deployment authority is [`FC-032.4 — Factory Demo Deployment Contract`](../features/FC-032.4-factory-demo-deployment-contract.md). This guide supplies Dashboard-specific deployment detail and must not override FC-032.4 machine identity, release-manifest, startup, stop/recovery, or target-input rules.

The dashboard is a presentation host. It serves the React production assets, exposes browser-safe runtime configuration, and forwards only the explicitly mapped reporting and current-state operations through a same-origin gateway. It does not read FactoryConnect persistence directly, calculate factory metrics, or derive current machine state.

```text
browser
  ↓ same origin
FactoryConnect.Dashboard
  ├── /dashboard/config
  ├── static React assets
  └── exact reporting + current-state gateway routes
          ↓
FactoryConnect.Api
```

## Clean publish

Publish the dashboard from a clean source tree:

```powershell
dotnet publish src\FactoryConnect.Dashboard\FactoryConnect.Dashboard.csproj -c Release -o <publish-directory>
```

The publish must contain:

```text
wwwroot/index.html
wwwroot/assets/index-<hash>.js
```

The frontend is generated during publish. `src/FactoryConnect.Dashboard/wwwroot/` is generated output and is not a source-of-truth artifact.

## Production host binding

Publishing files does not by itself make the dashboard reachable from another factory-LAN machine. The deployed ASP.NET Core process must bind to a LAN-reachable interface and production environment explicitly.

A minimal environment-variable shape is:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:<dashboard-port>
Dashboard__ReportingApiBaseAddress=http://<reporting-host>:<reporting-port>/<optional-base-path>/
Dashboard__RequestTimeout=00:00:30
Dashboard__Sources__0__MachineId=<machine-guid>
Dashboard__Sources__0__ProcessorId=<processor-id>
Dashboard__Sources__0__SiteId=<site-id>
Dashboard__Sources__0__ProductionLineId=<production-line-id>
Dashboard__Sources__0__DisplayName=<display-name>
```

Additional configured machines continue with `Dashboard__Sources__1__...`, `Dashboard__Sources__2__...`, and so on.

`0.0.0.0` means the Kestrel process listens on all IPv4 interfaces available to that host. Network reachability still depends on the server's IP configuration, routing, and firewall policy. The dashboard port must be exposed only to the intended factory LAN or an equivalently restricted management/network segment; it must not be opened indiscriminately to untrusted networks.

TLS termination, certificate lifecycle, operating-system service supervision, automatic restart, startup ordering, log collection, and machine-level hardening belong to deployment infrastructure. They may be provided by a reverse proxy, Windows Service/systemd/container supervisor, or equivalent site-standard mechanism. FactoryConnect does not silently configure those concerns inside the dashboard application.

## Production configuration

Repository defaults intentionally fail closed:

```json
{
  "Dashboard": {
    "ReportingApiBaseAddress": "",
    "RequestTimeout": "00:00:30",
    "Sources": []
  }
}
```

A production deployment must provide a non-loopback absolute HTTP/HTTPS reporting API address. Configured sources may be `0..N`; when present, each source must satisfy the validated identity and presentation fields below.

The browser never receives `ReportingApiBaseAddress`. `/dashboard/config` exposes only:

```text
reportingBasePath = "/"
requestTimeoutMilliseconds
sources[] = MachineId + ProcessorId + SiteId + ProductionLineId + DisplayName + GroupName? + DisplayOrder
```

## Seven-source pilot composition fixture

The following is a shape example only. Replace every machine, processor, site, production-line and presentation identity, hostname, and port with the deployed values.

```json
{
  "Dashboard": {
    "ReportingApiBaseAddress": "http://factory-reporting.internal:5080/factoryconnect/",
    "RequestTimeout": "00:00:30",
    "Sources": [
      { "MachineId": "00000000-0000-0000-0000-000000000001", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-1", "DisplayName": "Machine 1", "DisplayOrder": 0 },
      { "MachineId": "00000000-0000-0000-0000-000000000002", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000002:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-1", "DisplayName": "Machine 2", "DisplayOrder": 1 },
      { "MachineId": "00000000-0000-0000-0000-000000000003", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000003:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-1", "DisplayName": "Machine 3", "DisplayOrder": 2 },
      { "MachineId": "00000000-0000-0000-0000-000000000004", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000004:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-1", "DisplayName": "Machine 4", "DisplayOrder": 3 },
      { "MachineId": "00000000-0000-0000-0000-000000000005", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000005:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-1", "DisplayName": "Machine 5", "DisplayOrder": 4 },
      { "MachineId": "00000000-0000-0000-0000-000000000006", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000006:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-2", "DisplayName": "Machine 6", "DisplayOrder": 5 },
      { "MachineId": "00000000-0000-0000-0000-000000000007", "ProcessorId": "operational-metrics:00000000-0000-0000-0000-000000000007:builtins-v1", "SiteId": "site-1", "ProductionLineId": "line-2", "DisplayName": "Machine 7", "DisplayOrder": 6 }
    ]
  }
}
```

Source identity is exactly `(MachineId, ProcessorId)`. `DisplayName` is presentation metadata only.

The ProcessorId in the fixture follows the production Edge authority exactly:

```text
operational-metrics:<MachineId-guid>:builtins-v1
```

The GUIDs, hostnames, ports, site/line identifiers, and display names in this fixture remain examples only. Numbered ProcessorId aliases are not valid runtime processor identities and must not be used for commissioning.

## Gateway restriction

The dashboard forwards exactly these API operations:

```text
POST /api/reporting/v1/operational-metrics/shifts/query
POST /api/reporting/v1/operational-metrics/production-days/query
POST /api/reporting/v1/operational-metrics/production-day-shifts/query
GET  /api/machines/v1/{machineId}/current-state
```

The current-state route uses the same configured `ReportingApiBaseAddress` and request timeout as the reporting routes; FC-031.5B requires no additional dashboard upstream endpoint or port.

No generic `/api` proxy exists. Near-miss paths, trailing-slash variants, extra segments, unknown API paths, and unsupported methods are not forwarded.

The gateway preserves request JSON bytes and upstream HTTP status/body/content type. It does not retry, cache, reshape, classify, aggregate, or calculate reporting data.

Upstream timeout maps to `504 Gateway Timeout`. Upstream connection/runtime failure maps to `502 Bad Gateway`. Browser cancellation aborts the upstream request.

## Health and startup

```text
GET /health/live
GET /health/ready
```

`live` proves the process is running. `ready` proves the production frontend entry asset exists. Neither endpoint probes the reporting API or current-state authority.

Startup validation rejects malformed reporting addresses, loopback production addresses, non-positive or over-five-minute timeouts, invalid configured source identity/presentation fields, and duplicate `(MachineId, ProcessorId)` pairs. `MachineId`, `ProcessorId`, `SiteId`, `ProductionLineId`, and `DisplayName` are required for each configured source; `GroupName` is optional and `DisplayOrder` must be non-negative.

## Presentation boundary

The browser may query the delivered reporting surfaces and, for configured machines, consume the authoritative FC-031 current-state response. It may format and render returned records. It must not:

- calculate Availability, Performance, Quality, OEE, utilization, or factory-wide percentages;
- infer production-day timezone boundaries;
- infer current machine state from observations or reporting data; current-state presentation must preserve the authoritative FC-031 response;
- combine metric-definition versions;
- reinterpret `unavailable` or `insufficient-evidence` as zero;
- convert failures into empty reporting results;
- inspect or synthesize continuation-token semantics.


## Current-state refresh boundary

Machine current state is loaded on initial machine-detail presentation and may be refreshed manually. FC-031 does not add dashboard polling, streaming, push updates, focus/visibility refresh, or background refresh. Those remain optional future product work.

TLS termination, certificate lifecycle, service supervision, restart policy, startup ordering, log collection, firewall policy, and host hardening remain deployment/infrastructure responsibilities rather than dashboard application behavior.
