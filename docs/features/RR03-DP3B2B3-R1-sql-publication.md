# DP.3B.2B.3 R1 — SQL publication and operational compatibility proposal

Status: PROPOSED — R1 NOT FROZEN. Documentation only; no SQL or runtime implementation authorized.

Companion: [R1 authority contract](RR03-DP3B2B3-R1-receipt-authority-contract.md). The following is the proposed concrete schema/transaction contract for review, not an applied migration. Allocate a migration ID from then-current main only after authorization.

## 1. Tables, keys and dependency graph

Identity/hash columns are binary(32), canonical payloads varbinary(max) with a 33,554,432-byte limit, codec versions positive int. Machine IDs are uniqueidentifier, ordinal stream keys retain the repository's exact varbinary identity, Agent/raw positions use decimal(20,0) constrained to UInt64. Hash and decoded-identity verification remains mandatory; a SQL key match alone is insufficient.

| Proposed table | Keys/constraints | Foreign-key dependencies |
| --- | --- | --- |
| ResponseSelectionDefinition | PK DefinitionId; canonical payload/version; identity = digest of all bytes; no application UPDATE/DELETE | None |
| ResponseSelectionAdmission | PK AdmissionId; UNIQUE(AdmissionId,DefinitionId); scope/effective-time projections must equal decoded payload | DefinitionId -> Definition |
| ResponseSource | PK SourceId; canonical payload/version, projected entity byte count within admitted/hard limits; source identity includes retained metadata and bytes | None |
| ResponseReceiptTarget | PK TargetId; UNIQUE(MachineId,StreamKeyBinary,PublicationId); UNIQUE(MachineId,StreamKeyBinary,PublicationId,TargetId); projected source/definition/admission IDs equal canonical target | SourceId -> Source; DefinitionId -> Definition; (AdmissionId,DefinitionId) -> Admission |
| ResponseReceiptObservation | PK(TargetId,OutputOrdinal); UNIQUE(TargetId,SourceOrdinal); UNIQUE(TargetId,InstanceId,Sequence); all raw binding/content projections must equal the receipt payload | TargetId -> Target; exact raw tuple described below -> MachineObservation |
| ResponseReceiptPayload | PK TargetId; UNIQUE(TargetId,ContentHash); canonical payload/version; hash = digest of complete receipt payload | TargetId -> Target |
| Existing AcquisitionEvidencePublication, extended for new V2 rows | Existing publication PK and ordinal uniqueness retained; add EnvelopeVersion and optional ReceiptTargetId/ReceiptContentHash | Existing checkpoint/predecessor FKs; (ReceiptTargetId,ReceiptContentHash) -> ReceiptPayload; (MachineId,StreamKeyBinary,IdentityHash,ReceiptTargetId) -> Target's corresponding unique key |
| Existing AcquisitionEvidenceHead | Existing stream PK and publication FK retained | No new authority shortcut; mutable head is never an exact-read substitute |

The proposed raw tuple is (MachineId,StreamKeyBinary,InstanceId,Sequence,Position). Add an exact unique index on this existing source tuple if absent, so the receipt-observation FK cannot join a sequence to one source row and a position to another. This adds an index, not a new observation writer or new raw value. Each output content hash is independently recomputed from actual source fields; the FK proves tuple existence, not semantic/hash equality.

ResponseReceiptTarget stores the intended PublicationId as data, with NO FK back to AcquisitionEvidencePublication. Instead the publication has the mandatory V2 FK to the receipt, and its composite FK ensures the receipt names that same machine/stream/publication. This removes the cycle:

Definition -> Admission; Source + Definition + Admission -> Target; Target + raw observations -> receipt bindings/payload; Target + Payload -> V2 acquisition publication; publication -> head. Here A -> B means B depends on A.

A Target alone is never authoritative. The publication transaction must perform the closure guard below, and exact receipt reads must join back to its exact V2 acquisition publication and validate both committed hashes. A dangling target is Corrupt/non-authoritative, never a Found verified receipt. The absence of the reverse FK is explicit; SQL Server is not assumed to offer deferred cyclic FKs.

The only application API owning receipt/source/raw/publication writes is the existing ingestion store. The separate configuration publisher may insert definitions/admissions but has no observation/checkpoint/contact/receipt/publication write operations or grants. Readers receive no DML grants. Any administrative bypass is outside the publication API; readers must still fail closed for corrupt/dangling declared evidence. No standalone receipt-publication endpoint is introduced.

## 2. Version and dependency constraints

Existing records stay EnvelopeVersion=1 with both receipt commitments absent. New receipt-required rows use EnvelopeVersion=2 and both commitments present. Existing acquisition PayloadVersion remains independent: V2 is an envelope extension, not a reinterpretation of old payload bytes. No existing V1 content/hash is regenerated.

Checks enforce known-version receipt shape, version/range/size bounds and positive raw/source ordinals. The application only publishes supported versions. Unknown envelope/codec versions encountered on reads produce UnsupportedEncoding before attempting a V1 hash fallback. Schema compatibility must stop an older runtime from operating against an incompatible migration; do not promise old binaries can read V2.

The V2 content hash is SHA-256 over the exact 102-byte FCAR preimage in the R1 contract. Its LegacyEnvelopeDigest is recomputed from the actual unchanged acquisition payload, ordinal, predecessor and accepted-position boundaries under the existing V1 algorithm. Never accept a caller-provided legacy digest or substitute a receipt-free hash. The vectors deliberately use a synthetic legacy digest for the wrapper test; SQL conformance must additionally exercise recomputation against real ingestion fixtures.

SQL FK guarantees: a committed V2 publication cannot lack its target/payload/source/configuration/admission dependencies, or refer to the wrong intended publication. Semantic closure guarantees: the single ingestion transaction verifies the full source, admission, body traversal, output bijection, actual raw tuple/content and hashes before commit. The manifest/array-to-row bijection is an explicit verifier/guard invariant, not falsely described as a declarative FK guarantee.

## 3. Transaction order — ReceiptRequired

Preparation outside SQL is bounded and nonauthoritative. Freeze exact request/body/definitions/admission IDs, publication UUID, original contact time and verifier version in the pending attempt. Retrying does not rerun a request or select a new configuration.

1. Open the existing ingestion connection/transaction with serializable stream CAS protection and rollback-on-error. Lock the stream checkpoint/key range using the repository's UPDLOCK/HOLDLOCK convention, including initial stream creation. Use one lock order for every ingestion path.
2. Look up the exact publication identity and envelope version. If it exists, enter the replay branch in section 4 before any new insert or CAS advancement. Do not return success after only validating raw publication content.
3. For a new publication, validate expected checkpoint, exact pinned definition/admission, original contact-time applicability and pending source/receipt intent. Definition/admission reads occur on this same transaction, retaining coherent dependency reads. Ambiguous/incompatible admissions fail closed; no latest selection.
4. Reconcile staged observations with existing raw tuples. For a new stream insert the checkpoint row as needed before FK-dependent raw inserts, but do not commit. Assign positions only to new rows; retain original positions of replayed observations. Position exhaustion fails without partial commit.
5. Recompute the included source-output manifest against those actual existing/new rows. Independently verify the retained body and exact parser/selection rules. Derive final receipt output positions/content hashes; no caller-supplied raw binding is trusted.
6. Insert-or-identical-replay ResponseSource, comparing full canonical bytes on identity match. Insert Target, observation bindings and Payload in that order. Target depends on source/definition/admission, bindings depend on already inserted raw/target rows, and Payload depends on Target. A conflicting unique identity or intended-publication key is a conflict, not an overwrite.
7. Finalize the acquisition publication's ordinal/predecessor/accepted boundaries under the existing stream authority. Recompute the legacy envelope digest and V2 wrapper hash using the finalized receipt target/content hashes. Insert the acquisition publication with its two receipt FKs. No back-reference insertion is required from Target.
8. Persist remaining checkpoint/contact/head changes under existing rules, all in this same transaction. A new head points only to a fully inserted publication.
9. Execute the precommit closure guard for every new V2 publication: exact intended-publication/target/payload reverse join; all hashes/versions/decoded keys equal; every included source ordinal has exactly one raw binding/output and every binding is declared; excluded elements have no output; all source/admission/raw dependencies valid; expected counts and accepted-position delta reconcile; no new dangling receipt target/payload. Any mismatch throws and rolls back the entire transaction.
10. Commit once. Only after success may Edge advance its in-memory session and release the pending attempt. Lost acknowledgement retries the same intent.

The closure guard is a required part of the ingestion Commit implementation and must have no bypassing success path. It is not an assumed deferred SQL constraint. Target/source rows inserted before the publication are private uncommitted rows; readers must never use READ UNCOMMITTED/NOLOCK. Exact multi-table reads use one coherent transaction/snapshot so they see either no new authority or the complete committed graph.

For a batch touching more than one shared deduplicated object, acquire hash/key locks in a fixed table order and ascending key order within each table. Configuration publication uses Definition then Admission. A deadlock/transient failure rolls back and retries the original pending intent; it never allocates a replacement receipt/publication identity. SQL concurrency tests must prove identical and conflicting attempts, not merely absence of a race in a single-thread test.

## 4. Historical replay branch

Resolve the original exact envelope before checking the current mutable head. V1 plus omitted receipt follows the existing V1 replay contract. V1 plus any proposed receipt rejects LegacyReceiptUpgrade. V2 plus omitted/changed intent rejects ReceiptIntentOmitted/ReceiptReplayConflict. Unknown version returns explicit unsupported.

For an identical proposed V2 replay: verify the whole requested original acquisition prefix and committed receipt links; resolve exact original source/definition/admission/verifier; compare canonical pending metadata/body; reconcile against raw positions bounded by the ORIGINAL AcceptedThrough, not later rows; rederive the original receipt content and compare its hash/bytes; recheck original contact-time admission applicability. Only then may replay return success. The current checkpoint/contact/head is unchanged, even after newer acquisition.

If a declared target, receipt, source, binding or configuration is missing/corrupt, fail without inserting a repair row. A replay early-return must not make absent legacy proof authoritative. Tests must inject failure and changed/omitted receipt intent precisely at this branch.

## 5. Read closure and failure behavior

Exact receipt reads begin from explicit target and publication identities, verify the reverse publication join/commitment and then all canonical/source/raw dependencies in a coherent transaction. A target with no matching committed V2 publication is Corrupt; a legitimate V1 publication has absent receipt authority. An entirely absent exact lookup is Absent. Absence of a member declared by V2 is Corrupt, not Absent. Unknown versions remain unsupported.

The configuration publisher supports only atomic insert, identical replay, conflict rejection and exact historical reads. Definition/admission hashes and byte equality are checked on both publication and retrieval. Historical versions are retained; application UPDATE/DELETE/TTL of referenced definitions, admissions, sources, receipts or bindings is prohibited. FK protection plus source/hash revalidation is required; loading a Found payload is not independent revalidation.

Rollback/failure tests must cover before/after raw assignment, source insertion, target insertion, binding insertion, payload insertion, V2 insertion, head advancement and closure guard, plus lost acknowledgement. Assert all related counts, raw positions, checkpoint/contact revisions, publication/head and receipt membership; existing immutable rows remain unchanged.

## 6. Compatibility and activation gates

Actual Gajra MTConnect namespace/device compatibility is NOT VERIFIED by this repository proposal. Agent executable version is not evidence of the emitted Streams namespace. The synthetic vector XML is not a factory capture. No network query to the factory or claim of live compatibility was made for this document.

Before any ReceiptRequired activation, obtain a provenance-recorded actual /probe and /sample response for the selected machine endpoint/device, plus status/content type/charset/encoding and exact sanitized request parameters. Protect credentials. Preserve original response bytes as evidence, identify the actual namespace and name/uuid structure, required signal IDs/categories, condition representation, request cursor/filter behavior and server limits. Reproduce traversal/output reconciliation offline under the proposed profile. A namespace other than 2.5 requires a separately reviewed profile/definition; never rewrite the capture to make it pass.

Activation additionally requires approved definition/admission and limits, retained parser/verifier artifacts, green executable pure/SQL/Edge conformance, compatible runtime/schema deployment, and explicit factory authorization. None is supplied by design approval, documentation CI or a receipt vector match.

## 7. Operational rollback without silent downgrade

- On verification/retention failure, ReceiptRequired preserves the old durable checkpoint. Surface the blocked acquisition attempt and the precise failure; do not mark the response traversed or advance a cursor to skip it.
- Pause/drain acquisition before configuration or binary changes. Retain the pending UUID/contact/source/receipt intent for exact retry where available. If commit acknowledgement is uncertain, first resolve the exact original publication and declared receipt; never guess whether it committed.
- A pending ReceiptRequired attempt cannot be retried as LegacyOperational. Resolve its original commit/replay status or explicitly abandon the UNCOMMITTED attempt under an approved recovery procedure. Abandonment cannot alter a committed publication or claim continuity over skipped source data.
- Explicitly reverting to LegacyOperational is a separately approved NEW acquisition episode/attempt using a new identity and actual request/receipt absence. Preserve checkpoint/recovery evidence, record the mode transition and leave response/continuity proof unavailable for that episode. Never hide the downgrade in a retry flag.
- If the old cursor has fallen outside the Agent buffer during a stall, use the existing durable recovery boundary for subsequent acceptance; do not advance it manually or erase the loss. Later receipt success cannot repair that interval.
- Do not drop receipt tables, reverse migrations or edit V1/V2 rows as rollback. Reverting to a binary without V2/schema support is blocked; use a compatible runtime in an explicitly approved nonauthoritative mode or stop acquisition. Existing history remains readable under the compatible verifier.

The explicit mode-transition/abandonment operational record requires its own reviewed ownership/retention detail before activation; this section is not a permission to invent or omit that record.

## 8. Acceptance gate

The dependency graph, FKs, precommit guard and replay table are ready for review as a contract. They are not SQL conformance evidence. R01–R36 remain specified requirements; no production SQL, receipt parser or Edge test is executed by this documentation change. The vectors exercise encoding, not actual acquisition completeness or factory compatibility.

R1 remains NOT FROZEN pending review/sign-off. Implementation, Established promotion, producer integration, Availability activation, revision 534 recovery and deployment remain unauthorized.
