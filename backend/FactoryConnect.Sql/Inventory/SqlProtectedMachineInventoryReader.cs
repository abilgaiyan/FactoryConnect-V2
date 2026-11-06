using System.Diagnostics;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Inventory;

/// <summary>
/// Internal source inventory only. Reads are authorized and fully materialized while
/// the shared security gate is held. Not an active runtime roster or an HTTP endpoint.
/// </summary>
public sealed class SqlProtectedMachineInventoryReader(string connectionString, Guid installationId)
{
    private static readonly ActivitySource Diagnostics = new("FactoryConnect.V2.Inventory");

    public async Task<IReadOnlyList<MachineInventoryItem>> ReadSiteAsync(
        byte[] sessionHash, Guid siteId, CancellationToken cancellationToken = default)
    {
        if (siteId == Guid.Empty) throw new ArgumentException("Explicit site is required.", nameof(siteId));

        using var activity = Diagnostics.StartActivity("inventory.read-site", ActivityKind.Internal);
        var start = Stopwatch.GetTimestamp();
        var count = 0;
        var commands = 0;
        var successful = false;
        try
        {
            var authority = new SqlSecurityAuthority(connectionString, installationId);
            var result = await authority.ReadAsync(sessionHash, "configuration.read", AuthorizationScope.Site(siteId),
                async (connection, transaction, ct) =>
                {
                    var machines = new List<MachineInventoryItem>();
                    await using var query = new SqlCommand("""
                        SELECT m.MachineId,m.SiteId,m.DisplayName,m.IsRetired
                        FROM [master].Machine m
                        WHERE m.SiteId=@site
                        ORDER BY m.MachineId;
                        """, connection, transaction);
                    query.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                    commands++;
                    await using var reader = await query.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        machines.Add(new MachineInventoryItem(reader.GetGuid(0), reader.GetGuid(1),
                            reader.GetString(2), reader.GetBoolean(3)));
                    return (IReadOnlyList<MachineInventoryItem>)machines;
                }, cancellationToken);
            count = result.Count;
            successful = true;
            return result;
        }
        finally
        {
            // Metadata only; no site/machine identities, SQL text, credentials, tokens,
            // display names, evidence values or connection strings in diagnostic tags.
            if (activity is not null)
            {
                activity.SetTag("inventory.result", successful ? "completed" : "unavailable");
                activity.SetTag("inventory.rows", count);
                activity.SetTag("inventory.reader.commands", commands);
                activity.SetTag("inventory.elapsed.ms", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
    }
}
