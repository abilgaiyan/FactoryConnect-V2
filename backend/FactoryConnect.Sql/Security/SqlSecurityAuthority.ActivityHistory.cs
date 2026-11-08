using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.ActivityProcessing;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed record PublishedActivitySeries(Guid MappingId,string SourceAddress,string QualificationSha256,
    string Generation,DateTimeOffset LastObservedAt,Guid PackageRevisionId,Guid AcknowledgementId);
public sealed record PublishedActivityPeriod(Guid EffectId,Guid PublicationOperationId,string State,
    DateTimeOffset StartedAt,DateTimeOffset EndedAt,string DurationTicks,string InstanceId,
    string StartSequence,string EndSequence,string StartRawValue,string EndRawValue);
public sealed record PublishedActivityPage(PublishedActivitySeries Series,IReadOnlyList<PublishedActivityPeriod> Rows,string? NextCursor);

public sealed partial class SqlSecurityAuthority
{
    private sealed record HistoryCursor(Guid MachineId,string StreamKey,Guid MappingId,string Qualification,
        string StartedAt,string InstanceId,string StartSequence,Guid EffectId);
    private sealed record StoredActivity(ActivityProcessorCheckpoint Checkpoint,PublishedActivitySeries Series);
    private static readonly UTF8Encoding ActivityUtf8=new(false,true);

    public Task<IReadOnlyList<PublishedActivitySeries>> ReadActivitySeriesAsync(byte[] sessionHash,
        Guid siteId,Guid machineId,string streamKey,CancellationToken ct=default)
        => ActivityHistoryRead(sessionHash,siteId,machineId,streamKey,null,async(c,t)=> {
            var stored=await LoadActivityCheckpoints(c,t,siteId,machineId,streamKey,null,ct);
            return (IReadOnlyList<PublishedActivitySeries>)stored.Select(x=>x.Series).ToArray();
        },ct);

    public Task<PublishedActivityPage> ReadActivityPeriodsAsync(byte[] sessionHash,Guid siteId,Guid machineId,
        string streamKey,Guid mappingId,int take=20,string? cursor=null,CancellationToken ct=default)
    {
        if(mappingId==Guid.Empty || take is <1 or >100 || cursor?.Length>2048)
            throw new ArgumentException("Exact mapping and bounded activity page required.");
        return ActivityHistoryRead(sessionHash,siteId,machineId,streamKey,mappingId,async(c,t)=> {
            var stored=(await LoadActivityCheckpoints(c,t,siteId,machineId,streamKey,mappingId,ct)).SingleOrDefault()
                ?? throw new KeyNotFoundException("No published activity series.");
            var identity=stored.Checkpoint.Qualification;
            var after=DecodeCursor(cursor,machineId,streamKey,mappingId,stored.Series.QualificationSha256);
            var cursorFilter=after is null?"": """
                AND StartedAt<=@afterTime AND (StartedAt<@afterTime OR (StartedAt=@afterTime AND
                    (InstanceId<@instance OR (InstanceId=@instance AND (StartSequence<@sequence OR
                    (StartSequence=@sequence AND EffectId<@effect))))))
                """;
            await using var q=Command(c,t,"""
                DECLARE @selected TABLE(EffectId uniqueidentifier NOT NULL PRIMARY KEY);
                INSERT @selected SELECT TOP (@limit) EffectId FROM activity.ClosedPeriod WITH(HOLDLOCK,INDEX(IX_ActivityClosedPeriod_Keyset),FORCESEEK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                    /*cursor-filter*/
                ORDER BY StartedAt DESC,InstanceId DESC,StartSequence DESC,EffectId DESC;
                IF COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(p.StartRawValue))+DATALENGTH(p.EndRawValue)+512)
                    FROM @selected selected INNER LOOP JOIN activity.ClosedPeriod p WITH(HOLDLOCK,FORCESEEK) ON p.EffectId=selected.EffectId),0)
                    +COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(o.ResultBytes)))
                    FROM (SELECT DISTINCT p.OperationId FROM @selected selected INNER LOOP JOIN activity.ClosedPeriod p WITH(HOLDLOCK,FORCESEEK) ON p.EffectId=selected.EffectId) ids
                    INNER LOOP JOIN activity.ProcessorOperation o WITH(HOLDLOCK,FORCESEEK) ON o.OperationId=ids.OperationId),0)>8388608
                    THROW 51091,'Activity publication page exceeds byte budget.',1;
                SELECT p.EffectId,p.OperationId,p.QualificationHash,p.State,p.StartedAt,p.EndedAt,
                    p.DurationTicks,p.InstanceId,p.StartSequence,p.EndSequence,p.StartRawValue,p.EndRawValue
                FROM @selected selected INNER LOOP JOIN activity.ClosedPeriod p WITH(HOLDLOCK,FORCESEEK) ON p.EffectId=selected.EffectId
                ORDER BY p.StartedAt DESC,p.InstanceId DESC,p.StartSequence DESC,p.EffectId DESC;
                SELECT o.OperationId,o.AppliedGeneration,o.ResultBytes
                FROM (SELECT DISTINCT p.OperationId FROM @selected selected INNER LOOP JOIN activity.ClosedPeriod p WITH(HOLDLOCK,FORCESEEK) ON p.EffectId=selected.EffectId) ids
                INNER LOOP JOIN activity.ProcessorOperation o WITH(HOLDLOCK,FORCESEEK) ON o.OperationId=ids.OperationId
                WHERE o.MachineId=@machine AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping;
                """.Replace("/*cursor-filter*/",cursorFilter,StringComparison.Ordinal));
            Add(q,("@machine",machineId),("@stream",ActivityUtf8.GetBytes(streamKey)),("@mapping",mappingId),
                ("@limit",take+1),
                ("@afterTime",(object?)after?.StartedAt??DBNull.Value),("@effect",(object?)after?.EffectId??DBNull.Value));
            q.Parameters["@stream"].Size=512; // Match the indexed binary key; MAX creates a range/residual seek.
            q.Parameters["@afterTime"].SqlDbType=SqlDbType.DateTimeOffset;
            q.Parameters["@afterTime"].Value=after is null?DBNull.Value:DateTimeOffset.ParseExact(after.StartedAt,"O",CultureInfo.InvariantCulture);
            Number(q,"@instance",after is null?null:ulong.Parse(after.InstanceId,CultureInfo.InvariantCulture));
            Number(q,"@sequence",after is null?null:ulong.Parse(after.StartSequence,CultureInfo.InvariantCulture));
            var rows=new List<(PublishedActivityPeriod Row,ClosedActivityPeriod Period)>();
            var receipts=new Dictionary<Guid,ActivityDurableResult>();
            var receiptIds=new List<Guid>();
            await using(var r=await q.ExecuteReaderAsync(ct))
            {
                while(await r.ReadAsync(ct))
                {
                    if(!((byte[])r[2]).AsSpan().SequenceEqual(Convert.FromHexString(stored.Series.QualificationSha256)))
                        throw new InvalidDataException("Published period qualification differs from checkpoint.");
                    if(!Enum.TryParse<ActivityState>(r.GetString(3),out var state)||!Enum.IsDefined(state)||state.ToString()!=r.GetString(3))
                        throw new InvalidDataException("Invalid published state.");
                    var period=new ClosedActivityPeriod(machineId,streamKey,mappingId,state,r.GetDateTimeOffset(4),
                        r.GetDateTimeOffset(5),r.GetInt64(6),decimal.ToUInt64(r.GetDecimal(7)),
                        decimal.ToUInt64(r.GetDecimal(8)),decimal.ToUInt64(r.GetDecimal(9)),r.GetString(10),r.GetString(11));
                    if(period.StartedAt.Offset!=TimeSpan.Zero || period.EndedAt.Offset!=TimeSpan.Zero ||
                        period.DurationTicks<=0 || period.EndedAt.UtcTicks-period.StartedAt.UtcTicks!=period.DurationTicks ||
                        period.EndSequence<=period.StartSequence || SqlActivityProcessorStore.PeriodEffect(identity,period)!=r.GetGuid(0))
                        throw new InvalidDataException("Published period integrity mismatch.");
                    rows.Add((new(r.GetGuid(0),r.GetGuid(1),state.ToString(),period.StartedAt,period.EndedAt,
                        period.DurationTicks.ToString(CultureInfo.InvariantCulture),period.InstanceId.ToString(CultureInfo.InvariantCulture),
                        period.StartSequence.ToString(CultureInfo.InvariantCulture),period.EndSequence.ToString(CultureInfo.InvariantCulture),
                        period.StartRawValue,period.EndRawValue),period));
                }
                await r.NextResultAsync(ct);
                while(await r.ReadAsync(ct))
                {
                    var receipt=JsonSerializer.Deserialize<ActivityDurableResult>((byte[])r[2])
                        ?? throw new InvalidDataException("Published operation result missing.");
                    if(receipt.Replayed || receipt.Checkpoint.Qualification!=identity ||
                        receipt.Checkpoint.AcceptedSource?.OperationId!=r.GetGuid(0) ||
                        receipt.Checkpoint.NextGeneration!=(ulong)r.GetInt64(1) || receipt.NewPeriods.Count>1000)
                        throw new InvalidDataException("Published operation ancestry mismatch.");
                    receipts.Add(r.GetGuid(0),receipt);
                    receiptIds.Add(r.GetGuid(0));
                }
            }
            foreach(var receiptId in receiptIds)
                await VerifyActivityPublicationReceiptAsync(c,t,receiptId,identity,ct);
            foreach(var item in rows)
                if(!receipts.TryGetValue(item.Row.PublicationOperationId,out var receipt) ||
                    receipt.NewPeriods.Count(x=>x==item.Period)!=1)
                    throw new InvalidDataException("Period differs from its immutable publication receipt.");
            var more=rows.Count>take;
            if(more)rows.RemoveAt(rows.Count-1);
            var tail=rows.LastOrDefault().Row;
            var next=!more||tail is null?null:EncodeCursor(new(machineId,streamKey,mappingId,
                stored.Series.QualificationSha256,tail.StartedAt.ToString("O",CultureInfo.InvariantCulture),
                tail.InstanceId,tail.StartSequence,tail.EffectId));
            return new PublishedActivityPage(stored.Series,rows.Select(x=>x.Row).ToArray(),next);
        },ct);
    }

    private async Task<T> ActivityHistoryRead<T>(byte[] sessionHash,Guid site,Guid machine,string stream,Guid? mapping,
        Func<SqlConnection,SqlTransaction,Task<T>> read,CancellationToken ct)
    {
        if(site==Guid.Empty||machine==Guid.Empty||string.IsNullOrWhiteSpace(stream)||ActivityUtf8.GetByteCount(stream)>512)
            throw new ArgumentException("Exact activity scope required.");
        var token=sessionHash.ToArray();
        try
        {
            return await Transaction(null,false,async(c,t)=> {
                Activity.Current?.SetTag("security.activity.read",true);
                await Lock(c,t,ConfigurationLockResources.Machine(installationId,machine),false,ct);
                if(mapping.HasValue)await Lock(c,t,SqlActivityProcessorStore.ProcessorLockResource(machine,mapping.Value,stream),false,ct);
                async Task Validate()
                {
                    var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
                    await Authorize(c,t,actor,"commissioning.read",AuthorizationScope.Machine(site,machine),state.Now,ct);
                }
                await Validate();
                await using(var master=Command(c,t,"SELECT 1 FROM [master].Machine m JOIN [master].Site s ON s.SiteId=m.SiteId WHERE m.MachineId=@machine AND m.SiteId=@site AND m.IsRetired=0 AND s.IsRetired=0"))
                { Add(master,("@site",site),("@machine",machine));if(await master.ExecuteScalarAsync(ct) is null)throw new SecurityDeniedException(); }
                var result=await read(c,t);
                await Validate(); // Expiry after row/processor waits cannot release data.
                return result;
            },ct);
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private static async Task<IReadOnlyList<StoredActivity>> LoadActivityCheckpoints(SqlConnection c,SqlTransaction t,
        Guid site,Guid machine,string stream,Guid? mapping,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            DECLARE @selected TABLE(MappingId uniqueidentifier NOT NULL PRIMARY KEY);
            INSERT @selected SELECT TOP(101) MappingId FROM activity.ProcessorCheckpoint WITH(HOLDLOCK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND (@mapping IS NULL OR MappingId=@mapping)
                ORDER BY MappingId;
            IF (SELECT COUNT(*) FROM @selected)>100 THROW 51091,'Activity series count exceeds bound.',1;
            IF COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(p.CheckpointJson))) FROM @selected s
                INNER LOOP JOIN activity.ProcessorCheckpoint p WITH(HOLDLOCK,FORCESEEK) ON p.MachineId=@machine AND p.StreamKeyBytes=@stream AND p.MappingId=s.MappingId),0)>8388608
                THROW 51091,'Activity checkpoint bytes exceed bound.',1;
            IF COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(p.CheckpointJson))+COALESCE(DATALENGTH(o.ResultBytes),0))
                FROM @selected s INNER LOOP JOIN activity.ProcessorCheckpoint p WITH(HOLDLOCK,FORCESEEK) ON p.MachineId=@machine AND p.StreamKeyBytes=@stream AND p.MappingId=s.MappingId
                LEFT LOOP JOIN activity.ProcessorOperation o WITH(HOLDLOCK,FORCESEEK) ON o.OperationId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.CheckpointJson,'$.AcceptedSource.OperationId'))),0)>8388608
                THROW 51091,'Activity ancestry bytes exceed bound.',1;
            SELECT p.MappingId,p.QualificationHash,p.Generation,p.CheckpointJson,p.CheckpointHash,
                o.OperationId,o.AppliedGeneration,o.ResultBytes
            FROM @selected s INNER LOOP JOIN activity.ProcessorCheckpoint p WITH(HOLDLOCK,FORCESEEK)
                ON p.MachineId=@machine AND p.StreamKeyBytes=@stream AND p.MappingId=s.MappingId
            LEFT LOOP JOIN activity.ProcessorOperation o WITH(HOLDLOCK,FORCESEEK)
                ON o.OperationId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.CheckpointJson,'$.AcceptedSource.OperationId'))
                    AND o.MachineId=p.MachineId AND o.StreamKeyBytes=p.StreamKeyBytes AND o.MappingId=p.MappingId
            ORDER BY p.MappingId;
            """);
        Add(q,("@machine",machine),("@stream",ActivityUtf8.GetBytes(stream)),("@mapping",(object?)mapping??DBNull.Value));
        q.Parameters["@stream"].Size=512;
        q.Parameters["@mapping"].SqlDbType=SqlDbType.UniqueIdentifier;
        var result=new List<StoredActivity>();
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var json=r.GetString(3);
            if(!SHA256.HashData(Encoding.Unicode.GetBytes(json)).AsSpan().SequenceEqual((byte[])r[4]))
                throw new InvalidDataException("Activity checkpoint checksum mismatch.");
            var cp=JsonSerializer.Deserialize<ActivityProcessorCheckpoint>(json)??throw new InvalidDataException("Missing activity checkpoint.");
            var source=cp.AcceptedSource;
            if(cp.Qualification.MachineId!=machine || cp.Qualification.StreamKey!=stream || cp.Qualification.MappingId!=r.GetGuid(0) ||
                source is null || (source.LastObservation?.SiteId??source.SiteId)!=site ||
                cp.NextGeneration!=(ulong)r.GetInt64(2) || cp.NextGeneration==0 ||
                !SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(cp.Qualification)).AsSpan().SequenceEqual((byte[])r[1]) || r.IsDBNull(7))
                throw new InvalidDataException("Activity checkpoint lacks accepted publication ancestry.");
            var receipt=JsonSerializer.Deserialize<ActivityDurableResult>((byte[])r[7]);
            if(receipt is null || receipt.Replayed || receipt.Checkpoint!=cp || source.OperationId!=r.GetGuid(5) ||
                receipt.Checkpoint.NextGeneration!=(ulong)r.GetInt64(6))
                throw new InvalidDataException("Activity checkpoint differs from its publication receipt.");
            if(cp.LastConsumedSample is null)
            {
                if(source.LastObservation is not null || source.FrontierBatch is null || cp.OpenAnchor is not null || cp.OpenState is not null || receipt.NewPeriods.Count!=0)
                    throw new InvalidDataException("Unobserved progress cannot establish an activity series.");
                continue;
            }
            result.Add(new(cp,new(cp.Qualification.MappingId,cp.Qualification.SourceAddress,Convert.ToHexString((byte[])r[1]),
                cp.NextGeneration.ToString(CultureInfo.InvariantCulture),cp.LastConsumedSample.ObservedAt,source.PackageRevisionId,source.AcknowledgementId)));
        }
        return result;
    }
    private static void Number(SqlCommand q,string name,ulong? value)
    { var p=q.Parameters.Add(name,SqlDbType.Decimal);p.Precision=20;p.Scale=0;p.Value=value.HasValue?(decimal)value.Value:DBNull.Value; }
    private static string EncodeCursor(HistoryCursor value)=>Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static HistoryCursor? DecodeCursor(string? value,Guid machine,string stream,Guid mapping,string qualification)
    {
        if(value is null)return null;
        try
        {
            var raw=value.Replace('-','+').Replace('_','/');raw=raw.PadRight((raw.Length+3)/4*4,'=');
            var cursor=JsonSerializer.Deserialize<HistoryCursor>(Convert.FromBase64String(raw));
            if(cursor is null||cursor.MachineId!=machine||cursor.StreamKey!=stream||cursor.MappingId!=mapping||cursor.Qualification!=qualification||
                cursor.EffectId==Guid.Empty || !DateTimeOffset.TryParseExact(cursor.StartedAt,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time) ||
                time.Offset!=TimeSpan.Zero || !ulong.TryParse(cursor.InstanceId,NumberStyles.None,CultureInfo.InvariantCulture,out _) ||
                !ulong.TryParse(cursor.StartSequence,NumberStyles.None,CultureInfo.InvariantCulture,out _))throw new ArgumentException("Activity cursor scope or qualification mismatch.");
            return cursor;
        }
        catch(Exception error)when(error is FormatException or JsonException){throw new ArgumentException("Invalid activity cursor.",error);}
    }
}
