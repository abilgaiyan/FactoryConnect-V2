# P1-A.3I-I2 — Durable coverage assessment authority

Design baseline: `dc811d119de847889b4668a902ccefcdaa0f95ab`.
I2-B is frozen. I2-C authorizes this additive persistence implementation. I2-D
acceptance requires the real-SQL runs and independent exact-diff review.

## Scope and authority

The subject contains projection processor, exact evaluation key, full aggregation
checkpoint and coverage policy version. Classification is content. A positive
`long` assessment revision identifies an immutable version within that subject.
Neither publication order nor durable source existence establishes evidential
quality or historical schedule completeness. R3243 remains Unproven.

The store has no producer, evaluator integration, latest-assessment reader,
backfill or deployment behavior. Operational projection checkpoints and metric
calculations are unchanged. SQL registration advertises a separate opt-in
`OperationalMetricCoverageAssessmentStorage` capability; existing capability
composites retain their behavior.

## API

`IOperationalMetricCoverageAssessmentStore.PublishAsync` accepts an immutable
request containing subject, proposed revision, expected predecessor and the I1
assessment. Initial publication is revision 1 with no predecessor; subsequent
requests propose the checked immediate successor. The predecessor is metadata.

After structural validation and V1 encoding, publication opens one serializable
transaction, takes an exclusive subject lock, and resolves the exact source
binding/revision within that transaction. Stored identity, history, codecs and
canonical content are checked before exact replay. Matching historical replay
returns Replayed without changing the head, even after later versions. Changed
content returns ContentConflict. Absent proposals require the current head to
match the expected predecessor. First publication creates subject, version and
head atomically; later publication inserts a version and advances the head.

Other established publication results are PredecessorConflict,
SourceRevisionAbsent, UnsupportedEncoding and IntegrityFailure. Appended is
returned only after commit acknowledgement. SQL errors, lock failures and
cancellation propagate; they do not imply rollback. A rollback/disposal attempt
cannot settle an uncertain commit.

Retain the original request across uncertainty. Read its exact subject/revision;
matching content proves publication, different content conflicts, absence permits
retry of the original request, and unavailable reads leave the outcome uncertain.
Never automatically allocate or rebase a revision.

`ReadExactAsync` returns Found (and an assessment), Absent, Unavailable,
UnsupportedEncoding or Corrupt. Only Found carries content. Shared subject
locking and integrity checks precede Found/Absent. Operational SQL and lock
failures map to Unavailable; cancellation is not converted to absence or
corruption. Coding/configuration failures are not caught as availability results.

## Canonical V1 representation

Identity and content codec versions are separate SQL metadata (both 1), outside
semantic subject identity. Only V1 writes are supported. Future write codecs
require an explicit compatibility decision preserving uniqueness, lock identity
and exact replay across representations.

All integers use fixed-width little-endian representation. Identity begins with
ASCII FCS1; content begins with FCC1. Strings have signed Int32 UTF-16 code-unit
counts followed by little-endian UInt16 code units, preserving unpaired
surrogates. Optional values use exactly one byte (0 absent, 1 present). Lists have
Int32 counts; nullable interval lists have a preceding presence byte. Intervals
retain their fragment boundaries and order. Timestamps are Int64 UTC ticks at
100-nanosecond precision. GUIDs use the .NET `Guid.ToByteArray()` field order.
Source position is UInt64. Classification/reason are explicit Int32 fields.

Identity field order: projection processor; evaluation machine; period
(discriminator 1 shift: site, assignment, shift, start/end ticks; discriminator 2
production day: site, day number); optional order/operation/part/operator;
metric key/version; aggregation processor; source machine/stream/position;
coverage policy version.

Content field order: classification, reason, assessed intervals, completion
boundary, schedule authority identity/revision, expected intervals, classified
intervals, gaps, unknown eligibility intervals, ordered evidence references
(kind/identity/revision).

No arbitrary new I1 string/list/payload cap is imposed. Actual byte-array/runtime
capacity is checked before publication; lengths/counts must fit remaining bytes
before allocation. Existing durable aggregation keys have a 256-code-unit
capacity: a longer source identity cannot resolve to a committed source binding.
Missing fields, unsupported discriminators, invalid markers, truncation and
trailing bytes are rejected. Decoding reconstructs through I1 and re-encodes for
exact byte comparison. Unknown stored codecs return UnsupportedEncoding;
malformed/noncanonical supported bytes return Corrupt/IntegrityFailure. Hashes
only select candidates; full bytes establish identity. Detected collisions fail
without writes.

## SQL and integrity

Migration 015 adds immutable subject/version tables and one internal subject
head. All columns are non-null; revisions and codec metadata are positive;
canonical binaries are nonempty. SourcePosition is decimal(20,0), checked in
1..18446744073709551615. Subject binds both existing processor/stream unique key
and processor/revision composite primary key. Head references a version of its
own subject. Foreign keys have no cascading actions.

All supported store operations cooperate on transaction-owned application locks
using database scope, principal public, and resource
`FactoryConnect.CoverageAssessment:<uppercase SHA256 hex>`. Reads take Shared,
publication Exclusive, including when no subject exists. Lock timeout is 30,000
milliseconds; command timeout is zero so cancellation/SQL lock timeout controls
waiting. Negative or malformed return codes fail acquisition. Codec versions do
not partition lock resources.

Under serialization, a subject must have a head and versions with MIN=1,
MAX=head and COUNT_BIG=head. Composite uniqueness and positive checks establish
contiguity. The provider verifies duplicated source fields against the canonical
subject and current durable source binding. It also verifies each historical
payload, so detected older corruption/unsupported encoding blocks operations.
There is no automatic repair. Direct SQL writers that bypass these contracts are
outside the publication protocol.

Post014 is exposed explicitly; Post015 extends it; Current selects Post015.
Migration 001–014 SQL/checksums and historical descriptor content are preserved.
Owned schema recognition includes the three new tables.

## Verification

Run `tests/deployment/Invoke-OperationalMetricCoverageAssessmentConformance.ps1`:
Portable builds Release, runs Core and non-SQL integration tests; FocusedSql runs
coverage and migration-015 SQL tests; Full runs the entire integration suite.
SQL modes require FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING pointing to a
local/disposable SQL test server with database-create/drop permission. Tests
create disposable databases. Do not use the factory deployment connection.

Added tests cover contract validation, all classifications, unknown/empty,
fragments, provenance order, unpaired surrogates, shift/context identity, ticks,
truncated/malformed bytes, maximum revision replay decision, opt-in composition,
and frozen prior checksums. Real-SQL tests cover append/replay/conflicts, source
absence/UInt64 maximum, first-publication races, coherent reads, write-stage
rollback, uncertain-response recovery, corrupted history/bindings/codecs,
hash collision, cancellation, initialization, Post014 upgrade and rerun.

Initial evidence: Core 604/604 PASS; non-SQL integration 681/681 PASS;
Release solution build PASS with zero warnings/errors. Real-SQL conformance and
independent review remain pending; the implementation is not I2-D closed.

Cancellation correction following review of `3555f30`: reads and publication
translate SQL/subject-lock failures observed with the caller token cancelled
to `OperationCanceledException`, preserving that exception as the inner
exception and retaining the caller token. Existing cancellation exceptions
propagate. This does not establish rollback or a publication outcome. Four
disposable-SQL cases exercise in-flight cancellation for both operations
during subject-lock waits and blocked SQL queries; execution remains pending.
Correction verification: Core 604/604 and non-SQL integration 681/681 PASS;
Release build passes with zero warnings/errors. Real-SQL execution remains pending.
