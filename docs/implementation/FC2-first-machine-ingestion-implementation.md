# First machine ingestion: MTConnect to durable V2 evidence

Implementation on `fc2/first-machine-ingestion`. Final SQL/transport qualification results are tracked in its pull request. This is an executable controlled-source vertical slice, not a Gajra deployment or an OEE/reporting implementation. No V1 persistence, reporting, containment reader, archive loop or hosted worker is imported.

## Runnable acceptance demonstration

With .NET 10 and an explicitly disposable SQL Server connection in `FC2_TEST_SQL`:

```bash
dotnet run --project tests/FactoryConnect.Sql.Acceptance --configuration Release -- --ingestion-only
```

The command creates a uniquely named fresh V2 database, applies migrations, bootstraps fixture identities, registers a runtime certificate, grants scoped authority, assigns/enables the machine through independent approval, submits/reviews/requests/acknowledges its configuration, and starts controlled HTTP and real mutual-TLS peers. It acquires samples over HTTP, transfers the original response bytes through the mTLS channel, commits them, reconstructs clients and a fresh TLS connection from the durable cursor, and reads the actual stored values. The database is removed in `finally`; no caller-selected existing database is dropped. This command must never be pointed at a production SQL server.

`artifacts/ingestion/first-machine.json` and the `DEMO` console line show saved values and provenance, including the padded string ` 00042 `, distinct `Counter`/`counter` mappings and an explicit `UNAVAILABLE` value. These are controlled agent values, not simulated successful production metrics. CI uploads the artifact. Full CI includes this same suite.

## Selective V1 reuse

Pinned source is `abilgaiyan/FactoryConnect@088bf52492546a2a79029cf366a22f7a4d7e8d44`, under `src/FactoryConnect.Protocols.MTConnect`.

| V1 source | V2 treatment |
| --- | --- |
| `MtConnectError.cs`, `MtConnectErrorResult.cs` | Copied verbatim |
| `MtConnectProtocolException.cs` | Error result retained; exception message reduced to HTTP status/count so source text is not implicitly logged |
| `MtConnectEndpoint.cs` | HTTP(S) endpoint pattern retained; reject embedded credentials/query/fragment; explicit count 1–1000 |
| `MtConnectSampleClient.cs` | ResponseHeadersRead/error-handling pattern retained; bounded streaming read replaces unbounded ReadAsStringAsync |
| `MtConnectSampleParser.cs`, `MtConnectObservationParser.cs` | Header/sequence/category parsing adapted; exact single device UUID, raw values/attributes and canonical source identity replace case-folded name matching and trimmed/coerced observations |
| `MtConnectAcquisitionSession.cs` | Prepare-before-advance pattern retained in composition; durable cursor is SQL authority and advances only on committed ingestion |

The new standalone protocol project depends only on the .NET BCL. Neither the whole V1 Abstractions project nor its transitive persistence/runtime graph is imported. Protocol parsing accepts namespace-bearing documents, prohibits DTD/external resolution, requires valid UTF-8 and explicit source timestamps with offsets, and retains original document bytes. Attribute/value canonicalization is ASCII-escaped JSON with ordinal attribute ordering and SHA-256; whitespace and exact logical source identities are not normalized away. Raw source timestamp text remains available alongside UTC SQL timestamp interpretation.

## First-slice protocol profile

- Exact approved stream key `mtconnect:uuid:<deviceUuid>`, UTF-8, at most 512 bytes; UUID at most 256 UTF-8 bytes. No device-name fallback, case folding or identity guessing.
- Approved signal protocol `mtconnect`, address encoding `utf-8`, normalization `identity-v1`; exact address bytes at most 512. Larger/other profiles require a separate reviewed extension, not truncation.
- One exact DeviceStream per response, <=1 MiB XML and <=1000 observations. Use a configured device-scoped agent endpoint. Mixed/missing/ambiguous device responses hold rather than advancing a cursor; broader multi-device/filter/probe composition remains pending.
- Instance and sequence identities use UInt64 in .NET, decimal(20,0) in SQL and decimal strings in JSON. Gaps/out-of-range, instance change, regression, duplicate sequence or conflicting source content cannot silently advance progress.
- Bootstrap is explicit at the response's first retained sequence. Baseline provenance is retained-window-only, never proof of physical/historical completeness. A current snapshot is not used as historical ingestion.
- An empty caught-up response needs no ingestion operation: the caller can skip submission. The writer rejects it rather than appending idle receipts. No background polling loop is registered by this change.

## Durable path and authority

Migration 017 adds immutable raw batches, immutable mapped observations and a mutable current cursor. Keys preserve exact stream/address bytes, source instance/sequence, mapping, runtime, acknowledged configuration and original receipt. The operation ledger retains a bounded canonical envelope/digest rather than duplicating the full XML; completed retries also compare the immutable retained raw bytes exactly. The observation primary key deduplicates overlap; differing canonical source content conflicts instead of overwriting. A new retry operation may retain another raw receipt while existing observations retain their original provenance/mapping; configuration changes do not reinterpret older evidence.

`ReceiveMtConnectAsync` reads a four-byte big-endian length and <=1 MiB payload from an owned authenticated server-side `RuntimeMtlsChannel`, outside SQL transactions, then calls the ingestion writer. The trusted transport owner supplies the scope/cursor envelope and must bound network read time and discard malformed framing. There is no default listener or HTTP upload endpoint. Producer IO happens outside database authority gates.

The writer revalidates actual mTLS/CA evidence and current runtime/assignment/credential/operating generations, exact last acknowledgement, approved canonical revision/hash, effective interval, policy/intent/approval revocation and normalized mapping/policy/line/interval pins. A pending replacement may retain a still-eligible last acknowledgement. Suspension, resume without a fresh acknowledgement, rotation and revocation do not rebind it. Completed retries reauthorize current scope/configuration and return the saved result without resetting the cursor.

Lock order is operation ID Exclusive, installation security Shared, ordinal policy Shared, machine Exclusive. Ordinary acquisition cannot mutate authority; security mutations take installation Exclusive. Evidence, raw batch, cursor, operation receipt and audit commit together. Polling does not advance the security epoch. One set-based compare/insert handles the batch; there is no SQL connection or command per observation. An absent cursor with retained batches fails closed.

`ReadMtConnectEvidenceAsync` requires current machine-scoped `commissioning.read`, takes the shared security/machine gates, and returns at most 100 accepted observations using the exact stream key index, with an 8 MiB payload estimate checked in SQL before transferring values. An oversized page fails explicitly and can be retried with a smaller row count. It is a historical commissioning evidence read, including after a safety hold; it does not claim current connectivity, active eligibility, numeric validity, quantity or OEE. Unavailable remains explicit, and missing evidence is not a fabricated zero.

## Acceptance and limits

Real HTTP/mTLS/SQL acceptance covers raw values and exact mapping case, fresh-client/TLS reconstruction from the cursor, identical retries, overlap deduplication, conflicting source/operation identity, competing cursor updates, missing cursor, immutable retention, late audit rollback, source OUT_OF_RANGE/size denial, current scope, suspension/resume, policy revocation and both commit orders versus approval withdrawal using observed SQL waits. The 1000-observation measurement includes HTTP fetch, TLS payload delivery and the full SQL ingestion transaction; context lookup is setup and SQL roundtrips are measured for the ingestion transaction. CI budget is <=30 TDS roundtrips and five seconds in the disposable fixture, not production capacity. Whole commissioned-host/process restart, sustained fleet polling/backpressure, production endpoint binding and operator UI remain subsequent delivery work.
