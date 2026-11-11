using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.MasterData;

/// <summary>
/// SQL-backed site, line and machine inventory. Registration must be called only
/// after an audited, transaction-bound SQL authorization command has verified the actor.
/// No runtime configuration, acquisition or activation is performed here.
/// </summary>
internal sealed class MachineAdministrationStore(string connectionString)
{
    public sealed record Site(Guid SiteId, string DisplayName, bool IsRetired);
    public sealed record ProductionLine(Guid ProductionLineId, Guid SiteId, string DisplayName, bool IsRetired);
    public sealed record Machine(Guid MachineId, Guid SiteId, string DisplayName, bool IsRetired);

    public async Task<IReadOnlyList<Site>> ListSitesAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT SiteId, DisplayName, IsRetired FROM master.Site ORDER BY DisplayName, SiteId", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Site>();
        while (await reader.ReadAsync(ct))
            rows.Add(new Site(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        return rows;
    }

    public async Task<IReadOnlyList<ProductionLine>> ListLinesAsync(Guid siteId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT ProductionLineId, SiteId, DisplayName, IsRetired FROM master.ProductionLine WHERE SiteId=@site ORDER BY DisplayName, ProductionLineId", connection);
        command.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ProductionLine>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ProductionLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3)));
        return rows;
    }

    public async Task<IReadOnlyList<Machine>> ListMachinesAsync(Guid siteId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT MachineId, SiteId, DisplayName, IsRetired FROM master.Machine WHERE SiteId=@site ORDER BY DisplayName, MachineId", connection);
        command.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Machine>();
        while (await reader.ReadAsync(ct))
            rows.Add(new Machine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3)));
        return rows;
    }

    // The caller must supply the SQL transaction that performed authorization.
    // No independent connection or commit is permitted for administrative writes.
    internal Task<Guid> RegisterSiteAsync(SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct = default)
        => RegisterAsync(connection, transaction, "Site", "SiteId", null, name, ct);

    internal Task<Guid> RegisterLineAsync(SqlConnection connection, SqlTransaction transaction, Guid siteId, string name, CancellationToken ct = default)
        => RegisterAsync(connection, transaction, "ProductionLine", "ProductionLineId", siteId, name, ct);

    internal Task<Guid> RegisterMachineAsync(SqlConnection connection, SqlTransaction transaction, Guid siteId, string name, CancellationToken ct = default)
        => RegisterAsync(connection, transaction, "Machine", "MachineId", siteId, name, ct);

    private static async Task<Guid> RegisterAsync(SqlConnection connection, SqlTransaction transaction,
        string table, string idColumn, Guid? siteId, string name, CancellationToken ct)
    {
        // Identifiers are selected exclusively from the fixed methods above.
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException("Display name must contain 1 to 200 characters.", nameof(name));
        if (siteId == Guid.Empty) throw new ArgumentException("Site ID is required.", nameof(siteId));
        name = name.Trim();
        var id = Guid.NewGuid();
        // The security authority owns transaction isolation and commit. UPDLOCK/HOLDLOCK
        // serializes duplicate-name checks until that transaction completes.
        var predicate = siteId.HasValue ? "SiteId=@site AND " : "";
        await using (var check = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM master.{table} WITH (UPDLOCK, HOLDLOCK) WHERE {predicate}UPPER(LTRIM(RTRIM(DisplayName)))=UPPER(@name)",
            connection, transaction))
        {
            check.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = name;
            if (siteId.HasValue) check.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId.Value;
            if ((long)(await check.ExecuteScalarAsync(ct) ?? 0L) != 0)
                throw new InvalidOperationException("A record with this display name already exists in the selected scope.");
        }
        if (siteId.HasValue)
        {
            await using var parent = new SqlCommand(
                "SELECT COUNT_BIG(*) FROM master.Site WITH (UPDLOCK, HOLDLOCK) WHERE SiteId=@site AND IsRetired=0",
                connection, transaction);
            parent.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId.Value;
            if ((long)(await parent.ExecuteScalarAsync(ct) ?? 0L) != 1)
                throw new InvalidOperationException("The selected site does not exist or is retired.");
        }
        await using (var insert = new SqlCommand(
            $"INSERT master.{table} ({idColumn}, {(siteId.HasValue ? "SiteId, " : "")}DisplayName) VALUES (@id, {(siteId.HasValue ? "@site, " : "")}@name)",
            connection, transaction))
        {
            insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = name;
            if (siteId.HasValue) insert.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId.Value;
            await insert.ExecuteNonQueryAsync(ct);
        }
        return id;
    }
}
