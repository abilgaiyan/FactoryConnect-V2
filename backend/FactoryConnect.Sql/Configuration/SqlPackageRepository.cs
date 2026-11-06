using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Configuration;

/// <summary>
/// Trusted persistence primitive, not an authorization boundary. The caller owns the transaction,
/// required authorization/operation/policy locks, and rollback on any failure. No HTTP endpoint registers this primitive.
/// </summary>
public sealed class SqlPackageRepository
{
    public async Task<byte[]> InsertSubmittedAsync(SqlConnection connection, SqlTransaction transaction,
        PackageDefinition definition, CancellationToken cancellationToken = default)
    {
        var bytes = PackageCanonicalizer.Serialize(definition);
        var hash = SHA256.HashData(bytes);
        // Derive every normalized value from the same immutable byte snapshot that is hashed/stored.
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var packageId = root.GetProperty("packageId").GetGuid();
        var revisionId = root.GetProperty("revisionId").GetGuid();
        var siteId = root.GetProperty("siteId").GetGuid();
        var timezone = root.GetProperty("timezone");
        var machines = root.GetProperty("machines").EnumerateArray().ToArray();
        await Execute("""
            IF NOT EXISTS(SELECT 1 FROM configuration.ConfigurationPackage WITH(UPDLOCK,HOLDLOCK) WHERE PackageId=@package)
                INSERT configuration.ConfigurationPackage(PackageId,SiteId) VALUES(@package,@site);
            INSERT configuration.ConfigurationPackageRevision
                (PackageRevisionId,SiteId,PackageId,RevisionNumber,CanonicalizationVersion,CanonicalBytes,ContentHash,
                 TimezoneRevisionId,TimezoneHash,MachineCount,MappingCount)
            VALUES(@revision,@site,@package,@number,1,@bytes,@hash,@timezone,@timezoneHash,@machines,@mappings);
            """, ("@package",packageId),("@site",siteId),("@revision",revisionId),
            ("@number",root.GetProperty("revisionNumber").GetInt32()),("@bytes",bytes),("@hash",hash),
            ("@timezone",timezone.GetProperty("revisionId").GetGuid()),("@timezoneHash",Digest(timezone)),
            ("@machines",machines.Length),("@mappings",machines.Sum(m=>m.GetProperty("signals").GetArrayLength())));
        foreach (var machine in machines)
        {
            var machineId = machine.GetProperty("machineId").GetGuid();
            var schedule = machine.GetProperty("schedule"); var breaks = machine.GetProperty("breakPolicy");
            await Execute("""
                INSERT configuration.PackageMachineScope(PackageRevisionId,SiteId,MachineId) VALUES(@revision,@site,@machine);
                INSERT configuration.PackageMachineAssignment(PackageRevisionId,SiteId,MachineId,ProductionLineId,
                    EffectiveFrom,EffectiveTo,ScheduleRevisionId,ScheduleHash,BreakPolicyRevisionId,BreakPolicyHash)
                VALUES(@revision,@site,@machine,@line,@from,@to,@schedule,@scheduleHash,@breaks,@breakHash);
                """,("@revision",revisionId),("@site",siteId),("@machine",machineId),
                ("@line",machine.GetProperty("productionLineId").GetGuid()),
                ("@from",machine.GetProperty("effectiveFrom").GetDateTimeOffset()),
                ("@to",machine.GetProperty("effectiveTo").ValueKind == JsonValueKind.Null ? DBNull.Value : machine.GetProperty("effectiveTo").GetDateTimeOffset()),
                ("@schedule",schedule.GetProperty("revisionId").GetGuid()),("@scheduleHash",Digest(schedule)),
                ("@breaks",breaks.GetProperty("revisionId").GetGuid()),("@breakHash",Digest(breaks)));
            foreach (var signal in machine.GetProperty("signals").EnumerateArray())
                await Execute("""
                    INSERT configuration.PackageSignalMapping(MappingId,PackageRevisionId,MachineId,
                        SemanticKey,Protocol,StreamKey,AddressEncoding,AddressBytes,Normalization)
                    VALUES(@mapping,@revision,@machine,@semantic,@protocol,@stream,@encoding,@address,@normalization);
                    """,("@mapping",signal.GetProperty("mappingId").GetGuid()),("@revision",revisionId),("@machine",machineId),
                    ("@semantic",signal.GetProperty("semanticKey").GetString()!), ("@protocol",signal.GetProperty("protocol").GetString()!),
                    ("@stream",signal.GetProperty("streamKey").GetString()!), ("@encoding",signal.GetProperty("addressEncoding").GetString()!),
                    ("@address",signal.GetProperty("addressBytes").GetBytesFromBase64()),("@normalization",signal.GetProperty("normalization").GetString()!));
        }
        await Execute("UPDATE configuration.ConfigurationPackageRevision SET SubmittedAt=SYSUTCDATETIME() WHERE PackageRevisionId=@revision",("@revision",revisionId));
        return hash;

        async Task Execute(string sql, params (string Name,object Value)[] parameters)
        {
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
            foreach (var (name,value) in parameters)
            {
                if (value is byte[] binary) command.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=binary;
                else if (name is "@from" or "@to") command.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=value;
                else command.Parameters.AddWithValue(name,value);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
    private static byte[] Digest(JsonElement policy) => Convert.FromHexString(policy.GetProperty("contentHash").GetString()!);
}
