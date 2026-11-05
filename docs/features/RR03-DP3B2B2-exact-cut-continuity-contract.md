# DP.3B.2B.2 — Exact-cut continuity assessment contract

## Formal project sign-off — 17 October 2026

Ajay Bilgaiyan explicitly approved DP.3B.2B.2 as DESIGN FROZEN in the project conversation at 10:49 IST, on reviewed baseline a783ade3b81e6e5df9ef6c223f02d225b535ff70. This records design approval only. PR #115 must remain draft and unmerged; implementation requires a separately reviewed scope and authorization. No Established promotion, DP.3C wiring, Availability activation or deployment is authorized. The 40 scenarios remain specifications, not passed tests. The next permitted work is preparing the response-completeness receipt implementation scope.


Status: DESIGN FROZEN — approved baseline a783ade3, 17 October 2026. Documentation only. No implementation, Established promotion, producer wiring or factory activation is authorized by this document or its merge.

Baseline inspected: main 5ae797f (PR 113). Review this contract and its conformance matrix before implementation authorization.

## 1. Meaning and repository boundary

Durable publication integrity, acquisition continuity, state coverage and full-period Running authority are separate claims. Established means that both acquisition and state evidence meet one explicitly admitted policy for the same positive half-open interval [start,end), scope and compatible pinned cut. It does not assert physically continuous measurement between polls.

Existing contracts inspected:

| Source | Available authority | Limit relevant to this design |
| --- | --- | --- |
| AcquisitionPublicationEvidence.cs; SqlServerAcquisitionEvidence.cs | Exact publication prefix, recovery-member identities, source-row reconciliation, legacy opening boundary | No independent response-completeness or temporal-policy admission; successful contacts alone are insufficient |
| RunningEvidenceProvenance.cs; SqlServerRunningProvenanceReader.cs | Exact aggregation contributions, eligibility/context outputs and activity-history identity; observed duration, gaps and unavailable slices | Closing/source activity reference is not a complete opening/transition/closing state proof; deliberately never emits Established |
| IMappingCoverageAuthorityStore.cs | Current processor/stream mapping authority | ReadAsync is current-state access, not an immutable historical mapping-definition/coverage lookup |
| OperationalMetricCoverageAssessment.cs and its SQL store | Prior coverage classification and immutable evidence references | Structural coverage and opaque references do not independently establish acquisition or mapping authority |

New contract types below are proposals, not claims that corresponding producers, SQL tables or verifiers already exist. PR 113 evidence can be consumed as persisted facts, but cannot be retroactively upgraded to response-completeness evidence if the necessary source material was never retained.

## 2. Exact target and authority identities

The target binds all rows of this table. Opaque IDs must be resolved and validated; possession of an ID/hash is not admission.

| Target component | Required immutable binding and validation |
| --- | --- |
| Scope | Machine ID, exact acquisition stream identity; site, production day, shift occurrence (assignment ID, shift ID, UTC boundaries); requested UTC [start,end) contained within that shift |
| Production context | Exact configuration snapshot and admission identities, schedule version, calendar version and time-zone interpretation/version; verify machine/site/date/shift applicability |
| Acquisition cut | Terminal publication identity and ordinal; ordered prefix 1..N including every recovery member and each publication content hash; retain PR 113 opening-boundary record |
| Aggregation cut | Exact MetricAggregationCheckpoint: processor, metric stream machine/key, position; processor ownership and revision membership validated |
| Lineage manifest | Immutable content-addressed list of exact contribution positions/fact identities and hashes, eligibility/context identities and hashes, activity history revision/output ordinal/fingerprint, and every raw operand binding described in section 3 |
| Temporal authority | Exact policy definition identity/version/hash, evaluator version and separate admission identity; scope, effective dates, admitting authority and durable instruction/source references |
| State authority | Exact immutable mapping definition/hash and admission, mapping processor/evaluation policy identities and versions, state-coverage manifest, and verifier version |
| Boundary authority | Exact opening/reopening and closing certificate identities/content hashes; may bind an explicitly absent authority at a pinned evidence cut to publish a non-Established assessment |
| Rules/encoding | Assessment target codec version, result codec version, continuity rule version, state verification rule version and evidence-resolution rule version |

A source binding is either Present(exact identity, content hash) or AbsentAtPinnedCut(authority kind, exact lookup target, cut). Absence is not an empty verified manifest. Later evidence requires a distinct evidence manifest and target; it cannot mutate a stored Missing assessment.

A context, policy admission or evidence change produces a distinct assessment identity. A later query must not replace any target member with the current configuration, newest acquisition head, latest mapping authority or newer aggregation revision.

## 3. Acquisition-to-aggregation compatibility proof

The verifier constructs a deterministic binding for every incorporated state fact used over the requested extent, including operands needed to prove its opening and closing boundaries:

1. Resolve the exact durable aggregation revision and contribution ledger through its position. Verify processor ownership and metric stream scope. Matching time and machine alone is insufficient.
2. Resolve each contribution's exact eligibility output, contextual activity output and original activity publication. Validate hashes, identities, state, interval, source processor and activity revision/output ordinal. Re-derive the expected fact and compare it to the incorporated fact.
3. Resolve each required raw operand as (machine, acquisition stream, durable position, Agent instance, Agent sequence, canonical observation content hash). Include opening state, every transition, quality/unavailability change, and the closing operand. A closing observation after the interval may be required, but must itself belong to the pinned source prefix and be incorporated by the pinned aggregation lineage where used by the activity calculation.
4. Locate the publication that originally accepted each raw position. Replayed appearances do not create new positions or substitute for the original binding. Verify its content and all predecessors using PR 113 exact reads. Imported legacy rows are classified as legacy operands, not assigned invented acquisition publication identities.
5. Verify every required publication ordinal <= N and every required contribution <= the exact aggregation revision. Require exact stream/instance/sequence/content equality at each join. Preserve the independently pinned cut identities; an acquisition ordinal is not comparable numerically to a metric-input position.
6. Bind a complete ordered traversal of recovery events and required source windows across the support extent. A manifest may not select only the publication members favorable to continuity.

The lineage manifest contains these joins, their original publication/operand identities and hashes, and the exact resolution cut. Verification recomputes the joins from durable sources; a caller-supplied fingerprint or asserted Complete flag does not satisfy it. Missing original acceptance/mapping provenance prevents Established. A declared member missing from persisted history is Corrupt; a legitimately absent authority is Missing/Partial with an explicit reason.

A later acquisition prefix may contain observations not used by the aggregation. They cannot supply missing state endpoints or repair that aggregation's coverage. They may be retained as acquisition facts only if the target explicitly pins them and compatibility for every required state operand still holds. No later evidence enters an existing target implicitly.

## 4. Independent source and state authorities still required

### Response validation authority

A verified acquisition window must bind the actual requested cursor, response header, endpoint/device selection, parser/adapter version, expected signal-binding set, filter semantics, and durable source material or a contemporaneous validated receipt with an immutable admission. It must attest complete traversal of the requested response cursor window under that protocol, rather than infer loss from filtered machine sequence adjacency.

The evidence must explain global Agent sequences skipped by device/signal filtering. First/last buffer numbers, an accepted next cursor and selected parsed rows are not alone proof that every required response observation was retained. If PR 113 did not retain the source needed to verify a window, report ResponseCompletenessNotEstablished. Do not manufacture this authority by hashing its existing fields.

A response receipt/admission is a new prerequisite requiring its own publication and verification review. This contract does not authorize a competing ingestion writer.

### Historical state coverage authority

A state manifest binds each required signal observation, exact historical mapping definition/admission, mapping output and state-policy evaluation. It records input quality, unavailable signals, all relevant state changes, opening state and the operand closing each duration. The verifier independently replays those rules and reconciles their time partition with the aggregation/activity evidence.

A current MappingCoverageAuthority lookup is insufficient. Exact historical mapping/admission and operand-completeness sources must be established before this verifier can certify Complete. Absence remains explicit. These are new prerequisite authorities, not aliases for DP.3B.1's activity fingerprint.

Unknown, Offline, invalid quality, conflicting mapped signals, expired state or an unresolved instance boundary make that region unavailable. A rule may map valid signals to Running/Idle/Stopped under its admitted definition; it may not relabel Unknown as a known non-Running state to obtain zero.

## 5. Deterministic temporal policy

No production tolerance or Gajra policy approval is supplied by this document. Required numeric values must be explicitly configured, admitted and pinned before evaluation. Missing values/admission produce PolicyNotEstablished; unknown versions produce UnsupportedPolicy.

| Policy field | V1 proposed semantics |
| --- | --- |
| MaxContactGapTicks | Positive maximum elapsed time between consecutive verified successful responses used for support. Equality is allowed; a larger gap creates an unresolved interval. Passing this comparison alone never certifies coverage |
| MaxStateAgeTicks | Positive maximum age of a valid initial/transition state operand for any held-state support; equality at expiry is allowed as the exclusive closing boundary, no support extends beyond it |
| MaxSourceClockErrorTicks | Nonnegative admitted bound and exact clock-calibration/evidence identity. Clock trust must itself have evidence; a configured bound alone cannot establish it |
| Time basis | UTC ticks; contact time is ingestion successful-contact time, source time is the observation timestamp. Preserve both. No use of query time, local system time, rounding or the newest contact |
| Silence rule | Silence may support a held state only with verified response traversal, expected signal/filter authority, valid unexpired state and the admitted state-on-change semantics. A heartbeat/contact alone cannot prove unchanged machine state |
| Reset rule | OutOfRange and Agent-instance changes close the current acquisition episode. V1 never bridges their loss region; only a separately proven reopening can begin later support |
| Counter reset | A quantity-counter reset is not an Agent-instance transition. Preserve/classify it separately; it neither repairs acquisition loss nor automatically invalidates unrelated Running state. Quantity/OEE semantics remain outside this assessment |
| Closing rule | A verified closing boundary at or after end must be included in the pinned evidence and justify support through end, subject to expiry, clock uncertainty and no intervening loss. A recent contact without state/response authority cannot close the interval |

Use conservative source-time support: with clock uncertainty e, a transition at t cannot establish its new state before t+e, and cannot establish the prior state after t-e. An unambiguously known interval therefore uses the conservative intersection of its independently proven endpoints. Unresolved clock uncertainty is not silently rounded away. The complete requested extent must lie within the resulting proven support partition; otherwise Partial/Missing.

Evaluate half-open intervals in integer ticks. Recovery and expiry at start require valid opening support at start; a loss at end does not invalidate [start,end) if the closing proof is independently valid. A loss strictly inside the extent or uncertainty overlapping it blocks Established. A boundary whose time cannot be bounded outside the extent remains unresolved.

## 6. Opening, reopening and closing certificates

An immutable opening certificate binds: scope, exact acquisition prefix/window receipt and admission, Agent instance/cursor, first usable raw position, source/contact time bounds, policy definition/admission, valid initial-state operands and historical mapping/state authority. Its identity hashes those exact bindings, not a free-form assertion that collection resumed.

Validate all dependencies against the pinned cuts and prove a conservative support start T. Valid initial state must be established by retained source evidence under the pinned mapping policy, not by carrying the last pre-loss state forward. Reopening cannot precede the latest unresolved loss/legacy boundary. A requested interval may begin at T or later, provided continuous subsequent support is demonstrated.

A closing certificate binds equivalent exact state/response and time evidence proving support through end; state expiry and intervening boundaries remain enforceable. Both certificates are independently verified, not trusted because their hashes match caller input.

UnverifiedLegacyPrefix remains on every historical result. A new opening may delimit an independently assessable later episode; it never changes the old prefix classification. If the loss/legacy region cannot be temporally excluded from the requested interval, Established is prohibited. Successful recovery alone cannot repair an interval intersecting a loss, even if later state is known.

## 7. Decisions and numeric authority

Return independent AcquisitionCoverage and StateCoverage axes (Complete, Partial, Missing), compatibility outcome, ordered reasons and support/unavailable/gap intervals. Invalid inputs are not forced into those three coverage values.

| Condition | Overall result | Numeric Running authority |
| --- | --- | --- |
| Compatible acquisition Complete and state Complete over the entire extent | Established | Full-period Running duration = exact sum of verified Running partition; may be zero |
| At least one usable known-state slice, but either completeness proof unresolved | Partial | ObservedRunningDuration may be reported, including observed zero; AuthoritativeRunningDuration = null |
| No usable known-state slice, including complete acquisition with entirely Unknown state | Missing | Both full-period authority and unsupported observed measurement remain null |
| Wrong scope, cut, mapping/policy admission applicability or contradictory target bindings | Incomparable | AuthoritativeRunningDuration = null |
| Bad hash/encoding structure, missing declared member, source-row mismatch or internally contradictory evidence | Corrupt | No authoritative numeric value |
| Unknown supported-format/policy/verifier version | UnsupportedEncoding / UnsupportedPolicy / UnsupportedVerifier | No authoritative numeric value; no fallback |

Established additionally requires a nonoverlapping complete partition of [start,end) with each state independently justified. Full-period zero requires every partition member to be a valid known non-Running state. Acquisition Complete alone cannot supply zero. Observed and authoritative durations are separate nullable fields; never coalesce either absence to zero.

Validation proceeds from supported envelope decoding and readable integrity checks through exact binding/admission verification to independent temporal and state proofs. The missing-prerequisite algorithm below and encoding appendix fix outcome precedence, reason codes and diagnostic ordering; evaluation order must not change the selected result.

### Deterministic missing-prerequisite decisions

An explicit AbsentAtPinnedCut binding is a missing prerequisite. A Present binding that cannot resolve, fails its hash, or omits a declared member is Corrupt; it must not be relabeled as ordinary absence. An admission that resolves but names a different scope, effective period, definition or evidence cut is Incomparable. Unknown admitted rule versions are unsupported, not missing.

Let U mean at least one independently verified known-state slice with valid historical mapping and quality. U permits a source-time observed duration, not full-period UTC authority. A raw observation or DP.3B.1 fingerprint without historical mapping verification does not establish U.

| Explicit missing/incompatible prerequisite | Axis restriction | Overall result and primary reason when this is the sole defect |
| --- | --- | --- |
| Temporal policy definition or its admission absent | Both axes NotEvaluated; no timing defaults, no assessment durations | Missing / PolicyNotEstablished |
| Response-completeness receipt/admission absent | Acquisition Missing if no verified window, otherwise Partial; state evaluated independently | Partial if U, otherwise Missing / ResponseCompletenessNotEstablished |
| Historical mapping definition/admission absent | State Missing; acquisition evaluated independently; raw status is not a verified known-state slice | Missing / HistoricalMappingAuthorityNotEstablished; observed and authoritative Running null |
| Clock calibration or clock admission absent | Acquisition and State cannot be Complete for UTC scope; each is Partial if it has otherwise verified supporting evidence, otherwise Missing | Partial if U, otherwise Missing / ClockAuthorityNotEstablished |
| Opening/reopening or closing certificate absent | Its affected axis cannot be Complete | Partial if U, otherwise Missing / BoundaryAuthorityNotEstablished |
| Policy admission present but wrong machine/stream/effective date/definition/cut | Neither axis may authorize the target | Incomparable / AdmissionScopeMismatch; both durations null |

For multiple defects, collect all inspectable diagnostics without inventing results for unreadable dependencies. Overall invalid-status precedence is Corrupt, UnsupportedEncoding, UnsupportedVerifier, UnsupportedPolicy, Incomparable. If no invalid status applies but the temporal policy/admission is absent, return Missing/PolicyNotEstablished with both axes NotEvaluated. Otherwise use U to select Partial or Missing when any prerequisite/coverage gap remains; select Established only when both axes are Complete. This is a fixed algorithm, not evaluator discretion.

Choose the primary diagnostic only among diagnostics responsible for the selected overall status. Their priority is ascending reason-code number in the encoding appendix, then authority kind, reference identity bytes and interval start/end. All other diagnostics remain retained in that same canonical order. For ordinary Partial/Missing, missing mapping outranks missing response, which outranks missing clock, then missing boundary, then temporal gaps and unavailable state. Thus two evaluators cannot select different primary reasons from the same set of missing inputs. The appendix is normative for codes and precedence.

## 8. Identity, encoding and immutable persistence

Proposed identity: SHA-256 of the complete canonical target in section 2, including exact source/admission/manifest hashes and rule versions. Result classifications and durations are content, not replacement identity inputs. Same target with different result content is a publication conflict. Corrected evidence, changed cuts or changed policy produces a distinct target, never an overwrite.

The complete proposed V1 field order, primitive encodings, authority-reference union, result payload, validation rules and generated golden vectors are specified in [the V1 encoding appendix](RR03-DP3B2B2-v1-encoding.md). This replaces the earlier deferred field specification. Both documents are frozen by the project sign-off recorded below; this appendix is not implementation authorization.

Persist assessments separately from canonical projections. Publication is insert-only and atomic; identical target and canonical payload replay succeeds without a new identity; differing content fails without mutation. Verify target hash, payload hash, versions, decoded target equality and result consistency. Concurrency permits only one canonical content per target.

Exact-target storage retrieval returns Absent, Found, Corrupt or UnsupportedEncoding. Found exposes the stored immutable assessment, including Missing/Partial if originally assessed. Separate revalidation of its pinned sources may report Corrupt/Unavailable; it never rewrites the historical assessment or silently recomputes with newer evidence. A result purporting to be newly verified authoritative must validate all pinned dependencies, not merely load a stored Found row. Unsupported rule versions cannot be evaluated by another version under the same identity.

## 9. Conformance matrix — required before implementation acceptance

These 40 scenarios are specifications for future tests, not executed tests. Fixture times/tolerances below are synthetic test inputs, not approved Gajra values. A test policy may use MaxContactGap=10 s, MaxStateAge=30 s and clock error=0, with explicit synthetic admission. Every Complete fixture must include independently verified response receipts, historical mapping/state authority, lineage and opening/closing certificates.

| ID | Scenario | Required outcome/assertion |
| --- | --- | --- |
| C01 | Exact target replay after restart and newer publications/revisions/configuration | Same identity, payload, diagnostics and numeric fields; no current/latest dependency |
| C02 | Required state operand accepted after terminal publication | Incomparable; cannot extend the pinned cut implicitly |
| C03 | Required contribution only in a later aggregation revision | Incomparable; earlier revision never borrows it |
| C04 | Same machine/time but wrong processor, stream, instance, shift or context | Incomparable with exact mismatched binding |
| C05 | Declared publication/event/manifest member missing or hash/source row altered | Corrupt; no fallback or numeric authority |
| C06 | Valid filtered Agent sequences 1,9,19 and durable positions 1,2,3 | Not a sequence-gap diagnosis merely from filtering; response completeness still independently required |
| C07 | Mixed replay/new publication | Original acceptance positions retained; replay appearances do not become new window authority |
| C08 | Intact PR 113 prefix without verified response completeness | Partial/Missing, ResponseCompletenessNotEstablished; never Established |
| C09 | Contacts 10 s apart versus 10 s + 1 tick under test policy | Equality eligible only with other proof; excess creates gap and blocks Established |
| C10 | Contact-only interval or no observations and no valid initial state | Missing; no uninterrupted-acquisition or zero-Running inference |
| C11 | State expiry exactly at end versus 1 tick before end | First eligible if other proof complete; second not Established |
| C12 | Unsupported/missing policy, missing admission or inapplicable effective scope | Explicit unsupported, PolicyNotEstablished or Incomparable as applicable; no default thresholds |
| C13 | Missing clock calibration or uncertain transition overlapping boundary | ClockAuthorityNotEstablished or Partial; uncertainty cannot expand proven support |
| C14 | Complete acquisition but Unknown/Offline/bad quality state throughout | State Missing, overall Missing; no full-period zero |
| C15 | Complete acquisition, valid state on only part of extent | Overall Partial, authoritative Running null |
| C16 | Contiguous activity geometry but missing historical mapping/state operands | Partial/Missing; adjacency never substitutes for state authority |
| C17 | Complete valid known non-Running partition | Established, AuthoritativeRunningDuration=0; observed zero distinguished from missing |
| C18 | Partial known non-Running partition | Partial, observed zero may be present, authoritative Running null |
| C19 | Complete mixed Running/non-Running partition | Established; exact tick sum agrees with independently verified state and incorporated facts |
| C20 | OutOfRange/loss strictly inside requested extent, later successful recovery | Never Established for that extent; preserve loss boundary |
| C21 | Recovery succeeds without valid reopening or initial state | Never Established |
| C22 | Valid reopening at T, complete subsequent independent proof for [T,end) | Eligible Established for later interval; original loss remains in prefix |
| C23 | Reopening established but later coverage incomplete | Partial/Missing; no full-period authority |
| C24 | Unverified legacy region intersects extent or cannot be bounded outside it | Never Established; retain explicit legacy opening |
| C25 | Legacy prefix followed by proven reopening | Later interval independently assessable; legacy classification never erased |
| C26 | Agent-instance change without durable recovery evidence | Never Established; incomparable/corrupt when declared lineage contradicts, otherwise unresolved boundary |
| C27 | Counter reset with intact independent acquisition/state coverage | Counter event does not itself repair or break unrelated Running; quantity authority remains separate |
| C28 | Loss exactly at end versus strictly before end | End-exclusive case eligible only with valid closing proof; earlier loss blocks Established |
| C29 | New acquisition evidence attempts to close an old unclosed state calculation | Incomparable/Partial; no state authority borrowed across pinned aggregation cut |
| C30 | Changed policy/admission, state authority or evidence manifest | Distinct immutable identity; same target/different payload is conflict |
| C31 | Concurrent identical publication and conflicting publication | Identical replay converges; conflicting content rejected with original intact |
| C32 | Unknown codec/policy/verifier version; malformed/trailing encoding | Explicit unsupported or Corrupt; no fallback |
| C33 | Historical stored assessment's source later deleted | Stored result not rewritten; source revalidation fails and cannot claim current authoritative verification |
| C34 | Arithmetic overlap, negative duration or Running > proven extent | Corrupt/inconsistent result rejected before publication; no clamping |
| C35 | Explicit response receipt absent, valid mapped known-state slices present | Partial / ResponseCompletenessNotEstablished; acquisition not Complete, authoritative Running null |
| C36 | Historical mapping authority absent, raw slices look known | Missing / HistoricalMappingAuthorityNotEstablished; no verified observed or authoritative Running |
| C37 | Clock admission absent, independently mapped observed slices present | Partial / ClockAuthorityNotEstablished; neither UTC axis Complete |
| C38 | Policy admission present but for wrong scope/date/definition | Incomparable / AdmissionScopeMismatch; no fallback to another admission |
| C39 | Response, mapping and clock authorities all explicitly absent | Missing; primary HistoricalMappingAuthorityNotEstablished, all three reasons retained in canonical code order |
| C40 | e=1 s, Running [0,10), Stopped [10,20) adjacent in source time; request [2,18) | Proven supports [1,9) and [11,19); unresolved [9,11), overall Partial, authoritative Running null. Never bridge uncertainty via tolerance or adjacency |

## 10. Review and implementation gate

Approval must settle the compatibility proof, temporal-policy semantics/admission, historical state authority and reopening rules above. Review response-validation receipts and historical mapping/state/boundary publication prerequisites explicitly; these authorities are not delivered by PR 113.

Only after design approval should implementation be authorized in separately reviewable slices: prerequisite historical/source authority, pure deterministic verification and codec conformance, immutable SQL publication/exact reads, then DP.3B.2C Running-reader integration. Their detailed split depends on the prerequisite trace; do not claim all are already available. No test result, green CI run or documentation merge alone authorizes Established promotion.

DP.3C, Availability publication, revision 534 recovery, API/Dashboard activation and factory acceptance remain separate gates. Issue 114 mixed-machine work must not interrupt GG-003 Production Day delivery.
