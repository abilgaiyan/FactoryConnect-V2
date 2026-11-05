# Stage-B P1 — canonical codec implementation

Status: implemented for review in draft PR #125; P2–P5 remain pending.
No SQL migration, publisher, source validator, reporting activation or restored
database replay is included. Stage A's commit guard remains intact.

## Implemented boundary

- FCWE V1 encodes the exact source cut, period, normalized numerator/denominator
  components and complete positioned fact, including optional context/provenance
  and shift/day ownership. Decoding rejects malformed primitives, unsupported
  versions, truncated/trailing bytes and contradictory local witness bindings.
- FCDS V2 recursively encodes the explicit containment processor/policy, unchanged
  FCEK V1, status/value/reason, source cut, mandatory authority tag, inline witness
  identity/bytes, components and complete ordered dependencies.
- Performance requires the completed-reference binding shape, ordered unique
  source IDs and standard revisions; other built-in nodes require aggregation-only
  authority. Empty R0 sets are representable claims, not proof of a completed cut.
- Semantic validation restricts V2 to fixed built-in 1.0 unpartitioned shift/day
  definitions. It enforces operand order/identity/unit/dimension, coherent child
  bindings, missing/zero/witness precedence, durable precision/domain and numeric
  consistency. Calculated OEE uses unrounded ratios from the child operands.
- Decoding bounds lengths/counts/depth before allocations, shares a global evidence
  budget, rejects noncanonical decimals/Unicode/tags and requires exact re-encoding.
  Nested readers use slices of the bounded root buffer.

`ContainmentWitnessDocument` and `ContainmentResultDocument` are internal,
**unverified wire claims**. A decoder cannot construct
`AvailabilityScopeConflictEvidenceV1`, return a committable projection or certify
historical authority. `FromEvidence`/`FromProjection` serialize existing evaluated
objects; they do not replace P3's source validation.

P3 must independently establish incorporated membership through A, both complete
aggregates, the lowest offending position, exact completed transition/cut R,
quantity sources, outcome/standard revisions S, and original arithmetic. Hashes,
matching wire fields and declared authority arrays alone establish none of this.
Source contradictions with canonical bytes remain possible until that gate runs.

No V1 encoder/decoder, abstraction constructor, evaluator, projection commit guard,
production composition, migration resource or API contract is changed.

## Vector provenance and tests

The accepted RC2 `canonical-vectors.json` SHA256 is
`a6493df12230d7ddfe6224da0ed0e454a40ed0dc3d4d9b2ef9e67ae51a610e51`.
The test resource extracts its seven envelope inputs/bytes/digests, six negative
candidates and primitive probes. Obsolete reference-tool execution annotations
are omitted. Reference calculators and the private review document are excluded.
The two packaged GG-003 fixtures match the existing repository fixtures exactly;
no factory export, deployment identities or additional source history is added.

Tests construct all seven models directly from their explicit inputs, independently
of the decoder, then require exact encoded bytes/lengths and all applicable
WitnessId, ResultId and DependencyHashV2 values. Separately, each accepted envelope
is decoded and re-encoded. The negative candidates retain recomputed ResultIds,
so hashing malformed bytes cannot substitute for validation.

Additional cases cover production-day encoding, nested witness placement,
nonempty authority with UTF-16 ordinal ordering and Int64 boundaries, reordered
or duplicate operands/authorities, forged values/witnesses, malformed Unicode,
nullable tags, every truncated prefix, excessive sizes/counts/depth and a real
Stage-A witness built from two synthetic incorporated contributions. The latter
also verifies that V1 serialization and the existing commit coordinator still
reject containment projections.

The new V1 regression specimen uses the unchanged baseline encoder at `31aec8a7`:
1,100 bytes, SHA256
`4cb376c206e8f153b75173d8a695168c1e0941f34e76a21f293ae46d1dc70441`.
Its exact bytes and original non-domain-separated hash must remain unchanged,
including a nested Availability projection. This specimen is separate from the
seven RC2 envelopes. No legacy history is reclassified.

## Local validation

SDK 10.0.402 was installed in the transient development environment. The production
codecs also compiled with the repository Core/Abstractions through a small local
console harness with zero warnings/errors; all seven byte round-trips and six
negative cases passed before repository test execution. This harness is not a
replacement test runner and is not included in the PR.

The actual Release Integration project was then built and tested using:

```text
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj
  --configuration Release --no-restore -m:1 --filter "Category!=SqlServerIntegration"
```

Result: **752 passed, 0 failed, 0 skipped**, including the new codec cases and
existing ordinary reporting/persistence/HTTP tests. OpenAPI generation succeeded.
No local SQL integration suite, factory database, migration or recovery was run.
The existing V1 codec/decoder sources are byte-identical to baseline.

The first local attempt failed because the OpenAPI subprocess could not locate
`dotnet`; setting PATH/DOTNET_ROOT to the installed SDK resolved it. No repository
configuration or generated contract was changed to bypass that failure.

Require CI results on the final PR head and independent code/vector review before
accepting P1. Keep PR #125 draft. This evidence does not close P2–P5 or unblock
revision 534.
