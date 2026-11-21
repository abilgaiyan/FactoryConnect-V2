using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Configuration;

/// <summary>Immutable, idempotent policy persistence. Caller supplies an authorized site and exact snapshots.</summary>
public sealed class SqlPolicyRevisionWriter
{
    public sealed record Revision(Guid RevisionId, byte[] ContentHash);

    public static async Task<Revision> SaveScheduleAsync(SqlConnection connection, Guid siteId,
        IReadOnlyList<PolicyCanonicalizer.Shift> shifts, CancellationToken cancellationToken = default)
        => await SaveAsync(connection, siteId, "configuration.SharedScheduleRevision",
            "ScheduleRevisionId", PolicyCanonicalizer.Schedule(shifts), cancellationToken);

    public static async Task<Revision> SaveBreakPolicyAsync(SqlConnection connection, Guid siteId,
        IReadOnlyList<PolicyCanonicalizer.Break> breaks, CancellationToken cancellationToken = default)
        => await SaveAsync(connection, siteId, "configuration.SharedBreakPolicyRevision",
            "BreakPolicyRevisionId", PolicyCanonicalizer.BreakPolicy(breaks), cancellationToken);

    public static async Task<Revision> SaveTimezoneAsync(SqlConnection connection, Guid siteId,
        PolicyCanonicalizer.Timezone zone, CancellationToken cancellationToken = default)
    {
        var canonical = PolicyCanonicalizer.TimezonePolicy(zone);
        var rules = zone.RulesBytes.ToArray();
        var hash = PolicyCanonicalizer.Hash(canonical);
        return await SaveAsync(connection, siteId, "configuration.SiteTimezoneRevision",
            "TimezoneRevisionId", canonical, cancellationToken, async (command, revisionId) =>
            {
                command.CommandText = """
                    INSERT configuration.SiteTimezoneRevision
                    (TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,
                     RulesBytes,RulesSha256,CanonicalBytes,ContentHash)
                    VALUES (@id,@site,@iana,@tzdb,@resolver,@gap,@fold,@rules,@rulesHash,@bytes,@hash)
                    """;
                command.Parameters.Add("@iana", SqlDbType.NVarChar, 200).Value = zone.IanaZoneId;
                command.Parameters.Add("@tzdb", SqlDbType.VarChar, 32).Value = zone.TzdbRelease;
                command.Parameters.Add("@resolver", SqlDbType.NVarChar, 200).Value = zone.ResolverVersion;
                command.Parameters.Add("@gap", SqlDbType.VarChar, 32).Value = zone.GapPolicy;
                command.Parameters.Add("@fold", SqlDbType.VarChar, 32).Value = zone.FoldPolicy;
                command.Parameters.Add("@rules", SqlDbType.VarBinary, -1).Value = rules;
                command.Parameters.Add("@rulesHash", SqlDbType.Binary, 32).Value = SHA256.HashData(rules);
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
    }

    private static async Task<Revision> SaveAsync(SqlConnection connection, Guid siteId,
        string table, string idColumn, byte[] canonical, CancellationToken ct,
        Func<SqlCommand, Guid, Task>? insert = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (siteId == Guid.Empty) throw new ArgumentException("Site required.", nameof(siteId));
        if (connection.State != ConnectionState.Open) throw new InvalidOperationException("Open SQL connection required.");
        var bytes = canonical.ToArray();
        var hash = PolicyCanonicalizer.Hash(bytes);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // A transaction-owned lock serializes identical snapshots across concurrent callers.
            await using (var gate = new SqlCommand("""
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
                    @LockOwner='Transaction',@LockTimeout=10000;
                IF @result<0 THROW 51046,'Policy revision lock unavailable.',1;
                """, connection, tx))
            {
                gate.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value =
                    $"fc2:policy-write:{siteId:D}:{Convert.ToHexString(hash)}";
                await gate.ExecuteNonQueryAsync(ct);
            }
            // Compare exact bytes, not just a digest. SQL constraints independently verify SHA-256.
            await using (var find = new SqlCommand($"""
                SELECT {idColumn},CanonicalBytes FROM {table} WITH (UPDLOCK,HOLDLOCK)
                WHERE SiteId=@site AND ContentHash=@hash
                """, connection, tx))
            {
                find.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId;
                find.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = hash;
                await using var reader = await find.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetGuid(0);
                    var existing = (byte[])reader.GetValue(1);
                    if (existing.AsSpan().SequenceEqual(bytes))
                    {
                        await reader.DisposeAsync();
                        await tx.CommitAsync(ct);
                        return new Revision(id, hash);
                    }
                    throw new InvalidOperationException("Policy hash collision or corrupt canonical content.");
                }
            }
            var newId = Guid.NewGuid();
            await using (var command = new SqlCommand($"""
                INSERT {table} ({idColumn},SiteId,CanonicalBytes,ContentHash)
                VALUES (@id,@site,@bytes,@hash)
                """, connection, tx))
            {
                command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = newId;
                command.Parameters.Add("@site", SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@bytes", SqlDbType.VarBinary, -1).Value = bytes;
                command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = hash;
                if (insert is null) await command.ExecuteNonQueryAsync(ct);
                else await insert(command, newId);
            }
            await tx.CommitAsync(ct);
            return new Revision(newId, hash);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
