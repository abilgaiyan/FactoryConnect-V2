using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Inventory;

/// <summary>
/// Installation-verified source inventory. This is NOT an active roster, commissioning
/// discovery result, authorization decision, or evidence of live machine connectivity.
/// Do not expose this reader directly from an unauthenticated HTTP endpoint.
/// </summary>
public sealed class SqlMachineInventoryReader(string connectionString, Guid installationId)
{
    public async Task<IReadOnlyList<MachineInventoryItem>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
            throw new InvalidOperationException("Explicit installation identity is required.");

        var target = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(target.InitialCatalog) ||
            target.InitialCatalog.Equals("master", StringComparison.OrdinalIgnoreCase) ||
            target.InitialCatalog.Equals("model", StringComparison.OrdinalIgnoreCase) ||
            target.InitialCatalog.Equals("msdb", StringComparison.OrdinalIgnoreCase) ||
            target.InitialCatalog.Equals("tempdb", StringComparison.OrdinalIgnoreCase) ||
            target.InitialCatalog.Equals("FactoryConnect", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(target.AttachDBFilename))
            throw new InvalidOperationException("An explicit, independent V2 SQL database is required.");

        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await using (var identity = new SqlCommand("""
            SELECT InstallationId FROM platform.InstallationIdentity
            WHERE Singleton=1 AND Product=N'FactoryConnect-V2';
            """, connection, transaction))
        {
            var actual = await identity.ExecuteScalarAsync(cancellationToken);
            if (actual is not Guid id || id != installationId)
                throw new InvalidOperationException("Installation identity does not match the V2 target.");
        }

        var machines = new List<MachineInventoryItem>();
        await using (var query = new SqlCommand("""
            SELECT MachineId,SiteId,DisplayName,IsRetired FROM [master].Machine
            ORDER BY SiteId,MachineId;
            """, connection, transaction))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                machines.Add(new MachineInventoryItem(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetBoolean(3)));
        }

        await transaction.CommitAsync(cancellationToken);
        return machines;
    }
}

public sealed record MachineInventoryItem(Guid MachineId, Guid SiteId, string DisplayName, bool IsRetired);
