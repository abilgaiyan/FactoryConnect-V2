# DP.3B.2B.3 R1 — Response receipt authority contract proposal

Status: PROPOSED FOR REVIEW. No implementation, migration, receipt activation or production configuration is authorized. This completes the next written-contract deliverable, not an executable authority.

Parent: [receipt scope](RR03-DP3B2B3-response-completeness-scope.md). Frozen assessment baseline: [PR 115, a783ade3](https://github.com/abilgaiyan/FactoryConnect/tree/a783ade3b81e6e5df9ef6c223f02d225b535ff70/docs/features). The assessment V1 bytes remain unchanged.

## 1. Four proposed decisions

| Decision | R1 proposal |
| --- | --- |
| Retention failure | ReceiptRequired mode is strict: if complete source retention, admitted verification or transaction publication fails, do not advance observations/checkpoint/contact/publication. No automatic fallback to legacy mode |
| Replay ownership | The ingestion transaction compares original publication AND declared receipt intent before every replay early return; the table in section 3 is exhaustive |
| Parser/filter profile | One pinned, bounded, namespace-aware profile with explicit device attribute, bindings, exclusions and ordering rules; unsupported or unaccounted elements block VerifiedTraversal |
| Configuration authority | A separate restricted configuration publisher owns insert-only definitions/admissions and exact reads. It cannot insert observations, receipts, checkpoints or acquisition publications |

LegacyOperational mode retains existing ingestion semantics and publishes no receipt. It must be explicitly selected before acquisition, not chosen after a retention/verification failure. A mode change applies only to a new publication attempt, never a pending retry. Legacy periods remain missing receipt authority.

## 2. Source representation and retention

Retain the complete HTTP entity bytes AFTER declared transport decompression but BEFORE character/XML decoding. Record the original content-encoding interpretation, media type, declared charset, HTTP status, actual retained byte count and decoded-character count. Do not call these original network-wire bytes. A compressed Content-Length is not the retained-byte count.

The initial receipt profile supports HTTP 200 and strict UTF-8 XML only. Absent charset means UTF-8 under this explicit profile; declared other charset, conflicting XML declaration, invalid UTF-8 or unsupported transport coding fails verification. Preserve any UTF-8 BOM in retained bytes and hash it; the XML decoder may recognize it. Never hash reserialized XML as source identity.

Proposed implementation ceilings: 16,777,216 entity bytes; 16,777,216 decoded UTF-16 code units; XML depth 64; 262,144 elements; 64 attributes per element; 65,536 UTF-8 bytes per observation text value; 33,554,432 bytes per canonical record. The admitted definition contains positive MaxEntityBytes, MaxDecodedUnits, MaxDepth, MaxElements, MaxAttributes, MaxObservationTextBytes and MaxRecordBytes at or below these ceilings. Admission of specific values is still required; these proposal ceilings are not Gajra approval.

Count bytes while streaming/decompressing and stop before exceeding the admitted bound. Count decoded units/elements/depth while parsing; prohibit DTDs and external entity resolution. Do not allocate based solely on Content-Length or unvalidated XML lengths. Count the full selected and nonselected structure needed to prove complete traversal to EOF.

| Failure point in ReceiptRequired mode | Required behavior |
| --- | --- |
| Oversized, truncated or undecodable body before pending acquisition | Abort preparation; no ingestion commit or cursor advance; retain prior durable checkpoint; retry/error reporting remains operational only |
| Missing/inapplicable definition or admission | Do not prepare a verified receipt; no silent default profile or commit in ReceiptRequired mode |
| Namespace/device/filter/output verification failure | Do not publish verified authority; no automatic downgrade or advancement through this required-receipt attempt |
| Source/receipt DB write or any ingestion stage fails | Roll back all new observations, checkpoint/contact/publication/head/source/receipt writes |
| Commit succeeds, acknowledgement lost | Retry the exact pending publication/body/definition/admission/receipt intent; reconcile, never reacquire and substitute bytes |
| LegacyOperational selected explicitly beforehand | Existing ingestion path may proceed, with receipt absent; none of its periods gains traversal authority |

This strict proposal can stall acquisition on unsupported source input. That is an explicit review tradeoff, not a hidden fallback. Deployment selection of ReceiptRequired requires approved limits/profile and compatible source; no mode is activated by this document.

## 3. Receipt commitment and replay

A sidecar alone cannot distinguish a legitimately absent legacy receipt from deletion of a previously promised receipt. Therefore propose a versioned acquisition ENVELOPE extension for new receipt-required publications. Existing PR 113 V1 payloads, identities and hashes are not rewritten.

V1 publications have no receipt commitment and remain valid under their original contract. New Envelope V2 commits to receipt target identity and receipt payload digest in the publication content hash. The immutable sidecar references the publication; its inclusion is declared by the envelope. Missing declared sidecar/source is Corrupt.

Proposed V2 digest preimage, in order: ASCII FCAR + U16(2) + Hash LegacyEnvelopeDigest + Hash ReceiptTargetIdentity + Hash ReceiptPayloadDigest. LegacyEnvelopeDigest is the existing PR 113 V1 envelope hash recomputed from that NEW publication's unchanged acquisition payload, ordinal, predecessor and accepted boundaries. V2 does not mean that a V1 row previously existed or may be updated.

The future schema must store explicit envelope version and nullable receipt commitments with constraints: V1 has both commitments absent; V2 has both present. Updated readers verify both formats, reject unknown envelope versions and validate every declared sidecar throughout a requested prefix. Older runtime/schema compatibility is a release gate; do not claim an old reader understands V2. This is a proposed versioned extension requiring review, not an approved migration.

Receipt target can be prepared before SQL position allocation: it binds the publication ID, source identity, definition/admission and verifier. Receipt payload includes final assigned/replayed raw positions and is finalized and checked in the ingestion transaction. Its hash is then committed by Envelope V2. The transaction owns both finalization and publication; caller-provided raw positions are not authoritative.

| Existing durable state | Retry intent | Decision |
| --- | --- | --- |
| No publication; expected checkpoint CAS valid | LegacyOperational, no receipt | Publish new V1 under legacy rules |
| No publication; expected checkpoint CAS valid | ReceiptRequired with fully verified intent | Atomically publish source/receipt and new V2, or roll back all |
| V1 publication | Identical original legacy intent, receipt omitted | Existing current-state or explicit-publication replay rules apply; receipt remains absent |
| V1 publication | Any supplied receipt, even source/output apparently identical | Reject LegacyReceiptUpgrade; never attach a sidecar on replay |
| V2 publication and declared receipt/source intact | Same publication payload, mode, receipt target/content, exact source bytes/configuration/admission | Idempotent replay; return original authority without mutation |
| V2 publication and declared receipt/source intact | Changed receipt, source bytes, configuration/admission, verifier or publication content | Reject ReceiptReplayConflict; no new target may be substituted under that publication |
| V2 publication | Receipt omitted or mode changed to legacy | Reject ReceiptIntentOmitted; no early-return success |
| V2 publication | Declared receipt/source missing, corrupt or unsupported | Explicit corrupt/unsupported failure, not absence or a repair insert |
| Publication absent but stale expected checkpoint | Any new intent | Reject stale CAS; no reconstruction using current source/head |
| Any historical publication | Query uses latest configuration or a newly computed receipt | Reject as replay substitution; any separate historical reassessment is outside R1 |

Same byte hash alone is not a replay equality check: compare canonical metadata/definitions/admissions and actual source bytes, plus the exact original publication and receipt content. Recovery members, UUID and contact metadata remain immutable as in PR 113.

## 4. Verified parser/selection profile

Proposed profile ID fc-mtconnect-traversal/1; parser artifact digest, runtime semantic version and independent verifier version are mandatory definition fields. The first profile supports exact MTConnectStreams namespace urn:mtconnect.org:MTConnectStreams:2.5 only. Gajra source compatibility must be checked before configuration admission; unknown namespaces remain unsupported, not a LocalName fallback. Adding a namespace/semantic rule requires a new admitted definition/profile version.

The verifier must resolve the definition's exact endpoint identity/base URI and require the prepared request descriptor to match them under the pinned URI and request-protocol rule. Arbitrary response bytes fetched from another endpoint cannot be admitted merely because the XML/device selector matches.

Device matching is explicitly configured as NameAsciiIgnoreCase or UuidAsciiIgnoreCase. The selector is nonempty ASCII, comparison is ASCII A–Z folding only, and exactly one DeviceStream must match the designated attribute. Do not silently use name OR uuid. Record the actual selected name/uuid attributes. Zero matches is SelectionMissing; multiple matches SelectionAmbiguous. Neither is verified-empty.

Require the expected Streams root/Header structure in the exact namespace. Require one Header, valid unsigned instance/first/last/next fields and request-window consistency under a separately pinned request-protocol rule. The actual sanitized URI/query binds from/count/path and any Agent-side filtering. The current sample?from= path must not be described as device-filtered merely because the client later selects a device.

Enumerate elements in document preorder before sorting any outputs. Assign a source ordinal to every element traversed, including nonselected devices. Record selected observation elements with source ordinal, namespace/name, category, dataItemId, sequence, timestamp lexeme, content fingerprint and disposition. Structural/excluded/nonselected elements are covered by the retained body and deterministic traversal; no selected relevant element can vanish from accounting.

Every selected observation must match an explicit binding or explicit exclusion clause. A binding contains exact case-sensitive dataItemId, exact namespace/local element name, category, output type and signal semantics. No wildcard default-ignore for unknown selected items. Explicit exclusions must name their exact rule and still be accounted for. Required signal bindings do not imply each signal must emit on every state-on-change response.

| Item rule | Verified behavior |
| --- | --- |
| Samples | Pinned binding maps to Numeric; invariant decimal parsing and canonical SQL observation value serialization version must be specified |
| Events | Pinned binding maps to Enumeration; pinned whitespace/value treatment |
| Condition | Pinned binding maps to Text only if its information can be represented without loss under the admitted binding; otherwise unsupported or explicitly excluded |
| UNAVAILABLE | Pinned ASCII-insensitive token after admitted trim rule maps to null/Uncertain; never known zero |
| Duplicate selected sequence identity | Reject, including identical duplicates, in this first receipt profile; the legacy parser/dedup behavior is not silently recertified |
| Duplicate dataItemId at different sequences | Allowed when binding semantics permit; preserve every source ordinal and sequence |
| Source ordering | Retain original preorder; include output sorted by Agent sequence; ties are rejected, not resolved arbitrarily |
| Unknown element/category or conflicting binding | Unsupported/Invalid with no verified receipt; no silent drop |
| Missing device versus present device with no included new observations | SelectionMissing versus VerifiedTraversal of an empty eligible set, if all other rules pass |

The parser profile must pin timestamp parsing, decimal format, trimming and UNAVAILABLE semantics by explicit rule/version and artifact hash. R2 requires independently written traversal/output reconciliation, not merely a second call to the ingestion parser or a comparison of caller-provided hashes. Artifact availability/verification is exact and allowlisted; do not dynamically execute arbitrary historical assemblies.

A valid traversal receipt says only that this particular response was accounted for under this selection definition. It cannot independently establish an Agent's honesty, acquisition continuity, state-on-change validity or a complete production period.

## 5. Configuration definition and admission ownership

A restricted configuration publisher is the sole proposed writer for selection definitions/admissions. Its API accepts an immutable definition or admission, validates canonical identity/content, and performs atomic insert-only publication with identical replay and conflicts rejected. It has no API/permission to write MachineObservation, acquisition checkpoint/contact/history, receipt sidecars or the ingestion head.

Definition identity is a domain-separated hash of complete canonical configuration. Admission identity separately binds the exact definition digest, site/machine/stream, device selector, effective UTC [from,to), admitting authority and durable instruction/source references. Reject conflicting applicable admissions rather than selecting newest/highest arbitrarily. Receipt publication resolves an explicitly pinned admission and validates its exact scope; no caller-supplied approval flag.

Publish definitions before admissions. Admission publication verifies the referenced definition on the same SQL transaction and retains an FK. Neither record is overwritten or deleted by the application. Historical exact reads require identity/hash/version validation, not a current configuration file.

A hash proves content, not approval. The trusted admitting-authority mechanism and configuration publisher access control must be reviewed before real admissions are enabled. Test fixtures may use explicitly synthetic admitted authorities; neither project design sign-off nor a code merge supplies a customer configuration admission.

An acquisition receipt binds the exact configuration/admission IDs resolved when its pending acquisition was prepared. A retry does not reselect them. Expiry/applicability is evaluated against the original successful-contact UTC time under the admission contract, not retry/query time. This defines receipt applicability only, not temporal coverage of machine state.

## 6. Proposed canonical records and identity rules

Use a new receipt codec family, not the frozen assessment codec. Primitive grammar: fixed domain bytes, U16 version, big-endian integers, strict UTF-8 U32-length strings without normalization, RFC4122 GUID bytes, 32-byte raw hashes, I64 UTC ticks, U64 Agent/raw positions, U32 collection/byte-array lengths, byte tags 0 absent/1 present for Optional. Version 1 record payload limit is 33,554,432 bytes; source bytes additionally obey section 2. Validate lengths before allocation, enforce exact field order, reject unknown tags/trailing data, and require canonical re-encoding equality.

The following complete ordered layouts are proposals for R1 review. Context is GUID machine + String site + String acquisition stream. String values are nonempty when present except the FCEL source-attribute namespace, attribute value and exact decoded text fields, which may be empty when the retained XML has that value. Unqualified XML attributes have an empty namespace. A missing device attribute uses Optional tag 0, not an empty string. U8 denotes one unsigned byte. RequestDescriptor is String nonsecret endpoint identity + String sanitized absolute URI + String method (GET) + U64 requested cursor + Optional(U32 explicit count) + Optional(String explicit path). URI strings are exact prepared descriptors, not reconstructed from today's configuration; URI normalization algorithm/version is a definition field. No credentials/userinfo/authorization headers occur in a record.

| Record/domain | Ordered canonical fields after ASCII domain + U16(1) |
| --- | --- |
| Source/ FCRS | U16 representation code (1 decompressed HTTP entity), U16 HTTP status, String media type, Optional(String declared charset), String effective charset (utf-8), Optional(String original Content-Encoding header), String content-decoding rule/version, U32 entity-byte count, U32 decoded-unit count, U32 bytes-length + exact entity bytes |
| Definition/ FCRD | String profile ID, String profile version, String endpoint configuration identity, String admitted sanitized base URI, String URI normalization rule, U8 device match mode (1 name/2 uuid), String selector, String namespace, Hash parser artifact, String parser semantic version, String timestamp rule/version, String decimal/value codec rule/version, String trim/token rule/version, String request-protocol rule/version, seven U32 resource maxima in section-2 order, U32 binding count + bindings, U32 exclusion count + exclusions |
| Admission/ FCRA | Hash definition identity, Context, I64 effective start, Optional(I64 exclusive end), String admitting authority identity, U32 source-reference count + ordered String references |
| ReceiptTarget/ FCRT | Context, Hash acquisition publication identity, Hash source identity, Hash definition identity, Hash admission identity, RequestDescriptor, String verifier version |
| ReceiptPayload/ FCRP | Hash target identity, U64 instance/first/last/next header fields, Optional(String selected device name), Optional(String selected device uuid), U32 traversed-element count, U32 accounting count + accounting entries, U32 raw-output count + outputs, U8 outcome (1 VerifiedTraversal) |

Binding = String dataItemId + String element local name + U8 category (1 Samples/2 Events/3 Condition) + U8 output type (1 Numeric/2 Enumeration/3 Text) + String signal semantics reference. Namespace is inherited from the definition. Sort bindings by dataItemId UTF-8 bytes then local name/category; reject duplicate dataItemId bindings in this first profile.

Exclusion = String immutable clause ID + String exact dataItemId + String exact local name + U8 category. Sort by clause ID UTF-8 bytes; reject duplicate or conflicting matches, including an included binding also excluded. No wildcard syntax in V1.

Accounting = U32 source ordinal + String local name + U8 category + String dataItemId + U64 sequence + String original timestamp lexeme + Hash source-element content fingerprint + U8 disposition (1 Included/2 Excluded) + String binding or clause reference. Sort by source ordinal, reject duplicates. Selected relevant invalid/unsupported elements prevent publication, so are not encoded as successful dispositions.

Source-element fingerprint is SHA-256 over U32 preorder ordinal + exact namespace/local name strings + U32 attribute count + attributes sorted by expanded (namespace,local-name) UTF-8 bytes, each encoded as namespace/name/value Strings + exact decoded text String, under ASCII FCEL + U16(1). Reject nested observation elements in V1 rather than silently flattening their semantic structure. The retained source body remains the independent verification root; XML prefix spelling and attribute order differences still change source identity even where this element fingerprint is equivalent.

Output = U32 source ordinal + U64 sequence + U64 durable raw position + U8 output type + U8 quality (1 Good/2 Uncertain) + I64 observation UTC ticks + Optional(String canonical serialized value) + Hash canonical observation content. Outputs are ordered by sequence, unique source ordinal/sequence. Verify raw position against instance/machine/stream and actual SQL row. A replayed source observation may reference an earlier position; it does not allocate a new position.

Canonical observation content digest preimage: ASCII FCOV + U16(1) + Context + U64 instance + U64 sequence + String source (mtconnect) + String address + output type + quality + timestamp + optional serialized value, using the grammars above. Excluded elements have no Output; included accounting entries must match outputs bijectively. Count/identity equality is mandatory. Source membership in retained XML is independently rederived, not trusted from accounting data.

Identity for Source/Definition/Admission/ReceiptTarget is SHA-256 of its full domain/version/canonical bytes. ReceiptPayload content hash is SHA-256 of its bytes. A source's transport/decoding metadata is therefore part of its identity, not just its body hash. Identical body with different representation/metadata cannot silently replay. Same receipt target/different payload is a conflict. Caller-supplied hashes are recomputed.

The complete linked fixture and exact source/definition/admission/target/payload/V2 bytes are now provided in [the golden vector set](RR03-DP3B2B3-R1-golden-vectors.json), with [reproduction and verification](RR03-DP3B2B3-R1-vector-reproduction.md). Two supporting digest preimages and ten exact rejection-input recipes are included. Python and a separately written JavaScript fixture encoder matched all eight byte arrays, lengths and hashes; rejection-input recipes were reconstructed, not executed against a production decoder. Golden vectors require review before R1 freeze; no wire format or migration is approved merely by their generation.

## 7. Immutable publication and exact-read contract

The configuration publisher owns definitions/admissions only. The ingestion transaction owns source records and receipt payload/target sidecars, after validating exact configuration authority. Source dedup is insert-or-identical-content, never mutable replacement. Receipt target/content uniqueness is enforced atomically.

New receipt-required transaction order, under the existing stream CAS lock: resolve pinned definition/admission; validate pending intent and expected checkpoint; reconcile raw rows/assign new positions; finalize/reverify receipt payload; insert-or-replay source; insert receipt target/payload; insert the V2 acquisition publication with mandatory receipt commitments; advance checkpoint/contact/head as required; commit all. The [concrete SQL publication proposal](RR03-DP3B2B3-R1-sql-publication.md) defines table keys, exact raw-tuple FKs and an acyclic insert order: the target names its intended publication without a reverse FK, while the V2 publication references and commits to the target/payload. A required precommit closure guard and exact-read reverse join enforce the remaining relationship; SQL Server deferred FKs are not assumed. The proposal still requires review before implementation. No intermediate row becomes authoritative before commit.

All error paths, including receipt reconciliation on an old publication, occur before replay success is returned. A replay never inserts a sidecar missing from an original V1 publication or repairs a corrupted V2 declaration. Read and replay validation must share the same coherent SQL transaction.

Exact reads use explicit identity and return Absent, Found, Corrupt or UnsupportedEncoding; verification may additionally return Incomparable/UnsupportedProfile/UnsupportedVerifier. A V1 publication legitimately has no receipt; a V2 publication's absent declared receipt/source is Corrupt. No newest row or current parser replaces an exact identity/version. Retain all source bytes/configuration/admissions required by referenced verified receipts; application deletion/TTL of referenced records is not permitted in this slice.

## 8. Additional conformance requirements

R01–R22 in the parent scope remain required, with strict ReceiptRequired failure semantics from section 2.

| ID | Additional fixture | Required outcome |
| --- | --- | --- |
| R23 | Entity limit exactly N versus N+1, including decompression expansion | N eligible; N+1 aborts required-receipt preparation with no checkpoint/publication advance |
| R24 | Retention write fails after raw insertion | Entire SQL transaction rolls back; no fallback receipt omission |
| R25 | V1 original publication replay supplied a newly computed receipt | LegacyReceiptUpgrade rejection; original remains receipt-absent |
| R26 | V2 replay omits receipt or changes mode | ReceiptIntentOmitted rejection; no early return |
| R27 | Declared V2 receipt/source deleted, but observation payload still valid | Corrupt, never interpreted as legacy absence |
| R28 | Device name matches but configured UUID does not, or vice versa | Only the pinned match mode is used; no name-or-uuid fallback |
| R29 | Same local XML names under unsupported namespace | Unsupported profile/namespace; no VerifiedTraversal |
| R30 | Identical duplicate selected sequence versus conflicting duplicate | Both rejected in first profile; no hidden dedup certification |
| R31 | Document order differs from sequence order | Source preorder retained; outputs sequence ordered and reconciled bijectively |
| R32 | Configuration publisher attempts observation/receipt/checkpoint write | API/permission separation prevents alternate acquisition publication |
| R33 | Competing effective admissions or mismatched definition hash | Ambiguous/incompatible admission; no arbitrary newest selection |
| R34 | Same original receipt retried after admission expiry/configuration change | Original contact-time applicability and pinned IDs retained; no query-time reselection |
| R35 | Existing V1 history plus new V2 publication | Both formats validate without rewriting V1 hashes; unknown envelope versions fail closed |
| R36 | Receipt body/definition/content hash unchanged but output position/content altered | Source/SQL reconciliation or committed receipt digest fails |

## 9. Review gate

These are proposals resolving scope choices for review, not declarations that R1 is already frozen. Before authorization, approve strict failure behavior, explicit parser profile/production compatibility, trusted configuration admission ownership, and V2 receipt-membership commitment. Review the supplied golden vectors, concrete SQL FK/publication order and operational gates before corresponding implementation slices. Actual Gajra namespace/device compatibility remains unverified and is an explicit activation blocker, not inferred from Agent version or synthetic fixtures.

The explicit V2 membership proposal extends the earlier sidecar-only scope to prevent deletion being mistaken for ordinary absence. It changes only future receipt-enabled publication format; it does not amend historical V1 content or the frozen continuity assessment codec. This additional compatibility boundary requires reviewer acceptance.

No code, SQL migration, tests or runtime activation are delivered here. No Established classification, Running producer integration, Availability activation, revision 534 recovery or factory deployment is authorized.
