using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Inventory;

/// <summary>
/// A protected, installation-scoped SQL inventory read. The security authority holds
/// its shared revocation gate through full materialization. This is not an active roster.
/// No public endpoint is registered.
/// </summary>
public sealed class SqlProtectedMachineInventoryReader(string connectionString, Guid installationId)
{
    public Task<IReadOnlyList<MachineInventoryItem>> ReadSiteAsync(
        byte[] sessionHash, Guid siteId, CancellationToken cancellationToken = default)
    {
        if (siteId == Guid.Empty) throw new ArgumentException("Explicit site is required.", nameof(siteId));
        var authority = new SqlSecurityAuthority(connectionString, installationId);
        return authority.ReadAsync(sessionHash, "configuration.read", AuthorizationScope.Site(siteId),
            async (connection, transaction, ct) =>
            {
                var machines = new List<MachineInventoryItem>();
                await using var query = new SqlCommand("""
                    SELECT m.MachineId,m.SiteId,m.DisplayName,m.IsRetired
                    FROM [master].Machine m
                    JOIN [master].Site s ON s.SiteId=m.SiteId
                    WHERE m.SiteId=@site
                    ORDER BY m.MachineId;
                    """, connection, transaction);
                query.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    machines.Add(new MachineInventoryItem(reader.GetGuid(0), reader.GetGuid(1),
                        reader.GetString(2), reader.GetBoolean(3)));
                return (IReadOnlyList<MachineInventoryItem>)machines;
            }, cancellationToken);
    }
}
