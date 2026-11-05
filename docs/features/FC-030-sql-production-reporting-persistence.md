# FC-030 — SQL Production Reporting Persistence

## Status

- **FC-030 — SQL Production Reporting Persistence:** complete / frozen
- **FC-030.1 — SQL Migration & Schema Compatibility Foundation:** complete / frozen
- Production-reporting SQL persistence and provider composition required by the delivered reporting path are complete.
- Later migrations added by subsequent features continue to use the FC-030 migration/catalog/schema authority established here.

The remainder of this document preserves the FC-030 design narrative and the repository state at the start of the feature. Statements such as “not started,” “prohibited,” or “future migration” below are historical design constraints unless explicitly presented as current status.

## Goal

FC-030 establishes SQL Server persistence for FactoryConnect production reporting without allowing schema evolution, deployment privileges, or migration policy to leak into runtime reporting semantics.

The first slice is deliberately infrastructure-only:

> **FC-030.1 establishes the repository-controlled SQL migration, adoption, and runtime compatibility contract before any new production-reporting schema is introduced.**

This slice does not add operational-metric tables, reporting projections, or migration 005.

## Repository evidence at FC-030 start

The SQL Server provider currently embeds four SQL resources:

```text
001_InitialObservationIngestion.sql
002_DurableMetricAggregation.sql
003_BindMetricInputFactMachine.sql
004_ProductionContextMetricInputHandoff.sql
```

They are explicitly listed as embedded resources in `FactoryConnect.Persistence.SqlServer.csproj`.

These migrations predate the FC-030 migration-history architecture. They therefore form the immutable legacy baseline that FC-030.1 must be able to adopt and verify without rewriting historical SQL.

The repository has no FC-030 migration ledger, no repository migration catalog, and no migration 005 at the start of this slice.

## Governing architectural invariant

A FactoryConnect SQL Server database is compatible with a running binary only when both historical migration identity and live FactoryConnect-owned structure agree with the binary-supported repository contract.

```text
repository migration catalog
        ↓
canonical SQL + checksum
        ↓
migration ledger history
        ↓
CurrentSchemaDescriptor
        ↓
live FactoryConnect-owned SQL structure
        ↓
Compatible
```

History and structure are independent evidence dimensions.

A valid ledger does not prove that the live schema has not drifted. A structurally correct schema does not prove that the recorded migration history is the history supported by the current binary.

## FC-030.1 scope

FC-030.1 owns:

```text
✓ deterministic repository migration catalog
✓ canonical SQL resource representation
✓ immutable migration checksums
✓ lexical SQL content validation
✓ FactoryConnect migration ledger
✓ serialized migration execution
✓ engine-owned migration transactions
✓ explicit legacy 003 transaction exception
✓ legacy post-004 database adoption
✓ FactoryConnect-uninitialized database classification
✓ exact runtime migration-history verification
✓ exact runtime current-schema verification
✓ deployment/runtime privilege separation
✓ deterministic failure taxonomy
✓ executable acceptance matrix
```

FC-030.1 explicitly excludes:

```text
✗ migration 005
✗ new operational-metric SQL schema
✗ new reporting tables or indexes
✗ reporting query behavior changes
✗ automatic runtime DDL
✗ arbitrary T-SQL parsing
✗ generalized database drift repair
✗ destructive schema reconciliation
```

## Migration catalog

The repository is the authority for migration identity and content.

Each supported migration is represented by an immutable descriptor containing at least:

```text
MigrationId
Name
EmbeddedResourceName
CanonicalChecksum
LegacyTransactionPolicy
```

Migration identity is ordered and exact. The catalog must reject malformed or ambiguous repository state before connecting migration execution to a database.

Required catalog invariants:

```text
migration IDs are unique
migration names are unique
resource names are unique
catalog ordering is deterministic
IDs are strictly increasing
resource discovery is deterministic
all declared resources exist
canonical checksums are reproducible
```

The implementation may derive descriptors from deterministic embedded-resource discovery rather than hand-registering every migration, but the resulting catalog must be equivalent to a frozen ordered repository contract.

## Canonical SQL content

The checksum must be computed over the exact canonical SQL content that the engine executes.

Canonicalization is frozen as:

```text
1. decode as strict UTF-8
2. accept and remove a UTF-8 BOM when present
3. normalize CRLF and CR line endings to LF
4. preserve whether the canonical document ends with a final newline
5. encode canonical execution/checksum bytes as UTF-8 without BOM
6. SHA-256 over those canonical bytes
7. execute that exact canonical text
```

The engine must never checksum one representation and execute another.

Invalid UTF-8 is a repository/catalog failure, not a database migration failure.

## SQL content policy

Migration resources are application-owned SQL scripts, not sqlcmd scripts.

### `GO`

Executable `GO` batch separators are unsupported.

Detection must be lexical rather than naïve text/regex matching. The validator must not reject examples where the token occurs inside non-executable content such as:

```sql
'GO'
"GO"
[GO]
-- GO
/* GO */
```

An actual standalone executable `GO` directive must be rejected.

### Transaction-control statements

Migration 005 onward must not contain migration-owned transaction control. The engine owns migration transaction boundaries.

The lexical validator must reject executable transaction-control statements including the supported forbidden vocabulary for:

```text
BEGIN TRANSACTION / BEGIN TRAN
COMMIT TRANSACTION / COMMIT TRAN / COMMIT
ROLLBACK TRANSACTION / ROLLBACK TRAN / ROLLBACK
SAVE TRANSACTION / SAVE TRAN
```

Equivalent executable forms covered by the implementation's token grammar must follow the same policy.

Comments, string literals, quoted identifiers, and bracketed identifiers must not create false positives.

The validator is deliberately a narrow SQL lexical scanner, not a general T-SQL parser. Its responsibilities are limited to skipping non-executable lexical regions and recognizing the executable policy tokens required by FC-030.1.

## Historical migrations 001–004

The four existing repository migration resources are historical artifacts and become immutable once FC-030.1 establishes checksum identity.

They must not be rewritten merely to make them resemble the new migration policy.

### Migration 003 grandfathering

`003_BindMetricInputFactMachine.sql` is explicitly grandfathered if it contains migration-owned transaction behavior.

Frozen rule:

> **The migration engine owns transaction boundaries for all newly introduced migrations. Migration resources introduced after FC-030.1 must not contain transaction-control statements. Existing migration 003 is a grandfathered legacy exception whose SQL Server transaction behavior is explicitly supported and tested.**

The engine must support both its successful nested transaction behavior and its failure/rollback behavior without converting a migration failure into a misleading secondary transaction-cleanup error.

Success must preserve the outer engine transaction so that the history row is recorded atomically with the migration.

Failure must guarantee:

```text
no partial 003 schema committed
no committed 003 history row
no later migration executed
original migration failure remains primary
subsequent invocation can retry safely
```

This behavior requires real SQL Server integration coverage.

## Migration ledger

FC-030.1 introduces one FactoryConnect-owned migration history table.

The exact physical name is an implementation detail to freeze during the implementation slice, but its semantics are not.

Each committed migration history row contains enough immutable identity to prove at minimum:

```text
MigrationId
Name
CanonicalChecksum
AppliedAtUtc
```

Additional operational metadata may be recorded only if it does not become part of migration identity unless explicitly frozen later.

A migration is considered applied only when both its schema/content effects and its ledger row commit atomically.

## Serialization and concurrency

Migration execution must be serialized at the SQL Server database boundary.

The migration command must:

```text
open connection
    ↓
begin engine-owned Serializable transaction
    ↓
acquire Exclusive transaction-owned sp_getapplock
    ↓
classify ledger/schema state
    ↓
validate/apply required migrations
    ↓
insert corresponding history rows
    ↓
validate final supported structure
    ↓
commit
```

The application lock must be acquired before migration-ledger/schema classification so that two deployment processes cannot independently classify the same pre-migration state and race into DDL.

Lock acquisition failure is explicit and must not be treated as compatibility success.

## FactoryConnect-owned schema boundary

Compatibility checks inspect only structural artifacts owned by FactoryConnect.

Unrelated customer/admin objects are tolerated and do not make a database initialized, legacy, or incompatible.

FactoryConnect-owned structural artifacts include the tables and explicitly frozen subordinate artifacts described by the schema descriptors, including as applicable:

```text
tables
columns
data types
length/precision/scale
nullability
primary keys
foreign keys
unique constraints
check constraints
required indexes
other explicitly cataloged FactoryConnect structural artifacts
```

Object comparison must be deterministic and must not rely on incidental SQL Server metadata ordering.

## Database classification before a ledger exists

The term **empty database** is not used by FC-030.1 because unrelated objects may legitimately exist.

The canonical term is:

> **FactoryConnect-uninitialized database: no FactoryConnect migration ledger and no FactoryConnect-owned schema artifacts.**

Classification when no FactoryConnect ledger exists is:

```text
No ledger
│
├─ no FactoryConnect-owned artifacts
│    → Uninitialized
│
├─ exact LegacyPost004SchemaDescriptor
│    → LegacyAdoptable
│
└─ recognizable FactoryConnect artifacts that do not exactly match
     the legacy descriptor
     → PartialOrIncompatibleLegacy
```

Unrelated database objects are ignored by this classification.

## Legacy adoption

FC-030.1 must support databases that were legitimately created by repository migrations 001–004 before a migration ledger existed.

Legacy adoption is allowed only when the live FactoryConnect-owned schema exactly matches `LegacyPost004SchemaDescriptor`.

Adoption means recording the canonical historical identities for 001–004 under the same serialized migration transaction; it does not rerun the DDL.

No inference from a merely similar or partial schema is allowed.

If any required post-004 legacy artifact differs, the database is rejected for manual investigation rather than repaired or silently adopted.

## Structural descriptors

FC-030.1 introduces two distinct repository-controlled schema descriptors.

### `LegacyPost004SchemaDescriptor`

Purpose:

> Identify the one exact unledgered FactoryConnect schema that may be adopted as historical migrations 001–004.

It is an immutable legacy adoption baseline. After FC-030.1 it must not evolve merely because new migrations are added.

### `CurrentSchemaDescriptor`

Purpose:

> Describe the exact final FactoryConnect-owned SQL structure supported by the current binary.

This descriptor evolves whenever a newly supported migration changes owned structure.

Initially, before migration 005 exists, the current and legacy descriptors may describe the same post-004 structure. Their semantic roles are still distinct and must not be collapsed.

FC-030.1 does not require one structural descriptor for every historical migration.

## Runtime compatibility verification

Runtime hosts do not migrate databases.

A runtime may start against SQL Server only when:

```text
1. repository migration catalog is valid
2. ledger history exactly matches the binary-supported catalog
3. no supported migration is pending
4. database does not contain migration history newer than the binary
5. recorded migration names/checksums agree exactly
6. live FactoryConnect-owned schema exactly matches CurrentSchemaDescriptor
```

Runtime verification is SELECT-only from the database perspective. It must not perform DDL, create the ledger, adopt legacy schema, or repair drift.

This produces two independent drift concepts:

```text
MigrationChecksumDrift
    ledger/catalog disagreement

MigrationSchemaDrift
    ledger history is valid but live FactoryConnect structure
    differs from CurrentSchemaDescriptor
```

A correct ledger plus a missing required index is schema drift. A correct live schema plus a ledger checksum mismatch is checksum drift.

## History compatibility rules

Exact supported-history verification must distinguish at least:

```text
Compatible
MigrationPending
DatabaseNewerThanSupported
MigrationChecksumDrift
MigrationSchemaDrift
MigrationHistoryInvalid
```

Examples:

```text
catalog contains 005 but ledger ends at 004
→ MigrationPending

ledger contains 006 but binary catalog ends at 005
→ DatabaseNewerThanSupported

same ID/name but ledger checksum differs
→ MigrationChecksumDrift

correct ledger + missing required index
→ MigrationSchemaDrift

ledger IDs reordered, duplicated, or otherwise structurally invalid
→ MigrationHistoryInvalid
```

Public exception/type names may be refined during implementation, but these failure meanings must remain distinct.

## Deployment/runtime separation

Schema migration is a deployment concern, not an API/Edge runtime side effect.

FC-030.1 therefore requires a dedicated migration execution path/command with a DDL-capable deployment SQL identity.

Runtime services retain only the DML/read permissions their actual persistence/reporting responsibilities require, plus SELECT access required for compatibility verification.

Frozen deployment rule:

```text
deployment identity
    → acquire migration lock
    → adopt/migrate schema
    → verify final structure

runtime identity
    → verify exact history + current structure
    → start only when compatible
    → never execute migration DDL
```

This prevents an application restart from becoming a schema-deployment mechanism.

## Repository migration acceptance rule

Every migration introduced after FC-030.1 must satisfy all of the following before it can be considered complete:

```text
✓ immutable ordered catalog entry
✓ canonical checksum
✓ lexical SQL policy validation
✓ no migration-owned transaction control
✓ migration from the immediately supported prior version
✓ clean FactoryConnect installation through all migrations
✓ CurrentSchemaDescriptor updated to the new final structure
✓ migrated prior-version database exactly matches CurrentSchemaDescriptor
✓ clean install exactly matches CurrentSchemaDescriptor
✓ runtime verification reports Compatible afterward
✓ restart/re-execution is deterministic and does not reapply history
```

For the first future migration this means, conceptually:

```text
legacy post-004 database
→ adopt 001–004
→ apply 005
→ CurrentSchemaDescriptor

and

FactoryConnect-uninitialized database
→ 001 → 002 → 003 → 004 → 005
→ CurrentSchemaDescriptor
```

Migration 005 remains prohibited until the FC-030.1 infrastructure proving these rules is complete.

## FC-030.1 implementation slices

The implementation should proceed without introducing production-reporting schema changes.

### FC-030.1A — repository migration catalog and canonical SQL

```text
migration descriptor vocabulary
embedded-resource discovery
canonical UTF-8/newline handling
SHA-256 checksum identity
catalog validation
lexical GO validation
post-004 transaction-control validation
003 grandfathering metadata
unit/conformance tests
```

### FC-030.1B — schema descriptor and database classification

```text
FactoryConnect-owned structural vocabulary
LegacyPost004SchemaDescriptor
CurrentSchemaDescriptor
SQL Server metadata reader
exact deterministic structural comparison
Uninitialized / LegacyAdoptable / PartialOrIncompatibleLegacy classification
unrelated-object tolerance
```

### FC-030.1C — migration ledger, serialization, and execution engine

```text
ledger schema
Serializable engine transaction
Exclusive transaction-owned sp_getapplock
fresh installation
legacy adoption
atomic migration + history insertion
003 nested transaction success/failure compatibility
retry-safe failure behavior
```

### FC-030.1D — runtime compatibility verifier

```text
SELECT-only verification
exact supported history
pending/newer database detection
checksum drift
schema drift
host startup integration
runtime must not mutate schema
```

### FC-030.1E — dedicated migration command and whole-feature conformance

```text
DDL-capable deployment command
runtime/deployment identity boundary documentation
clean-install proof
legacy-adoption proof
concurrent migration proof
003 failure proof
restart/idempotence proof
runtime SELECT-only proof
whole-solution regression
```

The slice lettering is an implementation plan, not permission to start migration 005 before FC-030.1E closes.

## Executable acceptance matrix

At minimum FC-030.1 must prove:

| Scenario | Expected result |
|---|---|
| No ledger, no FactoryConnect artifacts, unrelated customer tables present | Fresh FactoryConnect installation allowed |
| No ledger, exact legacy post-004 FactoryConnect structure | Adopt 001–004 without rerunning their DDL |
| No ledger, partial legacy FactoryConnect structure | Reject |
| Ledger exactly matches catalog and live schema matches current descriptor | Compatible |
| Ledger checksum differs from catalog | `MigrationChecksumDrift` |
| Ledger valid, required owned index/constraint/column differs | `MigrationSchemaDrift` |
| Catalog has a migration absent from ledger | `MigrationPending` |
| Ledger contains migration newer than binary catalog | `DatabaseNewerThanSupported` |
| Two migrators start concurrently | One serialized migration sequence; no duplicate/partial history |
| New migration resource contains executable `GO` | Repository/catalog validation fails |
| `GO` appears only in comment/string/quoted or bracketed identifier | Accepted |
| Post-004 migration contains executable transaction control | Repository/catalog validation fails |
| Transaction keywords appear only in non-executable lexical content | Accepted |
| Grandfathered 003 succeeds under engine transaction | Schema + history commit atomically |
| Grandfathered 003 fails and rolls back transaction state | No partial schema/history; original failure preserved; retry safe |
| Runtime starts against uninitialized/adoptable/pending database | Refuse startup; runtime performs no DDL/adoption |
| Runtime verifies compatible database | SELECT-only success |

## Hard boundary for FC-030.1

If implementation requires deciding new production-reporting persistence semantics, new operational-metric schema, or new domain projection behavior, stop and return that decision to FC-030.2+ architecture review.

FC-030.1 may build only the migration and compatibility machinery required to make later SQL schema changes safe, deterministic, auditable, and runtime-independent.

## Exit criteria

FC-030.1 closes only when:

```text
repository catalog/checksum policy is executable
SQL lexical policy is executable
legacy post-004 adoption is executable
migration history is durable and serialized
003 compatibility is proven against real SQL Server
CurrentSchemaDescriptor verification is executable
runtime compatibility is SELECT-only
runtime cannot auto-migrate
migration command is separate from runtime startup
fresh/adopted/concurrent/failure/restart cases pass
whole-solution regression is green
Migration 005 still does not exist
```

Only after this foundation is reviewed and closed may FC-030 introduce the first new production-reporting SQL migration.
