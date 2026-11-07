# Next delivery: one machine from protocol to durable evidence and read

User-directed priority on 7 November 2026, following commissioning discovery. Deliver executable factory data flow next, before additional framework-only increments. This target does not authorize production endpoints, credentials, deployment or migration of V1 evidence.

Start with MTConnect sample acquisition for one acknowledged V2 machine and a controlled agent fixture. Fetch outside SQL authority transactions; bind the exact approved device/stream/mapping identities; commit immutable source evidence, deduplication result and cursor atomically in the fresh V2 database; then read the saved signal values with a bounded indexed query. Provide a runnable acceptance demonstration with actual protocol values. A security-only test, schema-only test or simulated UI value does not meet this target.

## Selective V1 review

Pinned source: `abilgaiyan/FactoryConnect` commit `088bf52492546a2a79029cf366a22f7a4d7e8d44` (reviewed on 7 November). Reviewed candidates under `src/FactoryConnect.Protocols.MTConnect`: `MtConnectEndpoint`, `MtConnectSampleClient`, `MtConnectSampleParser`, `MtConnectSampleResult`, `MtConnectSampleObservation`, `MtConnectAcquisitionSession`, `MtConnectObservationParser`, `MtConnectCurrentClient` and `MtConnectCurrentParser`. Project references show MTConnect and Modbus depend only on Abstractions, but that does not authorize importing the whole abstraction project or old persistence/runtime graph.

Reuse the endpoint/sample/error-handling and prepare-before-advance patterns with explicit provenance and relevant protocol fixtures. Correct or replace these reviewed behaviors before relying on them in V2: case-insensitive name/UUID matching, trimmed/coerced raw values, unbounded `ReadAsStringAsync`, and treating an unmatched device as an empty successful parse. Preserve the original response and exact source identities with declared encoding; make ambiguous/missing identity and discontinuity explicit. A current snapshot cannot be represented as complete historical sample ingestion.

Do not import V1 report readers, containment verification, archive loops, SQL schema, workers or replay/recovery authority. Quantity/activity derivation and OEE are subsequent slices over accepted evidence, not prerequisites for showing the first durable machine values.

## Acceptance for the next implementation

- A controlled HTTP MTConnect source supplies real instance/sequence/timestamp/data-item/value fields. An authenticated, exactly assigned and acknowledged V2 runtime delivers them to storage; unavailable/revoked/mismatched authority prevents acceptance.
- Observe bounded network response/batch sizes and lossless raw retention. Exact retry produces one logical append; conflicting source identity or operation reuse rejects. Audit/receipt/checkpoint failure rolls back the whole commit.
- Restart from the committed cursor, demonstrate overlap/replay deduplication, and refuse silent advancement across instance changes or an out-of-range gap. Explicit retained-window bootstrap provenance must not claim physical or historical completeness.
- A protected read returns the actual saved values, exact source/configuration provenance and explicit unavailable/quality state. No fabricated OEE, counters or healthy-empty fallback.
- Measure full-path ingestion/read latency and SQL roundtrips with bounded batches. No per-observation connection/query loop, raw-prefix reconstruction or dashboard timeout increase. Persist CI evidence and runnable commands.

Site commissioning, operator-friendly hosting and UI presentation follow this thin working path. The original V1 production system remains unchanged.
