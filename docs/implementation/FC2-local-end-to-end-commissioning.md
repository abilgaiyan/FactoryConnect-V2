# FC2 — Local end-to-end commissioning (single machine)

This is an **operator-run development procedure**, not evidence that a physical CNC has been commissioned. It is not a production deployment or permission to activate a machine. Use a disposable V2 database and an approved, isolated MTConnect test agent. Never point these steps at the Gajra V1 database.

## 1. Preconditions

- .NET 10, a reachable SQL Server 2022 Developer test instance and the V2 SQL schema installed by the existing V2 installer. Preserve the existing installation identity and SQL authorization state.
- One explicitly provisioned site, machine, MTConnect stream, approved mapping package, activation and runtime acknowledgement. The acquisition host **does not** create or approve these authorities.
- Existing runtime certificate/key pair, distinct server certificate/key pair, trust anchor and allowed CRL URI; protect private material and the JSON configuration with OS permissions.
- A known, retained MTConnect device-scoped agent endpoint and exact device UUID. Record its instance ID and retained starting sequence before bootstrap. No arbitrary cursor reset is permitted.
- Existing authenticated human session with machine-scoped commissioning read permission for the browser.

## 2. Configure one machine

Copy the configuration shape from [single-machine acquisition host](FC2-single-machine-acquisition-host.md) into a **protected local file** and replace every placeholder with approved development values. In addition to the required fields, set:

```json
"healthSnapshotPath": "./runtime/acquisition-health.json"
```

This file is local host telemetry. Place it outside web-served directories and restrict read access to the acquisition host and the API process identity. The path is resolved relative to the acquisition host configuration file.

Configure the API with the **same machine ID** and the absolute local path to that snapshot, e.g. through its development configuration provider:

```text
AcquisitionHealth:MachineId=<exact-machine-guid>
AcquisitionHealth:SnapshotPath=<absolute-path-to-runtime/acquisition-health.json>
AcquisitionHealth:MaxAgeSeconds=60
```

The API and acquisition host must be able to access the **same filesystem path**. This is a single-machine, collocated-host configuration; remote Edge-to-API snapshot transport is not implemented. Do not place credentials in dashboard code or query parameters.

## 3. Validate acquisition configuration before starting

On the local development machine, check the configuration file and provisioned certificate/key paths without opening an MTConnect connection or writing to SQL:

```powershell
dotnet run --project tools/FactoryConnect.AcquisitionHost -- --config "C:\\fc2-local\\machine.json" --check
```

A successful check reports `status: configuration-valid` and `acquisitionStarted: false`.
It validates the configuration shape and local files only; it **does not** prove runtime authorization, MTConnect connectivity, certificate trust, SQL connectivity, or that the CNC is collecting. Exit code 20 means configuration validation failed. The command does not print the SQL connection string or certificate contents.

## 4. Run the acquisition host

From the repository root, with the disposable test database and approved test agent confirmed:

```powershell
dotnet run --project tools/FactoryConnect.AcquisitionHost -- --config "C:\\fc2-local\\machine.json"
```

The host emits structured events: `started`, `idle`, `committed`, `retrying`, `held`, `stopped`. An `idle` response means the agent responded without new retained observations; it does not establish physical connectivity. A `committed` response indicates durable SQL admission. The host reads its cursor from SQL on each iteration. A source-generation mismatch or unsafe continuity holds rather than resetting the cursor.

In a separate terminal, inspect the latest local event:

```powershell
dotnet run --project tools/FactoryConnect.AcquisitionHost -- --health "C:\\fc2-local\\runtime\\acquisition-health.json" --max-age-seconds 60
```

Exit code 0 means only that the latest snapshot is a recent `idle` or `committed` host event. Exit code 20 means missing, stale, malformed or non-operational status. It does **not** prove current CNC activity.

## 5. Verify accepted evidence, processing and dashboard

1. With a valid existing machine-scoped commissioning session, use the host's `--config ... --read` command. Send the Base64-encoded 32-byte bearer **through redirected stdin**, not command-line arguments. Verify accepted observation source sequence and provenance. Do not log the bearer.
2. Run the existing V2 activity-processing host for this commissioned machine using its own documented configuration and runtime authority. Confirm that published activity is based on accepted evidence; acquisition alone does not create activity periods.
3. Start the existing V2 API and React dashboard using the development launch configuration. Sign in using the existing authenticated flow.
4. Open **Published activity history** for the exact machine. Confirm verified processed checkpoint and any published closed periods. Historical state is not a live machine-state claim.
5. Select **Check acquisition health**. Verify the host event, snapshot age, last MTConnect source response and last durable SQL admission. Optionally enable visible-tab-only 30-second refresh. A 503 means health is not configured/available, not that a CNC is necessarily disconnected.

## 6. Acceptance record

Record the exact V2 commit, test database name, installation/site/machine IDs, stream key, approved package/acknowledgement IDs, agent endpoint identity (no credentials), source instance and retained sequence, first and last durable cursor, host health events, activity checkpoint, dashboard screenshots and any held reason. **PASS** requires verified durable evidence admission, corresponding processed activity, and authenticated dashboard visibility for the same machine. If any stage is absent, record **PARTIAL**, not PASS.

## 7. Stop and recover safely

Stop the acquisition host with Ctrl+C or `stop` on redirected stdin. Exit 0 means graceful stop; exit 20 means held/failure. Never delete SQL evidence, alter the durable cursor, reset a source generation, or bypass approval/activation to force progress. Investigate the held reason and preserve the snapshot and logs. No production activation, automatic Windows startup or unattended restart is authorized by this guide.
