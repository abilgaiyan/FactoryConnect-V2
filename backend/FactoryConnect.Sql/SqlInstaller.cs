using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql;

public sealed record InstallationTarget(string ConnectionString, string Database,
    string ExpectedServer, Guid InstallationId, bool InitializeEmpty);

public sealed class SqlInstaller
{
    private const string Product = "FactoryConnect-V2";
    private static readonly string[] Forbidden = ["master", "model", "msdb", "tempdb", "FactoryConnect"];

    public async Task<int> ApplyAsync(InstallationTarget target,
        IReadOnlyList<SqlMigration>? migrations = null, CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(target.ConnectionString);
        if (string.IsNullOrWhiteSpace(target.Database) || string.IsNullOrWhiteSpace(target.ExpectedServer)
            || target.InstallationId == Guid.Empty || !string.Equals(builder.InitialCatalog, target.Database, StringComparison.Ordinal)
            || Forbidden.Contains(target.Database, StringComparer.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new InvalidOperationException("Explicit V2 database, server and installation identity are required.");

        migrations ??= SqlMigration.BuiltIn();
        var ordered = migrations.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 150)
            || ordered.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidOperationException("Invalid migration manifest.");

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // One installation transaction covers preflight, bootstrap, all migrations and ledger.
        try
        {
            await ExecuteAsync(connection, transaction, """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=N'FactoryConnect-V2:installer',
                    @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000;
                IF @result < 0 THROW 51000, 'Installer lock unavailable.', 1;
                """, cancellationToken);
            var server = Convert.ToString(await ScalarAsync(connection, transaction, "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'))", cancellationToken));
            if (!string.Equals(server, target.ExpectedServer, StringComparison.Ordinal))
                throw new InvalidOperationException("SQL server identity does not match explicit target.");

            var identified = Convert.ToInt32(await ScalarAsync(connection, transaction,
                "SELECT CASE WHEN OBJECT_ID(N'platform.InstallationIdentity', N'U') IS NULL THEN 0 ELSE 1 END", cancellationToken)) == 1;
            if (!identified)
            {
                var objects = Convert.ToInt32(await ScalarAsync(connection, transaction, """
                    SELECT (SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped=0)
                         + (SELECT COUNT(*) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383)
                         + (SELECT COUNT(*) FROM sys.database_principals WHERE principal_id > 4 AND principal_id < 16384);
                    """, cancellationToken));
                if (!target.InitializeEmpty || objects != 0)
                    throw new InvalidOperationException("Refusing unrecognized database; explicit empty initialization required.");
                await ExecuteAsync(connection, transaction, "CREATE SCHEMA platform", cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    CREATE TABLE platform.InstallationIdentity (
                        Singleton tinyint NOT NULL CONSTRAINT PK_Installation PRIMARY KEY CHECK (Singleton=1),
                        InstallationId uniqueidentifier NOT NULL UNIQUE,
                        Product nvarchar(64) NOT NULL CHECK (Product=N'FactoryConnect-V2'),
                        CreatedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME());
                    CREATE TABLE platform.SchemaMigration (
                        MigrationId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                        ContentSha256 binary(32) NOT NULL,
                        AppliedAt datetimeoffset(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                        InstallationId uniqueidentifier NOT NULL REFERENCES platform.InstallationIdentity(InstallationId));
                    """, cancellationToken);
                await using var insert = Command(connection, transaction,
                    "INSERT platform.InstallationIdentity(Singleton,InstallationId,Product) VALUES(1,@id,@product)");
                insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = target.InstallationId;
                insert.Parameters.Add("@product", SqlDbType.NVarChar, 64).Value = Product;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var identity = Command(connection, transaction,
                "SELECT InstallationId, Product FROM platform.InstallationIdentity WHERE Singleton=1"))
            await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != target.InstallationId
                    || reader.GetString(1) != Product)
                    throw new InvalidOperationException("Installation identity mismatch.");
            }

            var applied = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            await using (var ledger = Command(connection, transaction, "SELECT MigrationId, ContentSha256 FROM platform.SchemaMigration"))
            await using (var reader = await ledger.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) applied.Add(reader.GetString(0), (byte[])reader[1]);
            // Validate the entire ledger before applying any pending script.
            foreach (var entry in applied)
            {
                var source = ordered.SingleOrDefault(m => m.Id == entry.Key);
                if (source is null || !source.Checksum.SequenceEqual(entry.Value))
                    throw new InvalidOperationException("Unknown migration or changed migration checksum.");
            }
            if (applied.Count > 0 && ordered.Any(m => !applied.ContainsKey(m.Id)
                && string.CompareOrdinal(m.Id, applied.Keys.Max(StringComparer.Ordinal)) <= 0))
                throw new InvalidOperationException("Out-of-order migration refused.");

            var count = 0;
            foreach (var migration in ordered.Where(m => !applied.ContainsKey(m.Id)))
            {
                foreach (var batch in Regex.Split(migration.Sql, @"^\s*GO\s*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    if (!string.IsNullOrWhiteSpace(batch)) await ExecuteAsync(connection, transaction, batch, cancellationToken);
                await using var ledger = Command(connection, transaction,
                    "INSERT platform.SchemaMigration(MigrationId,ContentSha256,InstallationId) VALUES(@id,@hash,@installation)");
                ledger.Parameters.Add("@id", SqlDbType.NVarChar, 150).Value = migration.Id;
                ledger.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = migration.Checksum;
                ledger.Parameters.Add("@installation", SqlDbType.UniqueIdentifier).Value = target.InstallationId;
                await ledger.ExecuteNonQueryAsync(cancellationToken);
                count++;
            }
            await transaction.CommitAsync(cancellationToken);
            return count;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackError) when (rollbackError is SqlException or InvalidOperationException) { /* Server may already have rolled back. */ }
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = 30 };
    private static async Task ExecuteAsync(SqlConnection c, SqlTransaction t, string sql, CancellationToken ct)
    { await using var command = Command(c, t, sql); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<object?> ScalarAsync(SqlConnection c, SqlTransaction t, string sql, CancellationToken ct)
    { await using var command = Command(c, t, sql); return await command.ExecuteScalarAsync(ct); }
}
