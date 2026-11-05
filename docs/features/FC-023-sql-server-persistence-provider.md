# FC-023 — SQL Server Persistence Provider

## Goal

Implement a production SQL Server provider for `IObservationIngestionStore` without changing the neutral persistence contract or acquisition/runtime semantics established by FC-020, FC-021, and FC-022.

The provider must validate FC-022's open-extension architecture in practice:

```text
FactoryConnect.Persistence.SqlServer
        |
        +-- AddSqlServerPersistenceProvider(...)
        |
        +-- ProviderKey = "SqlServer"
        |
        v
FactoryConnect.Persistence selector
        |
        v
IObservationIngestionStore
```

SQL Server implements persistence semantics; it does not define them.

## Scope Boundary

FC-023 is limited to durable observation ingestion and checkpoint persistence.

Included:

- SQL Server provider project;
- provider-owned configuration;
- initial SQL schema;
- exact stream identity preservation;
- canonical observation-value serialization and replay equivalence;
- checkpoint reads;
- atomic observation/checkpoint commits;
- idempotent replay;
- optimistic concurrency;
- same-stream locking;
- reusable conformance testing;
- SQL integration-test infrastructure;
- Edge provider selection by configuration.

Excluded:

- FactoryConnect user authentication;
- FactoryConnect authorization;
- roles or permissions;
- audit-user identity;
- reporting schema;
- OEE tables;
- machine/company/site application persistence;
- runtime provider switching;
- multiple active persistence providers;
- MongoDB/PostgreSQL providers;
- automatic migration framework;
- EF Core domain model.

Database credentials used by the SQL Server provider are infrastructure configuration and are not FactoryConnect user authentication.

## Provider Project

```text
src/
└── FactoryConnect.Persistence.SqlServer/
    ├── FactoryConnect.Persistence.SqlServer.csproj
    ├── SqlServerPersistenceOptions.cs
    ├── SqlServerPersistenceServiceCollectionExtensions.cs
    ├── SqlServerObservationIngestionStore.cs
    ├── SqlServerObservationValueCodec.cs
    ├── OrdinalStringKeyCodec.cs
    └── Sql/
        └── 001_InitialObservationIngestion.sql
```

The provider uses `Microsoft.Data.SqlClient` directly. FC-023 does not introduce EF Core.

## Configuration Contract

```json
{
  "Persistence": {
    "Provider": "SqlServer"
  },
  "PersistenceProviders": {
    "SqlServer": {
      "ConnectionString": "..."
    }
  }
}
```

Composition order remains:

```csharp
builder.Services.AddSqlServerPersistenceProvider(
    builder.Configuration.GetRequiredSection(
        "PersistenceProviders:SqlServer"));

builder.Services.AddFactoryConnectPersistence(
    builder.Configuration);
```

`FactoryConnect.Persistence` must remain unaware of SQL Server configuration and provider names.

Connection strings may be supplied by normal .NET configuration providers or secret stores. Production credentials must not be committed to source control.

## Stream Identity

`ObservationStreamId` uses .NET string equality semantics. SQL textual equality cannot be trusted to reproduce complete ordinal identity because SQL collation and padding semantics differ from .NET.

FC-023 therefore defines the relational stream identity as:

```text
MachineId + StreamKeyBinary
```

`StreamKey` remains descriptive/readable data.

### Deterministic Ordinal String Encoding

`StreamKeyBinary` is not produced by a general text encoder. The provider owns an internal deterministic codec that writes each .NET UTF-16 code unit directly as an unsigned 16-bit value in fixed big-endian byte order.

Conceptually:

```csharp
internal static byte[] EncodeOrdinal(string value)
{
    var result = new byte[checked(value.Length * 2)];

    for (var index = 0; index < value.Length; index++)
    {
        BinaryPrimitives.WriteUInt16BigEndian(
            result.AsSpan(index * 2, 2),
            value[index]);
    }

    return result;
}
```

This preserves every distinction visible to ordinal UTF-16 comparison, including:

- casing;
- trailing spaces;
- embedded null characters;
- combining characters;
- surrogate code units;
- supplementary characters represented by surrogate pairs.

Provider limit:

```text
StreamKey <= 256 UTF-16 code units
StreamKeyBinary <= 512 bytes
```

This is an FC-023 SQL Server provider limit, not a new neutral abstraction limit.

Required codec tests include:

```text
"A" != "a"
"A" != "A "
"é" != "e\u0301"
embedded nulls remain distinct
supplementary characters round-trip
```

Readable `StreamKey` uses an explicit binary collation such as `Latin1_General_100_BIN2`, but the binary column is the relational identity.

## SQL Schema

FC-023 uses exactly two tables.

### ObservationStreamCheckpoint

```text
MachineId          uniqueidentifier
StreamKeyBinary    varbinary(512)
StreamKey          nvarchar(256) COLLATE ...BIN2
InstanceId         decimal(20,0)
NextSequence       decimal(20,0)
```

Primary key:

```text
MachineId + StreamKeyBinary
```

### MachineObservation

```text
MachineId          uniqueidentifier
StreamKeyBinary    varbinary(512)
InstanceId         decimal(20,0)
Sequence           decimal(20,0)
Source             nvarchar(...)
Address            nvarchar(...)
SignalType         tinyint
ObservationValue   nvarchar(max) NULL
Quality            tinyint
ObservedAt         datetimeoffset(7)
```

Primary key:

```text
MachineId + StreamKeyBinary + InstanceId + Sequence
```

Foreign key:

```text
MachineObservation(MachineId, StreamKeyBinary)
    -> ObservationStreamCheckpoint(MachineId, StreamKeyBinary)
```

Readable `StreamKey` exists only in the checkpoint table. Observation rows belong relationally to a known stream without duplicating descriptive stream text.

## Unsigned Range Preservation

C# uses `ulong` for MTConnect instance and sequence values. SQL Server stores them as `decimal(20,0)`.

Every corresponding database column must enforce:

```sql
CHECK (
    Value >= 0
    AND Value <= 18446744073709551615
)
```

Apply equivalent constraints to:

- `ObservationStreamCheckpoint.InstanceId`;
- `ObservationStreamCheckpoint.NextSequence`;
- `MachineObservation.InstanceId`;
- `MachineObservation.Sequence`.

Materialization back to `ulong` must use checked conversion even though the schema enforces the range.

## SignalType and Quality Storage

`SignalType` and `ObservationQuality` are stored numerically rather than through enum names.

Current values are:

```text
SignalType
0 Digital
1 Analog
2 Counter
3 WholeNumber
4 Numeric
5 Text
6 Enumeration
7 Timestamp

ObservationQuality
0 Good
1 Uncertain
2 Bad
```

Schema constraints must enforce those valid numeric ranges.

## Observation Value Contract

`MachineObservation.Value` is `object?`, so FC-023 must define accepted CLR values explicitly rather than broadly accepting arbitrary numeric or textual objects.

Initial support is based on actual acquisition behavior first:

```text
Numeric      -> decimal
Enumeration  -> string
Text         -> string
unavailable  -> null
```

Additional signal-type/CLR combinations are added deliberately with tests when required by acquisition behavior.

Unsupported signal-type/CLR combinations fail before persistence mutation.

### Storage Fidelity vs Replay Equality

Storage representation and semantic equality are related but not identical concerns.

The provider must define both:

```text
storage fidelity
    what data is persisted and reconstructed

replay equivalence
    when an incoming observation is considered equal to an existing one
```

Canonical serialization must be deterministic and invariant-culture based where applicable. The same codec used to create stored representations must participate in replay equivalence; however, comparison must still match current .NET semantics for each field.

Examples:

- `Source`, `Address`, `Text`, `Enumeration`: exact ordinal string semantics;
- accepted numeric values: compare according to the explicitly accepted CLR representation, not merely because different CLR types print the same text;
- `DateTimeOffset`: preserve the supplied instant and offset in storage, while replay equality follows `DateTimeOffset.Equals` semantics (same UTC instant);
- `SignalType` and `Quality`: numeric enum equality;
- `null`: remains distinct from any non-null serialized value.

Do not use SQL collation behavior as the definition of observation payload equality.

## Batch Staging and Deduplication

Incoming observations are validated and staged before SQL mutation.

For a batch checkpoint with instance `InstanceId`, observations are grouped by:

```text
InstanceId + Sequence
```

Within one incoming batch:

```text
same identity + equivalent payload
    -> collapse to one staged observation

same identity + conflicting payload
    -> reject the batch before SQL mutation
```

SQL primary-key violations are not used as domain validation.

The shared conformance suite must include:

```text
same identity + same payload appears twice in one batch
        ↓
commit succeeds
        ↓
one durable observation exists
        ↓
checkpoint commits normally
```

Both in-memory and SQL Server providers must pass this behavior.

## Transaction Semantics

Every `CommitAsync` executes as one SQL transaction.

```text
BEGIN TRANSACTION

    acquire same-stream checkpoint lock
    validate persisted checkpoint state
    validate replay/expected-checkpoint semantics
    stage/check observations
    insert or validate durable observations
    insert/update checkpoint

COMMIT
```

Any failure rolls back both checkpoint and observation changes.

The provider must never expose:

```text
observations committed without checkpoint
checkpoint committed without observations
```

For a first commit, the transaction may insert the checkpoint row before inserting observations so the foreign key is satisfied; any observation failure must roll the checkpoint insert back in the same transaction.

## Exact FC-020 Replay Algorithm

The SQL provider must mirror the existing FC-020 replay behavior.

```text
persisted checkpoint == proposed checkpoint
        ↓
idempotent replay
        ↓
ExpectedCheckpoint comparison is bypassed
        ↓
every supplied observation must already exist identically
```

An idempotent replay may not add an observation to an already committed checkpoint.

If the proposed checkpoint is not the already-persisted checkpoint, normal expected-checkpoint optimistic concurrency rules apply.

## Optimistic Concurrency

For a forward transition:

```text
persisted checkpoint == ExpectedCheckpoint
        ↓
transition may commit
```

Otherwise the commit fails as a stale checkpoint conflict.

SQL optimistic-concurrency conflicts are domain outcomes, not transient failures. They must not be silently translated into success and must not be indiscriminately retried.

## Same-Stream Locking

The checkpoint row/key is the serialization boundary for one observation stream.

A query using an indexed checkpoint identity with an appropriate locking pattern such as:

```sql
WITH (UPDLOCK, HOLDLOCK)
```

must protect both:

- an existing checkpoint row;
- the absent checkpoint key range during first-stream creation.

Different streams remain independently writable.

Required concurrency tests:

### Concurrent first creation

```text
checkpoint absent
writer A + writer B target same stream
        ↓
one commits
        ↓
other observes changed/stale state and fails
```

### Concurrent continuation

```text
checkpoint exists
writer A + writer B share same ExpectedCheckpoint
        ↓
one commits
        ↓
other observes stale checkpoint and fails
```

## Checkpoint Read

`ReadCheckpointAsync` must:

- return `null` for an unknown stream;
- look up by exact binary stream identity;
- reconstruct `InstanceId` and `NextSequence` through checked `ulong` conversion;
- honor cancellation;
- preserve stream identity exactly.

## SQL Integration-Test Environment

SQL integration tests remain in `FactoryConnect.Integration.Tests` and reference the SQL Server provider project.

Connection contract:

```text
FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING
```

The supplied connection is used only to provision a dedicated disposable test database. Tests must never target a production database.

Required lifecycle:

```text
SQL Server available
        ↓
create unique disposable database
        ↓
execute provider's embedded 001 schema script
        ↓
run SQL-specific + shared conformance tests
        ↓
drop disposable database
```

The embedded provider `001_InitialObservationIngestion.sql` script is the only schema source used by tests.

Test runs must be isolated from one another.

SQL Server being unavailable must never be reported as SQL conformance success.

Ordinary local test runs may exclude the SQL integration category when no test SQL Server is available, but PR acceptance CI must run both:

```text
ordinary test suite
+
SQL Server integration/conformance suite
```

## Shared Conformance Contract

`SqlServerObservationIngestionStoreConformanceTests` derives from the existing reusable `ObservationIngestionStoreConformanceTests` suite.

FC-023 must preserve all existing FC-020 behaviors, including:

- atomic observations/checkpoint commit;
- expected-checkpoint validation;
- same-instance advancement;
- idempotent replay;
- replay augmentation rejection;
- checkpoint regression rejection;
- invalid-observation atomicity;
- empty-batch checkpoint advancement;
- conflicting duplicate rejection;
- identical duplicate collapse;
- stream isolation;
- cancellation semantics;
- sequence-bound validation;
- stale expected-checkpoint rejection;
- explicit MTConnect instance transition.

## Implementation Checkpoints

### FC-023.1 — Provider Skeleton and Validated Configuration

Create the SQL Server provider project, options, provider registration, and configuration validation.

Acceptance:

```text
Provider = SqlServer
        ↓
FC-022 selects SQL Server provider
```

No neutral persistence changes are required.

### FC-023.2 — Schema Script and SQL Test Fixture

Add `001_InitialObservationIngestion.sql`, exact binary stream identity, numeric constraints, FK/PK constraints, and disposable SQL test-database fixture.

Acceptance:

- schema initializes from the embedded script;
- exact stream identity tests pass;
- stream-key limit is enforced;
- `ulong` range constraints are verified;
- SQL test databases are isolated and disposable.

### FC-023.3 — Canonical Value Serializer and Equivalence Rules

Implement the accepted CLR value matrix, deterministic serialization, storage fidelity, replay equivalence, ordinal text comparison, and unsupported-value rejection.

Acceptance:

- all supported combinations have round-trip tests;
- equivalence tests match defined .NET semantics;
- unsupported combinations fail before mutation.

### FC-023.4 — Checkpoint Read

Implement `ReadCheckpointAsync`.

Acceptance:

- missing stream -> `null`;
- existing stream -> exact checkpoint;
- maximum `ulong` values round-trip;
- exact stream identity is respected;
- cancellation works.

### FC-023.5 — Atomic Commit and Rollback

Implement transactional checkpoint + observation persistence.

Acceptance:

- observations and checkpoint commit together;
- failures leave no partial state;
- empty observation batches may advance checkpoints where allowed.

### FC-023.6 — Idempotency and Same-Stream Concurrency

Implement exact FC-020 replay behavior, incoming-batch staging/deduplication, stale-checkpoint conflicts, and same-stream locking.

Acceptance:

- identical replay succeeds without creating data;
- replay augmentation fails;
- identical duplicates within one incoming batch collapse to one observation;
- conflicting duplicates fail before mutation;
- concurrent absent-row creation yields one winner and one stale conflict;
- concurrent existing-row continuation yields one winner and one stale conflict;
- optimistic-concurrency conflicts are not automatically retried.

### FC-023.7 — Full Conformance and Edge Configuration Proof

Run the SQL provider through the full shared conformance suite and prove Edge provider selection by configuration only.

Acceptance:

```json
"Persistence": {
  "Provider": "SqlServer"
}
```

selects the SQL provider without acquisition/runtime changes.

PR CI must pass the ordinary suite and SQL integration suite.

## Frozen Invariants

FC-023 is frozen around these rules:

- stream identity is `MachineId + deterministic big-endian UTF-16-code-unit bytes`;
- readable stream text is descriptive, not relational identity;
- SQL Server stream keys are limited to 256 UTF-16 code units;
- all `ulong` columns have database range checks and checked materialization;
- `SignalType` and `Quality` use constrained numeric storage;
- the accepted CLR value matrix is explicit and begins with actual acquisition output;
- storage fidelity and replay equivalence are separately defined;
- incoming batches are staged and deduplicated before SQL mutation;
- checkpoint and observations commit in one transaction;
- exact FC-020 replay semantics are preserved;
- missing-row and existing-row concurrency are tested separately;
- SQL optimistic-concurrency conflicts are not automatically retried;
- SQL integration tests use a unique disposable database;
- ordinary local tests may exclude SQL tests, but PR CI must run both suites;
- the provider's embedded `001` script is the only schema source used by tests;
- SQL Server credentials are infrastructure configuration only;
- authentication, authorization, audit, reporting, and application-domain tables remain excluded;
- FC-023 adds a provider and must not require provider-specific changes to `FactoryConnect.Persistence`.

## Definition of Done

FC-023 is complete when:

```text
SQL Server provider registers through FC-022
              +
exact stream identity is preserved
              +
schema preserves the complete ulong domain
              +
canonical serialization/equivalence is deterministic
              +
CommitAsync is atomic
              +
FC-020 replay semantics are preserved
              +
same-stream concurrency is correct
              +
all shared store conformance tests pass
              +
SQL-specific integration tests pass
              +
Edge selects SqlServer by configuration only
              +
neutral persistence remains provider-agnostic
```
