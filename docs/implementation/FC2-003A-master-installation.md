# FC2-003A increment 1 — Master installation and migration acceptance

## Scope

New SQL library, explicit installer executable, immutable embedded migration manifest and real SQL acceptance executable. This increment creates only `platform` installation/migration metadata and `master` Site, ProductionLine and Machine records. G1's configuration tables, aliases, activation/security and evidence/reporting remain later increments. There is no production seed, automatic database creation, downgrade or runtime migration path.

The installer requires the full target connection through an environment variable, explicit database name, exact SQL `SERVERPROPERTY('ServerName')`, and installation UUID. The connection's Initial Catalog must match; system databases, the known legacy `FactoryConnect` name and attach-file paths are rejected. Name checks are only a secondary guard: unknown nonempty databases are refused, including custom schemas/principals. Empty initialization requires its own flag.

The installation UUID must be retained outside the database in installation records and supplied on every later run. Never derive it from a display name. The installer validates product and installation identity before pending DDL.

One serializable transaction and exclusive transaction-owned application lock cover preflight, optional bootstrap, the complete checksum/ordering validation, pending scripts and ledger. Failure rolls back the entire invocation, including first-install metadata. SQL scripts are trusted, embedded, transactional batches; no nontransactional migrations are supported. Checksums cover exact UTF-8 resource content, including line endings; published migration files must never be edited.

## Development use

Provision an empty, dedicated local development database manually. No existing application database qualifies. Set `FC2_SQL_CONNECTION` outside source control with its connection string and explicit Initial Catalog. Then run:

```powershell
dotnet run --project tools/FactoryConnect.SqlInstaller -- FC2_SQL_CONNECTION FactoryConnect_V2_Dev '<exact-server-name>' '<retained-installation-guid>' --initialize-empty
```

For repeat installation remove `--initialize-empty` and retain the same UUID. The installer reports a failure without printing SQL exception payloads or connection secrets. It does not create SQL logins or application grants; do not run the API with installer credentials.

## Acceptance

CI starts a dedicated SQL Server 2022 Developer container on loopback using an ephemeral masked password. The acceptance executable requires `FC2_TEST_SQL`; absence or unavailable SQL fails rather than skips. It creates only uniquely generated `FactoryConnect_V2_Test_<guid>` databases and cleans precisely those databases in `finally`; CI always removes the container as an independent cleanup boundary.

```powershell
dotnet run --project tests/FactoryConnect.Sql.Acceptance --configuration Release
```

Checks cover fresh install/rerun, wrong server/database/installation, checksum corruption, out-of-order scripts, migration and bootstrap rollback, unknown populated targets, FK/name constraints and concurrent installers. Cross-site assignments, alias overlap, immutable packages, activation/security and roster acceptance are not claimed by this increment.

Do not deploy this increment as a replacement for the current factory application. It contains no acquisition or reporting provider.
