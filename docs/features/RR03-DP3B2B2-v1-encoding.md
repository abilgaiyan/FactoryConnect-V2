# DP.3B.2B.2 — Assessment V1 encoding appendix

## Formal project sign-off — 17 October 2026

Ajay Bilgaiyan explicitly approved DP.3B.2B.2 as DESIGN FROZEN in the project conversation at 10:49 IST, on reviewed baseline a783ade3b81e6e5df9ef6c223f02d225b535ff70. This records design approval only. PR #115 must remain draft and unmerged; implementation requires a separately reviewed scope and authorization. No Established promotion, DP.3C wiring, Availability activation or deployment is authorized. The 40 scenarios remain specifications, not passed tests. The next permitted work is preparing the response-completeness receipt implementation scope.


Status: DESIGN FROZEN — project sign-off on 17 October 2026. Normative companion contract to [the continuity contract](RR03-DP3B2B2-exact-cut-continuity-contract.md). These specifications and vectors authorize no implementation or Established promotion. They define the assessment's reference encoding; the separately reviewed authority producers must define and verify the referenced manifest/definition content.

## Primitive grammar

All concatenation is in the listed order, with no padding, terminators, field names, implicit defaults or platform serialization. Integers use big-endian bytes; signed integers use two's complement. U16/U32/U64 are unsigned 2/4/8-byte integers; I32/I64 are signed 4/8-byte integers. Byte is one byte. A boolean/tag has only its enumerated values. Hash is exactly 32 bytes, not hex text. GUID is exactly 16 RFC 4122 network-order bytes (not the default .NET mixed-endian byte array). String is U32 byte count followed by strict UTF-8, with no normalization, BOM or replacement of invalid surrogates. Empty strings are forbidden except the diagnostic identity for authority kind 0. Case is significant. No type names are serialized.

Optional(T) is Byte 0 for absent, or Byte 1 followed by T. Interval is I64 start UTC ticks + I64 end UTC ticks, with 0 <= start < end <= DateTimeOffset.MaxValue.UtcTicks. Durations are I64 ticks >= 0; no floating point or rounding. Production day is the .NET DateOnly DayNumber in I32. Strings are bounded to 65,536 encoded bytes, counts to 1,000,000 members, and each complete target/result payload to 16,777,216 bytes. Check lengths/counts before allocating or reading. Reject negative/out-of-range values, truncation, unknown tags, duplicate keys, invalid ordering, trailing bytes and any decode/re-encode mismatch.

Exact reference R(kind) is U16 kind, then Byte tag:

- 1 = Present: String immutable identity, String version, Hash canonical content digest.
- 2 = AbsentAtPinnedCut: String exact lookup-target key, Hash immutable source-cut digest.

No tag 0, null reference or empty manifest substitutes for absence. R includes its expected kind even in fixed positions, preventing cross-kind substitution. A lookup key must be the canonical lookup-target identity defined by its authority contract, not prose. A present digest is a content binding, not approval. A missing declared Present member is corruption. A source-cut digest must resolve to the exact cut described by that authority; it is not an authorization token.

## Complete target field order

Target identity = SHA-256(target bytes). Do not hash the display hex, JSON, result, evaluation time or SQL row metadata. Every field below is mandatory unless its grammar explicitly says Optional or R(tag 2).

| Ordinal | Field | Encoding and invariants |
| --- | --- | --- |
| 1 | Domain | Exactly ASCII bytes `FCCT` (46 43 43 54) |
| 2 | TargetCodecVersion | U16 = 1 |
| 3 | ResultCodecVersion | U16 = 1 |
| 4 | Machine | GUID, nonempty |
| 5 | AcquisitionStreamKey | String, exact ordinal key |
| 6 | Site | String |
| 7 | ProductionDay | I32 DayNumber |
| 8 | ShiftAssignment | String |
| 9 | ShiftId | String |
| 10 | ShiftExtent | Interval |
| 11 | RequestedExtent | Interval, contained in ShiftExtent |
| 12 | ScheduleVersion | String |
| 13 | CalendarVersion | String |
| 14 | TimeZoneId | String |
| 15 | TimeZoneRuleVersion | String |
| 16 | TerminalAcquisitionPublication | Hash identity |
| 17 | TerminalPublicationOrdinal | I64 > 0 |
| 18 | AcquisitionPrefixContent | Hash of complete ordered prefix manifest, including all recovery members |
| 19 | OpeningKind | Byte 1 NoPriorDurablePrefix; 2 UnverifiedLegacyPrefix |
| 20 | LegacyAcceptedThrough | Optional(U64 > 0) |
| 21 | LegacyCheckpoint | Optional(U64 AgentInstance + U64 NextSequence), same acquisition stream as fields 4/5 |
| 22 | AggregationProcessor | String |
| 23 | AggregationMachine | GUID; must equal field 4 |
| 24 | MetricStreamKey | String |
| 25 | AggregationPosition | U64 > 0 |
| 26–39 | Authority references | Fourteen R values in exact kind order 1..14 from the following table; no count prefix |
| 40 | ContinuityEvaluatorVersion | String |
| 41 | StateVerifierVersion | String |
| 42 | EvidenceResolutionVersion | String |

OpeningKind 1 requires fields 20/21 absent; kind 2 requires at least one present. This describes durable history, never completeness. A target must have a syntactically valid terminal acquisition identity/cut to be encoded by V1; absence of that terminal acquisition history is a resolver-level Missing outcome, not an invented publication or an assessment published under a fabricated target. Other missing authorities can be bound using R(tag 2).

| Kind | Fixed reference role |
| --- | --- |
| 1 | Production configuration snapshot |
| 2 | Configuration admission |
| 3 | Acquisition-to-aggregation lineage manifest |
| 4 | Temporal policy definition (includes temporal/reset rule version and all tolerances) |
| 5 | Temporal policy admission |
| 6 | Historical mapping definition |
| 7 | Historical mapping admission |
| 8 | State-evaluation policy definition |
| 9 | State-coverage manifest (includes mapping processor identity, all operands and admissions) |
| 10 | Response-completeness manifest (includes exact receipts, versions, signal/filter bindings and admissions) |
| 11 | Clock calibration definition/evidence |
| 12 | Clock calibration admission |
| 13 | Opening/reopening certificate |
| 14 | Closing certificate |

Referenced definitions/manifests must bind their exact subsidiary authority identities, content digests and applicable context; the parent digest commits to those members. Admission validation verifies the exact referenced definition and applicable scope/effective dates, never its label alone. This is how state-policy version/admission and signal-binding changes affect target identity even where represented transitively. The authority publication contracts must fix those subsidiary canonical formats before producer implementation; the assessment codec does not reinterpret their bytes.

## Complete result payload field order

Content digest = SHA-256(result payload bytes). The result is not part of the target identity. Persisting the same target with different canonical content is a conflict.

| Ordinal | Field | Encoding |
| --- | --- | --- |
| 1 | Domain | Exactly ASCII `FCCR` (46 43 43 52) |
| 2 | ResultCodecVersion | U16 = 1 |
| 3 | Target | U32 byte length + complete target bytes above |
| 4 | TargetIdentity | Hash; recompute and verify |
| 5 | Overall | Byte: 1 Missing; 2 Partial; 3 Established; 4 Incomparable; 5 Corrupt; 6 UnsupportedEncoding; 7 UnsupportedPolicy; 8 UnsupportedVerifier |
| 6 | AcquisitionCoverage | Byte: 0 NotEvaluated; 1 Missing; 2 Partial; 3 Complete |
| 7 | StateCoverage | Same axis grammar |
| 8 | ObservedRunningTicks | Optional(I64 >= 0), source-time observed measure only |
| 9 | AuthoritativeRunningTicks | Optional(I64 >= 0) |
| 10 | PrimaryReason | U16 from reason table |
| 11 | Diagnostics | U32 count then D entries below, sorted by (reason code, authority kind, identity UTF-8 bytes, absent interval before present, interval start, interval end); exact duplicates forbidden |
| 12 | ProvenStatePartition | U32 count then entries: Interval + Byte state (1 Running, 2 Idle, 3 Stopped) + Hash exact state-proof identity; ordered by start/end/proof bytes; overlap forbidden |
| 13 | AcquisitionGaps | U32 count then Interval entries, start/end ordered, disjoint; merge touching intervals |
| 14 | StateUnavailableIntervals | Same interval-list grammar |

D = U16 reason + U16 authority kind (0 general, 1..14 above) + String exact implicated reference identity/lookup key + Optional(Interval). All intervals must be contained in RequestedExtent. Diagnostic human text, query/evaluation time and SQL insertion timestamps are not canonical content. New reason codes or new states require a new payload encoding version.

| Reason code | Meaning and decision category |
| --- | --- |
| 0 | Reconciled: only for Established, one general diagnostic with empty identity and absent interval |
| 1 | InvalidEnvelope: Corrupt |
| 2 | DeclaredMemberMissing: Corrupt |
| 3 | SourceContentMismatch: Corrupt |
| 4 | ResultInconsistent: Corrupt |
| 5 | LineageIncompatible: Incomparable |
| 6 | AdmissionScopeMismatch: Incomparable |
| 7 | UnsupportedEncoding |
| 8 | UnsupportedVerifier |
| 9 | UnsupportedPolicy |
| 10 | PolicyNotEstablished: policy/admission absence overrides coverage evaluation |
| 20 | HistoricalMappingAuthorityNotEstablished |
| 21 | ResponseCompletenessNotEstablished |
| 22 | ClockAuthorityNotEstablished |
| 23 | BoundaryAuthorityNotEstablished |
| 30 | TemporalGapNotResolved |
| 31 | StateUnavailable |
| 32 | RecoveryBoundaryNotResolved |
| 33 | LegacyRegionNotExcluded |
| 34 | NoUsableStateEvidence |

Overall invalid-status precedence is 5 Corrupt, 6 UnsupportedEncoding, 8 UnsupportedVerifier, 7 UnsupportedPolicy, 4 Incomparable (these are enum values, not numeric sort order). PrimaryReason is the smallest code belonging to the selected invalid status. If no invalid status exists, code 10 forces Missing with axes NotEvaluated. Otherwise codes 20..34 indicate non-Established coverage; primary is their smallest present code, and usable verified known-state slices choose Partial versus Missing as defined in section 7 of the parent contract. A fully verified result with no remaining defect uses Established/reason 0. Retain all independently inspectable diagnostics, including lower-precedence defects. Unknown/unreadable input does not license fabricating a diagnostic about its undecoded content.

Result validation additionally requires: invalid overall outcomes and policy-not-established have both axes NotEvaluated and both durations absent; Missing has no observed or authoritative duration; Partial has authoritative duration absent and may retain observed zero; Established requires both axes Complete, nonempty gap-free complete known-state partition, empty gap/unavailable lists, and AuthoritativeRunningTicks equal to the exact sum of Running partition ticks. All durations <= requested duration. For Established, ObservedRunningTicks must also equal that sum. A complete known non-Running partition yields numeric zero; an empty partition cannot. The primary diagnostic must exist and follow the deterministic selection above. Receipt/mapping/boundary verifiers must still validate every claimed proof; codec consistency alone cannot establish authority.

## Golden vectors

These synthetic vectors establish byte-level encoding only, not admission or valid factory coverage. They use machine `00000000-0000-0000-0000-000000000001`, stream `obs`, site `S`, day 0, assignment `a`, shift `s`, shift ticks [0,300), requested [100,200), schedule/calendar versions `1`, time zone `UTC`/rule `1`, terminal identity 32 bytes AA, ordinal 1, prefix digest 32 bytes BB, opening kind 1 with absent legacy fields, processor `agg`, metric stream `metric`, aggregation position 1, evaluator/state/resolution versions `1`.

All R values are AbsentAtPinnedCut with lookup key `kNN` (NN is the two-digit kind) and 32 zero cut bytes, except R4 = Present(identity `P`, version `1`, digest 32 bytes 11). Vector B changes only R4 version to `2` and digest to 32 bytes 22; it must change target identity. R5 remains explicitly absent, so the synthetic result for A is Missing/PolicyNotEstablished, axes NotEvaluated, both durations absent, one diagnostic (10, kind 5, `k05`, no interval), and all three interval lists empty. The cut hashes are structural fixtures without real resolver records and do not assert a live publication.

The following full bytes and SHA-256 digests were generated from the stated grammar with a scratch Python standard-library encoder. Generation is not execution of production codec conformance tests. Future independent implementations must reproduce these bytes exactly before V1 acceptance.

### A.target

Length: 826 bytes. SHA-256: `4D26F4050701BF69D3A4FBFB0123FD3527373D084A402D6B4C2452310722D0BA`.

```text
464343540001000100000000000000000000000000000001000000036F6273000000015300000000
000000016100000001730000000000000000000000000000012C0000000000000064000000000000
00C800000001310000000131000000035554430000000131AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0000000000000001BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB010000000000036167670000000000000000000000000000
0001000000066D65747269630000000000000001000102000000036B303100000000000000000000
00000000000000000000000000000000000000000000000202000000036B30320000000000000000
000000000000000000000000000000000000000000000000000302000000036B3033000000000000
00000000000000000000000000000000000000000000000000000004010000000150000000013111
11111111111111111111111111111111111111111111111111111111111111000502000000036B30
35000000000000000000000000000000000000000000000000000000000000000000060200000003
6B303600000000000000000000000000000000000000000000000000000000000000000007020000
00036B30370000000000000000000000000000000000000000000000000000000000000000000802
000000036B3038000000000000000000000000000000000000000000000000000000000000000000
0902000000036B303900000000000000000000000000000000000000000000000000000000000000
00000A02000000036B31300000000000000000000000000000000000000000000000000000000000
000000000B02000000036B3131000000000000000000000000000000000000000000000000000000
0000000000000C02000000036B313200000000000000000000000000000000000000000000000000
00000000000000000D02000000036B31330000000000000000000000000000000000000000000000
000000000000000000000E02000000036B3134000000000000000000000000000000000000000000
0000000000000000000000000000013100000001310000000131
```

### B.target

Length: 826 bytes. SHA-256: `6074DFB221CC8E2F2914680D99EE08C876611EAE57E9B290078BAF8D1B02E6CA`.

```text
464343540001000100000000000000000000000000000001000000036F6273000000015300000000
000000016100000001730000000000000000000000000000012C0000000000000064000000000000
00C800000001310000000131000000035554430000000131AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0000000000000001BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB010000000000036167670000000000000000000000000000
0001000000066D65747269630000000000000001000102000000036B303100000000000000000000
00000000000000000000000000000000000000000000000202000000036B30320000000000000000
000000000000000000000000000000000000000000000000000302000000036B3033000000000000
00000000000000000000000000000000000000000000000000000004010000000150000000013222
22222222222222222222222222222222222222222222222222222222222222000502000000036B30
35000000000000000000000000000000000000000000000000000000000000000000060200000003
6B303600000000000000000000000000000000000000000000000000000000000000000007020000
00036B30370000000000000000000000000000000000000000000000000000000000000000000802
000000036B3038000000000000000000000000000000000000000000000000000000000000000000
0902000000036B303900000000000000000000000000000000000000000000000000000000000000
00000A02000000036B31300000000000000000000000000000000000000000000000000000000000
000000000B02000000036B3131000000000000000000000000000000000000000000000000000000
0000000000000C02000000036B313200000000000000000000000000000000000000000000000000
00000000000000000D02000000036B31330000000000000000000000000000000000000000000000
000000000000000000000E02000000036B3134000000000000000000000000000000000000000000
0000000000000000000000000000013100000001310000000131
```

### A.result

Length: 903 bytes. SHA-256: `7BE91D4A6F30AF6BF80E4F3774549329233DA0DDB270E2F3504683C0BB3E0CAB`.

```text
4643435200010000033A464343540001000100000000000000000000000000000001000000036F62
73000000015300000000000000016100000001730000000000000000000000000000012C00000000
0000006400000000000000C800000001310000000131000000035554430000000131AAAAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0000000000000001BBBBBBBBBBBB
BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB0100000000000361676700000000
000000000000000000000001000000066D65747269630000000000000001000102000000036B3031
0000000000000000000000000000000000000000000000000000000000000000000202000000036B
30320000000000000000000000000000000000000000000000000000000000000000000302000000
036B3033000000000000000000000000000000000000000000000000000000000000000000040100
00000150000000013111111111111111111111111111111111111111111111111111111111111111
11000502000000036B30350000000000000000000000000000000000000000000000000000000000
000000000602000000036B3036000000000000000000000000000000000000000000000000000000
0000000000000702000000036B303700000000000000000000000000000000000000000000000000
00000000000000000802000000036B30380000000000000000000000000000000000000000000000
000000000000000000000902000000036B3039000000000000000000000000000000000000000000
0000000000000000000000000A02000000036B313000000000000000000000000000000000000000
00000000000000000000000000000B02000000036B31310000000000000000000000000000000000
000000000000000000000000000000000C02000000036B3132000000000000000000000000000000
0000000000000000000000000000000000000D02000000036B313300000000000000000000000000
00000000000000000000000000000000000000000E02000000036B31340000000000000000000000
0000000000000000000000000000000000000000000000000131000000013100000001314D26F405
0701BF69D3A4FBFB0123FD3527373D084A402D6B4C2452310722D0BA0100000000000A0000000100
0A0005000000036B303500000000000000000000000000
```

### Rejection vectors

| Mutation to vector A | Required rejection |
| --- | --- |
| Target byte 4..5 changed from 0001 to 0002 | UnsupportedEncoding; never decode with V1 defaults |
| Remove the final target byte | Corrupt/truncated String data |
| Append byte 00 to the complete target | Corrupt/trailing bytes |
| Change OpeningKind 1 to 2 while both legacy optionals remain 0 | Corrupt/inconsistent opening boundary |
| A.result with embedded target hash replaced by 32 zero bytes | Corrupt/decoded identity mismatch |
| A.result with AuthoritativeRunningTicks changed to present zero while overall stays Missing | Corrupt/ResultInconsistent, even though zero lies within interval duration |

Whitespace/newlines in the displayed hex are presentation only and are not payload bytes. Canonical published hashes use uppercase hex only as a display convention; stored hashes are raw bytes. Golden vectors must be checked independently against the table, not accepted merely because an implementation regenerates its own expected values.

Second-encoder reproduction is recorded in [the vector verification report](RR03-DP3B2B2-vector-verification.md). Design freeze still requires explicit sign-off.
