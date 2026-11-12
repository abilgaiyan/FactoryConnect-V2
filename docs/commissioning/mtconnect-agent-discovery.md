# FC2-COM-002 — MTConnect Agent discovery and machine commissioning

Status: implementation contract; discovery and activation are **not** implemented by PR #76.

## Reuse existing acquisition configuration

FactoryConnect already has Edge-side MTConnect acquisition. Before adding any new persistent Agent configuration, inspect the existing Edge configuration model, its storage, deployment flow and the actual URL consumer. Extend that model and its approved configuration path instead of creating a second independent list of MTConnect URLs.

An Agent endpoint and a CNC machine are different identities. One Agent can expose many devices. Store or reference the Agent configuration once; associate selected device identities with commissioned machine records. Never infer identity from a display name alone.

## Required administrator workflow

1. List existing configured Agents (URL, descriptive label and configuration status) from the authoritative configuration source. Do not expose credentials or secrets.
2. Read the Agent's MTConnect `/probe` document through a server-side, explicitly configured Agent endpoint; do not accept arbitrary URLs from an unauthenticated browser request.
3. Parse `Devices` and `Device` identities, including UUID when supplied, device name, data items and data item IDs. Treat probe data as untrusted external XML; disable DTD/external entities and bound response size and request duration.
4. Show discovered devices separately from registered machines. Indicate matched, available-to-add and ambiguous identities. Require explicit review for ambiguous matches.
5. For selected devices, register or associate the machine with an existing site and production line, then configure signal mappings through the existing configuration approval workflow.
6. Test reachability and usable observations using the existing Edge/MTConnect integration. A successful probe does not prove a live device or usable OEE data.
7. Activate acquisition only through the existing controlled activation path, after an administrator explicitly confirms the target machine and configuration. Do not activate on discovery or registration.

## Product acceptance

- One Agent with multiple devices: administrator can select a subset; no duplicate Agent URL per machine.
- Multiple Agents: distinguish devices by stable Agent and device identity; report ambiguous identity conflicts.
- Missing, malformed or unreachable Agent: actionable diagnostics, no machine registration or activation side effect.
- Existing machine: reconcile by durable configured identity; no silent duplicate registration.
- Machine assignment: site and production line are visible and editable through a controlled configuration path.
- Read-only discovery and test: no acquisition activation, no production database change.
- Once activated, Edge observations can be traced through accepted activity/quantity processing to operator screens and management OEE reports.

## Delivery order

1. Identify existing Edge Agent URL configuration and source-of-truth (V1 and V2), plus configuration publishing/acknowledgement contract.
2. Add a read-only, authenticated API that enumerates configured Agents and discovers their `/probe` devices using that configuration.
3. Add discovery and selection UI to Machine Administration.
4. Implement machine-to-line assignment and durable device-to-machine mapping using the established master/configuration schema.
5. Implement connection test, signal mapping, controlled activation and end-to-end data acceptance.

Keep production activation and deployment out of repository-only work.


## Local developer-machine live discovery (opt-in)

The V2 API can now read the existing Edge `MTConnect.Machines` entries from a trusted, absolute local file path. No Agent URL is entered through the browser and no new Agent registry is created.

In the **API process environment** set `MTConnect__EdgeConfigurationFile` to the absolute path of the existing Edge JSON configuration (for example, a local development copy of `edge.production.json`). Restart the API after changing the path. The API fails startup if an explicitly configured file is absent, malformed, duplicated by MachineId, or contains invalid machine addresses. Only MachineId, DeviceKey and BaseUri are imported; database credentials, security configuration and other Edge settings are not loaded.

The machine must already be registered in V2 with the **same MachineId** and must not be retired. Open **Administration → Machines**, select the registered machine, then choose **Discover Agent devices**. The API makes a bounded read-only `/probe` request to the configured Agent. It reports the devices and whether the configured DeviceKey has zero, one, or multiple matching devices. A potential match still requires administrator verification. Discovery never enables acquisition or saves a mapping.

The API host must be able to reach the configured Agent address from its own network context. Edge loopback URLs such as `127.0.0.1` only work when the API and Agent run on the same host; do not assume a copied production Edge configuration is reachable from a developer machine. Never activate production machines just to test discovery.
