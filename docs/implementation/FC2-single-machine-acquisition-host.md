# Single-machine acquisition host

Executable development increment on `fc2/single-machine-acquisition-host`; final controlled-source qualification is recorded in its pull request. Uses the existing V2 ingestion schema and authority. It never installs/migrates a database, enrolls identities, enables a machine, approves a package or fabricates a runtime acknowledgement.

## Run and stop

Build with .NET 10. Supply a protected version-1 JSON configuration containing explicit existing installation/site/machine IDs, stream key, device-scoped agent base URI, SQL connection, private server/client leaf certificate and key paths, public CA DER path and allowlisted CRL URI. Paths are relative to the configuration file. All credentials must already be provisioned; protect configuration/private keys with installation-specific OS permissions. No default identities, keys or production endpoints are shipped.

```bash
dotnet run --project tools/FactoryConnect.AcquisitionHost -- --config /protected/machine.json
```

Use Ctrl+C/SIGTERM to cancel, or send `stop` on redirected stdin. Exit 0 is graceful stop; exit 20 is held/failure and requires inspection. Stdout contains metadata-only JSON status/counters/last-confirmed positions, not configuration, keys, bearer material or raw signal values. Startup/read timeout is a held failure, not successful startup.

Configuration shape (values are placeholders to replace, not commissioned identities):

```json
{
  "version": 1,
  "installationId": "11111111-1111-1111-1111-111111111111",
  "siteId": "22222222-2222-2222-2222-222222222222",
  "machineId": "33333333-3333-3333-3333-333333333333",
  "streamKey": "mtconnect:uuid:exact-device-uuid",
  "agentBaseUri": "http://controlled-agent/device/",
  "bootstrapFromSequence": "100",
  "sqlConnectionString": "<explicit connection to existing V2 database>",
  "trustAnchorPath": "ca.cer",
  "allowedCrlUri": "http://controlled-ca/runtime.crl",
  "serverCertificatePath": "server.pem",
  "serverKeyPath": "server-key.pem",
  "runtimeCertificatePath": "runtime.pem",
  "runtimeKeyPath": "runtime-key.pem",
  "pollIntervalMilliseconds": 250,
  "batchCount": 100,
  "operationTimeoutSeconds": 15
}
```

The bootstrap sequence is used only when no durable cursor exists, and must equal the first retained source sequence for the initial accepted batch. It does not claim earlier history or physical completeness. A stored cursor always wins over bootstrap. Missing pointers with retained evidence, source instance change and out-of-range/regression hold; no automatic reset/recovery is introduced.

## Process and backpressure model

This is one trusted collocated host owning SQL access and its provisioned runtime credential. A loopback-only internal mutual-TLS carrier accepts exactly its own client, closes its temporary listener after handshake, and supplies the existing owned-channel possession proof. Client/server certificates and keys are separate; server identity is explicitly pinned and validated as a live RSA server-auth leaf, not a CA. Client validation uses existing exact CA/CRL and SQL binding rules. No remote ingestion service is exposed. This deployment shape is explicit; a distributed Edge/API transport is not claimed.

The single asynchronous loop obtains current authorized context, fetches at most one bounded MTConnect frame outside SQL, transfers it, and waits for the atomic ingestion commit before another HTTP request. Queue capacity is zero; at most one batch is in flight. Existing frame/canonical/SQL/read byte bounds apply. Every iteration reads the durable cursor from SQL; memory is never progress authority.

Caught-up frames create no batch, audit or operation receipt. Idle reads still reauthorize. Transient HTTP/IO/timeouts during fetch use cancellable exponential backoff capped at two seconds and at most five retries. Unsafe source responses, stale runtime/cursor authority and SQL/transport ambiguity hold the process. No retry can silently invent acceptance. Cancellation reaches HTTP, TLS and SQL; in-flight database work commits atomically or rolls back. A last-confirmed telemetry position can lag a commit whose acknowledgement was interrupted; restart consults SQL rather than trusting that telemetry.

## Operator-visible accepted evidence

The same executable provides a simple authenticated read without starting acquisition:

```bash
dotnet run --project tools/FactoryConnect.AcquisitionHost -- --config /protected/machine.json --read
```

Send the existing 32-byte session bearer encoded as standard Base64 through redirected stdin, not the command line or configuration; interactive echoed token entry is rejected. Only its SHA-256 is passed to the existing SQL authority. Current machine-scoped `commissioning.read` is required. Output is actual accepted SQL evidence with explicit historical/not-connectivity labels, raw value, quality, native timestamp, received time, source sequence and provenance. No activity, quantity or OEE processing is added. Real sign-in/provider integration and browser presentation remain separate; no fixture identity verifier is installed in this executable.

## Runnable controlled acceptance

With an explicitly disposable `FC2_TEST_SQL` server:

```bash
dotnet run --project tests/FactoryConnect.Sql.Acceptance --configuration Release -- --host-only
```

The harness creates a fresh V2 database, uses real authenticated commissioning/activation producers and controlled HTTP/CRL peers, provisions ephemeral fixture-only key/configuration files with owner-only permissions on Unix, launches the actual host DLL in separate OS processes, and removes the generated database/files/children in `finally`. No existing selected database is dropped and no production endpoint is contacted. CI executes this command and includes the same suite in full acceptance.

Acceptance gates cover serial sustained polling/idle no-ops, observable SQL backpressure, graceful cancellation, killing a child after insertion but before commit, killing after committed progress, fresh-process restart at the SQL cursor, contiguous unique accepted source sequences, source instance/gap holds, running authority revocation and separate operator read/invalid bearer. Agent concurrency, child working set, polling throughput, full security SQL roundtrips and startup-to-first-commit recovery are measured. The fixture ceilings are one in-flight request, <=256 MiB child working set, <=5s graceful stop and <=10s first-commit recovery; these are regression gates, not production/fleet capacity claims. Evidence is written to `artifacts/ingestion/single-machine-host.json` and uploaded by CI.

Production target binding, unattended service packaging, source-instance recovery and a browser operator screen require their own operational acceptance. The V1 production system remains unchanged.
