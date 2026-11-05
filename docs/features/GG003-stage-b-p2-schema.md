# Stage-B P2 — registered SQL containment schema

Status: implementation prepared for SQL CI and independent review in draft PR #125.
P1 head `8208ad6b` completed both CI jobs successfully in run `39141636528`.
P3–P5, composition, durable recovery and deployment remain pending.

## Registered contract

Migration `021_DurableOperationalMetricContainment` follows catalog head 020.
Its DDL is byte-identical to the reviewed RC2 test resource after renaming the
eleven execution delimiters. No SQL predicate, FK, key, trigger body or historical
migration is changed. A unit test compares the complete texts and freezes all
twenty original canonical checksums.

The production executor validates the complete twelve-command plan before any
SQL. Only migration 021's exact identity opts into the plan. Existing migrations
retain single-command execution, including migration 003's existing policy.
All twelve commands use the caller's connection/transaction sequentially; the
executor neither opens a connection nor begins, commits or rolls back a
transaction. The existing migration engine owns the single commit, ledger insert
and final schema validation. No GO or independent batch transaction is introduced.

The schema adds immutable witness, result and witness-reference archives plus
format/policy/result/witness bindings on the existing processor/projection tables.
Defaults preserve format 1, null policy and null archive references for existing
data and unchanged legacy publishers. No row is converted to a V2 assessment.
The complete evidence subtype expression is retained with codec membership 1/2.
SQL enforces the reviewed structural relationships; it does not validate payload
canonicality, complete reference sets or historical authority.

Post021 extends the physical schema descriptor and owned-object set. Current
schema comparison additionally checks all triggers on the three archives and the
two publication-format defaults, including trigger definition/enabled/instead-of/
replication state. This is necessary to reject disabled immutability protection or
changed legacy defaults at migration completion and runtime startup. Older schema
descriptors retain their original table/column/key/check definitions.

## Validation coverage

- Fresh creation and post-020 upgrade through the real engine, exact Current
  descriptor, ledger/checksum/timestamp preservation and identical rerun.
- Production executor failure injection at each of the twelve compilation batches,
  caller rollback to exact Post020 and subsequent successful engine retry.
- Caller rollback after all twelve successful commands and pre-cancelled execution.
- Runtime/migration rejection of disabled, dropped or replaced immutable triggers
  and missing/changed publication-format defaults.
- Existing RC2 CHECK/FK/trigger/slot and populated V1 upgrade/read/replay probes now
  run both their original prototype SQL and the registered production executor.
  Sentinel archive payloads remain SQL-shape probes, not canonical authority.
- Existing concurrency prototype uses an explicit isolated Post020 baseline before
  its unchanged proposed schema. It remains design evidence, not P4 publication.

The new named SQL CI step runs the registered migration tests before the retained
prototype/concurrency and full SQL suite. Updating current-catalog assertions to
21 does not alter historical migration expectations or frozen checksum values.

Local SDK 10.0.402 ordinary Integration run: **759 passed, zero failed/skipped**.
The local SQL engine could not start in this environment; no local SQL pass is
claimed. SQL CI results must be recorded on the reviewed head before P2 acceptance.

The first registered-schema CI run at `2699ce71` (`39143400544`) failed final
schema comparison: four CHECK descriptors used different parentheses from SQL
Server's stored expressions, and the two default descriptors differed. The
reviewed DDL executed; the schema authority correctly prevented commit. The
descriptor was corrected to SQL Server's stored representation without changing
the DDL or weakening comparison. This failed attempt remains in history.

The subsequent run at `517681e` (`39143785782`) passed all 21 registered
migration tests and the ordinary CI job, but the full SQL suite failed two
migration-006 preservation assertions (718 passed, one restored-database test
skipped). Those assertions used `SELECT *`, so the expected new format/policy
and archive-binding columns changed the JSON shape. Preservation snapshots now
select every column from the frozen Post005 descriptor. Separate assertions
require exactly one legacy processor/projection with format 1 and null policy
and archive bindings, plus empty archives. The injected migration-006 failure
also verifies migration-021 columns and archive tables are absent. Migration SQL
and historical checksums are unchanged. A new full SQL CI pass is required.

Publication guards, V1 codecs, projection stores, processor composition, API and
factory configuration remain unchanged. The schema does not unblock revision 534.
