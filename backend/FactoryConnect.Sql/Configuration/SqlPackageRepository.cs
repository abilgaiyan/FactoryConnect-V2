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
    public Task<byte[]> InsertSubmittedAsync(SqlConnection connection, SqlTransaction transaction,
        PackageDefinition definition, CancellationToken cancellationToken = default)
        => InsertCanonicalAsync(connection,transaction,PackageCanonicalizer.Serialize(definition),cancellationToken);

    internal async Task<byte[]> InsertCanonicalAsync(SqlConnection connection,SqlTransaction transaction,
        byte[] bytes,CancellationToken cancellationToken)
    {
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
        using var machineRows=new DataTable();
        machineRows.Columns.Add("MachineId",typeof(Guid));machineRows.Columns.Add("ProductionLineId",typeof(Guid));
        machineRows.Columns.Add("EffectiveFrom",typeof(DateTimeOffset));machineRows.Columns.Add("EffectiveTo",typeof(DateTimeOffset));
        machineRows.Columns.Add("ScheduleRevisionId",typeof(Guid));machineRows.Columns.Add("ScheduleHash",typeof(byte[]));
        machineRows.Columns.Add("BreakPolicyRevisionId",typeof(Guid));machineRows.Columns.Add("BreakPolicyHash",typeof(byte[]));
        using var signalRows=new DataTable();
        signalRows.Columns.Add("MappingId",typeof(Guid));signalRows.Columns.Add("MachineId",typeof(Guid));
        foreach(var column in new[]{"SemanticKey","Protocol","StreamKey","AddressEncoding"})signalRows.Columns.Add(column,typeof(string));
        signalRows.Columns.Add("AddressBytes",typeof(byte[]));signalRows.Columns.Add("Normalization",typeof(string));
        foreach(var machine in machines)
        {
            var machineId=machine.GetProperty("machineId").GetGuid();var schedule=machine.GetProperty("schedule");var breaks=machine.GetProperty("breakPolicy");
            machineRows.Rows.Add(machineId,machine.GetProperty("productionLineId").GetGuid(),machine.GetProperty("effectiveFrom").GetDateTimeOffset(),
                machine.GetProperty("effectiveTo").ValueKind==JsonValueKind.Null?DBNull.Value:machine.GetProperty("effectiveTo").GetDateTimeOffset(),
                schedule.GetProperty("revisionId").GetGuid(),Digest(schedule),breaks.GetProperty("revisionId").GetGuid(),Digest(breaks));
            foreach(var signal in machine.GetProperty("signals").EnumerateArray())
                signalRows.Rows.Add(signal.GetProperty("mappingId").GetGuid(),machineId,signal.GetProperty("semanticKey").GetString(),
                    signal.GetProperty("protocol").GetString(),signal.GetProperty("streamKey").GetString(),signal.GetProperty("addressEncoding").GetString(),
                    signal.GetProperty("addressBytes").GetBytesFromBase64(),signal.GetProperty("normalization").GetString());
        }
        await Execute("""
            INSERT configuration.PackageMachineScope(PackageRevisionId,SiteId,MachineId)
                SELECT @revision,@site,MachineId FROM @machines;
            INSERT configuration.PackageMachineAssignment(PackageRevisionId,SiteId,MachineId,ProductionLineId,
                EffectiveFrom,EffectiveTo,ScheduleRevisionId,ScheduleHash,BreakPolicyRevisionId,BreakPolicyHash)
                SELECT @revision,@site,MachineId,ProductionLineId,EffectiveFrom,EffectiveTo,ScheduleRevisionId,ScheduleHash,BreakPolicyRevisionId,BreakPolicyHash FROM @machines;
            INSERT configuration.PackageSignalMapping(MappingId,PackageRevisionId,MachineId,SemanticKey,Protocol,StreamKey,AddressEncoding,AddressBytes,Normalization)
                SELECT MappingId,@revision,MachineId,SemanticKey,Protocol,StreamKey,AddressEncoding,AddressBytes,Normalization FROM @signals;
            """,("@revision",revisionId),("@site",siteId),("@machines",machineRows),("@signals",signalRows));
        await Execute("UPDATE configuration.ConfigurationPackageRevision SET SubmittedAt=SYSUTCDATETIME() WHERE PackageRevisionId=@revision",("@revision",revisionId));
        return hash;

        async Task Execute(string sql, params (string Name,object Value)[] parameters)
        {
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
            foreach (var (name,value) in parameters)
            {
                if(value is DataTable table)
                {var parameter=command.Parameters.Add(name,SqlDbType.Structured);parameter.TypeName=name=="@machines"?"configuration.PackageMachineBatch":"configuration.PackageSignalBatch";parameter.Value=table;}
                else if (value is byte[] binary) command.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=binary;
                else if (name is "@from" or "@to") command.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=value;
                else command.Parameters.AddWithValue(name,value);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
    private static byte[] Digest(JsonElement policy) => Convert.FromHexString(policy.GetProperty("contentHash").GetString()!);
}
