# DP.3B.2B.3 — Response-completeness receipt implementation scope

Status: PROPOSED SCOPE — implementation NOT authorized. Documentation-only deliverable for review.

## Authority and baseline

Project sign-off freezes DP.3B.2B.2 at [PR 115, a783ade3](https://github.com/abilgaiyan/FactoryConnect/pull/115) for design purposes. PR 115 remains draft; no merge was authorized. This scope proposes the next bounded implementation and its acceptance gates. Approval of this document must be followed by explicit implementation authorization.

Source trace: acquisition implementation from main 5ae797f (PR 113); documentation branch based on main 925247a (commissioning documentation update). No runtime behavior is changed here.

## Objective and limits

Establish independently reproducible evidence of which source response was received, which exact request/configuration selected it, and whether every eligible source element was accounted for under an explicitly admitted parser/filter contract.

A receipt verifies traversal and reconciliation of a particular retained response. It cannot prove that an Agent reported every physical machine event, that all global Agent sequences belong to this machine, or that acquisition/state coverage spans a production interval. Response traversal is one prerequisite for the frozen assessment; contacts, parsed-row hashes and publication integrity alone remain insufficient.

## Existing seams and verified limitations

| Seam | Current behavior | Required scope change, subject to authorization |
| --- | --- | --- |
| MtConnectSampleClient.AcquireAsync | Fetches sample URI, reads response as string, passes XML to parser | Preserve reproducible source entity bytes and transport/decoding metadata before parsing; do not claim these are original network-wire bytes |
| MtConnectEndpoint.SampleUri | Constructs sample?from=<cursor> | Bind exact endpoint identity, effective URI/query and cursor semantics; no inferred device-side filter |
| MtConnectSampleParser.Parse | Reads header by local name, selects a device, sorts selected dataItemId elements by sequence | Pin namespace/schema rules, unique device selection and element-accounting policy; verify full selected subtree traversal |
| MtConnectSampleResult | Header values and parsed observations only | Carry receipt preparation evidence as a separate proposed companion; existing fields are not a response-completeness proof |
| Missing device selection | Returns empty Observations | Preserve legacy acquisition semantics, but classify receipt selection as missing rather than verified empty |
| PendingAcquisition and durable sink | Preserve response identity/contact metadata across write retry | Freeze source body, configuration/admission bindings and receipt with that same pending publication |
| SqlServerObservationIngestionStore.CommitAsync | Sole atomic ingestion writer; historical publication replay may return early | Publish/reconcile receipt and source bytes on that transaction, including replay paths before early return |

Paths above are under src/FactoryConnect.Protocols.MTConnect, src/FactoryConnect.Edge and src/FactoryConnect.Persistence.SqlServer. Proposed names below do not imply existing implementations.

## Proposed immutable evidence

| Record | Exact contents and authority |
| --- | --- |
| ResponseSource | Retained bounded HTTP entity bytes after declared transport decompression and before character/XML decoding; byte hash, byte count, status, media type, charset and content-encoding interpretation. Record which representation is hashed; decoding must reproduce the parser input |
| ResponseSelectionDefinition | Endpoint configuration identity, sanitized effective request URI/query, device selector and matching mode, expected data-item binding set, namespace/schema version allowlist, request-cursor/count/path semantics, element filtering and duplicate policy, parser/adapter identity/version/artifact digest, decoding/version and explicit resource limits |
| ResponseSelectionAdmission | Separate immutable identity binding that definition, machine/stream/device scope, effective period, admitting authority and durable source/instruction references. A definition hash is not approval |
| ResponseTraversalReceipt | Exact source/definition/admission references; acquisition publication identity; requested cursor and response header; selected device identity; ordered accounting manifest; expected parsed-output digest and final raw-row bindings; verification status/reasons and verifier version |

The selection definition must distinguish items excluded by policy, items belonging to other devices and unsupported items. No silent drop may be recorded as successfully traversed. The manifest records all relevant source elements with stable source ordinal/path, namespace/name, dataItemId, sequence, source-content fingerprint and disposition: Included(output identity), Excluded(exact policy clause), or Invalid(reason). Duplicate identities with conflicting content fail closed. Identical duplicates follow a pinned policy and cannot conceal dropped outputs.

Expected signals describe required bindings and event/filter semantics; they do not require every state-on-change signal to produce a sample in every response. A valid empty selected response can be traversal-verified while proving no state or time coverage.

Request credentials, authorization headers and URI userinfo must not be persisted. Bind a nonsecret endpoint identity and an exact sanitized request descriptor. If removal prevents reproducible selection, require an explicit descriptor/credential-reference contract before authority can be admitted. This is request provenance, not a new authentication system.

## Proposed deterministic identities and codecs

Source identity is a domain-separated SHA-256 over the declared entity representation and exact bytes. Definition/admission identities hash their full canonical immutable content. Receipt target identity binds publication identity, source identity, selection definition/admission and verifier version. Receipt result/accounting are payload content: same target with different content is a conflict, not a replacement identity.

Before implementation approval, review complete receipt/source/definition/admission field order, string/URI rules, enum codes, size limits, domain tags and golden vectors. Use explicit versioned canonical encodings with decoded-identity/content verification, bounded parsing and unsupported-version outcomes; do not reuse assessment V1 bytes for a different record. This scope does not modify the frozen assessment codec or PR 113 publication V1. The R1 clarification proposes a distinct future V2 envelope with mandatory receipt commitments, subject to review.

The existing acquisition publication is the exact external anchor. A separately stored immutable sidecar can bind its identity and hashes without editing historical publication payloads. Raw position bindings are computed/verified by the ingestion transaction, not trusted from a caller. Receipt-only mutable or independently committed writers are excluded.

## Verification algorithm to be implemented after authorization

1. Resolve the pinned definition and applicable admission. Missing admission is nonauthoritative; wrong scope/date/definition is Incomparable. Never consult the newest configuration during replay.
2. Validate complete bounded entity bytes, metadata and content hash, then decode with the pinned charset/XML rules. Parse to EOF; reject truncation, ambiguous header/device selection, unsupported namespace, unsafe entity expansion, invalid sequence fields and unaccounted selected elements.
3. Bind the actual requested cursor and complete response header. Header first/last sequences describe the Agent buffer; nextSequence describes this response's cursor advancement. A separately reviewed protocol rule defines request traversal/count/path semantics. It cannot infer missing machine events from gaps in filtered Agent sequence numbers.
4. Enumerate the selected subtree independently of the acquisition projection, classify all relevant elements, and rederive the included outputs under the pinned parser version. Compare expected output count, sequence, type, quality, timestamp and canonical value with prepared ingestion observations. A hash supplied by the acquisition caller is not independent verification.
5. Distinguish valid selected-empty traversal, absent selected device, intentionally excluded source items, unsupported elements and malformed input. No empty-list shortcut may yield VerifiedTraversal.
6. Reconcile included observations to existing/new durable positions with exact stream, instance, sequence and source content, preserving PR 113's mixed replay/new semantics.
7. Bind all source, definition/admission and receipt identities to the existing acquisition transaction and publication. Reverification on exact reads must be reproducible from retained bytes and pinned rules. Unknown parser/verifier versions return an explicit unsupported result.

Parser-version availability must be explicit. The first slice can support exactly one reviewed verifier/definition version; it must fail closed for other historical versions rather than load arbitrary assemblies or use today's parser as a substitute.

## Single-writer transaction and replay

The SQL ingestion transaction owns all writes: observations, checkpoint, contact authority, acquisition publication/head, retained source body and receipt sidecar. References must prevent a committed verified receipt without its publication, source and immutable configuration/admission. Failure at any stage rolls back all new authority. Existing publication identities/content remain unchanged.

Receipt preparation can occur before opening the SQL transaction, but source identity and rederived outputs must be checked against the exact committed batch. Contact/publication UUID, source bytes and receipt target remain frozen across retry.

Review the ingestion early-return replay branch explicitly. Identical historical publication replay must also reconcile the receipt/source intent. It must not silently ignore a changed or newly supplied receipt. A historical publication committed without a contemporaneous receipt cannot acquire one through a replay shortcut. Absent legacy receipt stays absent. Different receipt content under the same receipt target conflicts without mutation.

Definition/admission publication is distinct configuration authority, not an alternate observation writer. Scope review must settle its atomic insert-only ownership and exact retrieval before receipt implementation begins. A new migration is proposed only after authorization; allocate its ID from then-current main.

## Outcomes and exact historical reads

| Condition | Receipt outcome |
| --- | --- |
| Retained body independently traversed, selected outputs reconciled, all required admissions applicable | VerifiedTraversal for this response only |
| Explicitly absent source or selection/admission evidence, including old PR 113 publications | Unavailable(ResponseCompletenessNotEstablished), never fabricated verification |
| Device not found or ambiguous selection | SelectionMissing or SelectionAmbiguous; not verified-empty |
| Present but scope/cursor/definition/publication binding disagrees | Incomparable |
| Declared source missing, bad hash, malformed retained evidence or output/raw-row mismatch | Corrupt |
| Unsupported codec, namespace/parser semantics or verifier version | Explicit unsupported outcome; no fallback |

A source that was never retained is not the same as a declared source that has disappeared. Persist those outcomes distinctly. Exact receipt/source/configuration/admission reads take explicit identities and return Absent, Found, Corrupt or UnsupportedEncoding as appropriate. Loading a stored Found row does not itself claim current successful source revalidation.

Legacy source material may be assessed later only if its original provenance and applicable admission were independently retained and the assessment is a separately identified, explicitly historical evaluation. This slice must not retrofit verified receipt sidecars onto old acquisition publications or certify their missing source from parsed observations. Broad historical reassessment is outside scope.

## Proposed implementation work packages

These are reviewable parts of the proposed scope, not authorizations to start them.

| Part | Deliverable | Gate |
| --- | --- | --- |
| R1 | Source representation, exact selection/admission model, canonical identity/codec specification | Review authority and publication contract, golden vectors, limits and compatibility before code |
| R2 | Pure traversal verifier plus retained-response protocol companion | Parser/receipt output conformance, missing-device and namespace tests |
| R3 | Sidecar/source/configuration SQL storage integrated with existing ingestion transaction | SQL atomicity, replay, conflict, concurrency, historical retrieval and corruption proofs |
| R4 | Edge pending/recovery propagation and restart/retry tests | Original publication/source/receipt identity retained; no separate writer or receipt-only backfill |

R3 must not be treated as accepted after compilation alone. Configuration authority, source retention and receipt publication may require separate small implementation PRs after scope approval. Historical mapping/state and opening/closing certificate authorities remain subsequent work.

## Required acceptance matrix

All entries below are future test requirements; none is claimed executed by this scope PR.

| ID | Fixture/action | Required assertion |
| --- | --- | --- |
| R01 | Fully retained response, admitted definition, selected data reconciled | VerifiedTraversal; exact body, request, output and publication bindings |
| R02 | Same parsed values but different source entity bytes | Distinct source identity; changed receipt intent cannot replay silently |
| R03 | Changed endpoint/device/query/filter/parser/admission | Distinct target or explicit incompatibility; no latest substitution |
| R04 | Selected device absent versus present with no new data | SelectionMissing versus verified-empty traversal; neither invents state coverage |
| R05 | Multiple matching devices/headers or wrong XML namespace | Explicit rejection; no LocalName-only ambiguity admitted |
| R06 | Data belonging to other devices and excluded/unsupported selected items | Every relevant item accounted for; unsupported/unknown drops block verification |
| R07 | Filtered global sequences 1,9,19 | Accepted when the pinned request/selection semantics justify them; no false machine-sequence gap |
| R08 | Valid count-limited response plus successor cursor | Verification limited to that exact response window; no claim to the whole Agent buffer |
| R09 | Truncated body, invalid charset/header/sequence, resource-limit overflow | Fail closed; no verified receipt or partial authority |
| R10 | Altered included type/value/quality/timestamp/sequence | Output or raw-row reconciliation fails |
| R11 | Mixed replayed/new raw observations | Existing positions retained, only new suffix assigned, all receipt bindings exact |
| R12 | Failure at source, receipt, observation, checkpoint/contact, publication/head writes | No partial durable authority from the transaction |
| R13 | Lost acknowledgement; identical retry after newer publication | Same original source/publication/receipt identities; no new authority |
| R14 | Same publication but changed/omitted receipt through replay early-return path | Conflicting intent rejected; no silent omission or post-hoc attachment |
| R15 | Concurrent identical and conflicting receipt publication | One canonical immutable result; conflicts leave original intact |
| R16 | Restart and new configuration/parser release | Exact historical read/reverification uses pinned versions; unsupported remains unsupported |
| R17 | Source body/manifest/raw row altered or declared dependency deleted | Corrupt, never verified from stored hashes alone |
| R18 | Old publication with no body or receipt | Explicit absence/unavailable; no backfill from header/parsed rows |
| R19 | Recovery followed by successful response receipt | Event ordering retained; receipt does not erase prior loss or establish temporal coverage |
| R20 | Valid receipt but missing historical state/mapping/boundary authority | Continuity remains non-Established; no full-period zero or producer activation |
| R21 | Invalid/missing selection admission and valid content hash | Hash alone cannot admit authority; deterministic missing/incompatible outcome |
| R22 | Original receipt prepared, SQL commit fails, Edge retries | Same original UUID/contact/body/configuration/receipt retained across retries |

## R1 clarification proposal

The four scope decisions now have an explicit proposal in [the R1 contract](RR03-DP3B2B3-R1-receipt-authority-contract.md): strict ReceiptRequired retention/verification failure behavior, an exhaustive replay table, a pinned parser/selection profile, and separate immutable configuration/admission ownership. R23–R36 extend the existing acceptance matrix.

A new V2 acquisition envelope commitment is proposed for future receipt-required publications so a deleted promised sidecar cannot masquerade as legitimate legacy absence. Existing V1 records and hashes are unchanged; compatibility, canonical vectors and SQL FK/publication order still require review before implementation authorization. This is a contract proposal, not a design freeze or implementation instruction.

## R1 vectors, SQL closure and activation review

The R1 companion now has [exact golden vectors](RR03-DP3B2B3-R1-golden-vectors.json), [reproducible cross-encoder checks](RR03-DP3B2B3-R1-vector-reproduction.md) and a [concrete SQL publication/compatibility proposal](RR03-DP3B2B3-R1-sql-publication.md). These resolve the next written deliverable for review; they are not implementation or SQL-conformance evidence. The SQL proposal specifies the no-cycle FK design, mandatory closure guard, replay-time intent reconciliation and operational rollback boundaries.

Actual Gajra response namespace/device compatibility has not been established. No ReceiptRequired activation is authorized until actual source captures pass the admitted profile, operational recovery is approved and executable conformance succeeds.

## Approval and restrictions

Review must approve the record/admission ownership, source representation and retention limits, exact protocol/selection rules, sidecar replay behavior and test matrix. A complete proposed receipt codec and publication contract are required in R1 before implementation approval. This deliberately stops at scope; it is not a receipt design freeze or implementation instruction.

No production code, migration, tests or runtime configuration are delivered by this document. No change to PR 115 draft state, no merge approval, no Established promotion, no DP.3C producer, no Availability activation, no revision 534 recovery or factory deployment is authorized. GG-003 Production Day remains the first customer-visible milestone.
