using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

public sealed record ActivityDurableResult(
    ActivityProcessorCheckpoint Checkpoint,
    IReadOnlyList<ClosedActivityPeriod> NewPeriods,
    bool Replayed);

public sealed record ActivityPeriodCursor(
    DateTimeOffset StartedAt,ulong InstanceId,ulong StartSequence,Guid EffectId);

public sealed record ActivityPeriodRow(
    ClosedActivityPeriod Period,ActivityQualificationIdentity Qualification,
    string PersistedQualificationSha256,ActivityPeriodCursor Cursor);

public sealed record ActivityPeriodPage(
    IReadOnlyList<ActivityPeriodRow> Rows,ActivityPeriodCursor? Next);

/// <summary>Serializable atomic checkpoint and closed-period commit.
/// Caller supplies only independently qualified, retained input; this slice
/// does not turn raw counters into state nor claim physical continuity.</summary>
public sealed class SqlActivityProcessorStore(string connectionString)
{
    private static readonly UTF8Encoding Utf8=new(false,true);

    public async Task<ActivityProcessorCheckpoint> ReadCheckpointAsync(
        ActivityQualificationIdentity identity,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var c=new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var q=new SqlCommand("""
            SELECT CheckpointJson,CheckpointHash,QualificationHash FROM activity.ProcessorCheckpoint
            WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
            """,c);
        var hash=QualificationHash(identity);
        BindScope(q,identity);
        await using var reader=await q.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return ActivityCheckpointEvaluator.Initial(identity);
        if(!((byte[])reader[2]).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("Persisted activity qualification changed.");
        var raw=reader.GetString(0);
        if(!SHA256.HashData(Encoding.Unicode.GetBytes(raw)).AsSpan().SequenceEqual((byte[])reader[1]))
            throw new InvalidDataException("Persisted checkpoint checksum mismatch.");
        var checkpoint=JsonSerializer.Deserialize<ActivityProcessorCheckpoint>(raw)
            ?? throw new InvalidDataException("Persisted checkpoint unavailable.");
        if(checkpoint.Qualification!=identity)
            throw new InvalidDataException("Persisted checkpoint scope or qualification mismatch.");
        return checkpoint;
    }

    public async Task<ActivityDurableResult> ApplyAsync(
        Guid operationId,QualifiedActivitySignal signal,ActivityQualificationIdentity identity,
        ulong expectedGeneration,IReadOnlyList<ActivityInputSample> samples,
        bool injectRollback=false,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(samples);
        if(operationId==Guid.Empty || samples.Count is <1 or >1000 ||
           expectedGeneration>long.MaxValue-1 ||
           identity!=ActivityQualificationIdentity.From(signal,identity.ContinuityIdentity))
            throw new ArgumentException("Exact bounded activity operation required.");
        var savedSamples=samples.ToArray();
        var request=JsonSerializer.SerializeToUtf8Bytes(new {
            version=1,operationId,identity,expectedGeneration,samples=savedSamples
        });
        var requestHash=SHA256.HashData(request);
        var resource=$"fc2:activity:{identity.MachineId:N}:{identity.MappingId:N}:{Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(identity.StreamKey)))}";
        await using var c=new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            await using(var guard=new SqlCommand("""
                DECLARE @r int;
                EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
                    @LockOwner='Transaction',@LockTimeout=10000;
                IF @r<0 THROW 51064,'Activity checkpoint lock unavailable.',1;
                """,c,t))
            {
                guard.Parameters.Add("@resource",SqlDbType.NVarChar,255).Value=resource;
                await guard.ExecuteNonQueryAsync(ct);
            }
            // Load exact operation receipt before checking expected generation:
            // an identical retry returns the original committed result.
            await using(var lookup=new SqlCommand("""
                SELECT RequestHash,RequestBytes,ResultBytes FROM activity.ProcessorOperation
                WHERE OperationId=@operation
                """,c,t))
            {
                lookup.Parameters.Add("@operation",SqlDbType.UniqueIdentifier).Value=operationId;
                await using var reader=await lookup.ExecuteReaderAsync(ct);
                if(await reader.ReadAsync(ct))
                {
                    if(!((byte[])reader[0]).AsSpan().SequenceEqual(requestHash) ||
                       !((byte[])reader[1]).AsSpan().SequenceEqual(request))
                        throw new InvalidOperationException("Activity operation ID already has different request bytes.");
                    var result=JsonSerializer.Deserialize<ActivityDurableResult>((byte[])reader[2])
                        ?? throw new InvalidDataException("Persisted activity operation result unavailable.");
                    await reader.DisposeAsync();
                    await t.CommitAsync(ct);
                    return result with {Replayed=true};
                }
            }
            ActivityProcessorCheckpoint checkpoint;
            var oldHash=QualificationHash(identity);
            await using(var q=new SqlCommand("""
                SELECT CheckpointJson,CheckpointHash,QualificationHash,Generation
                FROM activity.ProcessorCheckpoint WITH(UPDLOCK,HOLDLOCK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                """,c,t))
            {
                BindScope(q,identity);
                await using var reader=await q.ExecuteReaderAsync(ct);
                if(await reader.ReadAsync(ct))
                {
                    if(!((byte[])reader[2]).AsSpan().SequenceEqual(oldHash))
                        throw new InvalidDataException("Cannot reinterpret a persisted activity checkpoint.");
                    var raw=reader.GetString(0);
                    if(!((byte[])reader[1]).AsSpan().SequenceEqual(SHA256.HashData(Encoding.Unicode.GetBytes(raw))))
                        throw new InvalidDataException("Persisted activity checkpoint checksum mismatch.");
                    checkpoint=JsonSerializer.Deserialize<ActivityProcessorCheckpoint>(raw)
                        ?? throw new InvalidDataException("Persisted activity checkpoint unavailable.");
                    if(reader.GetInt64(3)!=checked((long)checkpoint.NextGeneration))
                        throw new InvalidDataException("Persisted activity generation mismatch.");
                }
                else checkpoint=ActivityCheckpointEvaluator.Initial(identity);
            }
            if(checkpoint.NextGeneration!=expectedGeneration)
                throw new InvalidOperationException("Stale activity processor generation.");
            var evaluated=ActivityCheckpointEvaluator.Advance(signal,identity,checkpoint,savedSamples);
            var resultBytes=JsonSerializer.SerializeToUtf8Bytes(
                new ActivityDurableResult(evaluated.Checkpoint,evaluated.ClosedPeriods,false));
            var snapshot=JsonSerializer.Serialize(evaluated.Checkpoint);
            await using(var q=new SqlCommand("""
                IF EXISTS(SELECT 1 FROM activity.ProcessorCheckpoint
                    WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping)
                    UPDATE activity.ProcessorCheckpoint SET Generation=@generation,
                        QualificationHash=@qual,LastInstanceId=@instance,LastSequence=@sequence,
                        CheckpointJson=@json,CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json)),
                        UpdatedAt=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')
                    WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping;
                ELSE
                    INSERT activity.ProcessorCheckpoint(
                        MachineId,StreamKeyBytes,MappingId,QualificationHash,Generation,
                        LastInstanceId,LastSequence,CheckpointJson,CheckpointHash,UpdatedAt)
                    VALUES(@machine,@stream,@mapping,@qual,@generation,@instance,@sequence,
                        @json,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json)),
                        TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'));
                """,c,t))
            {
                BindScope(q,identity);
                q.Parameters.Add("@generation",SqlDbType.BigInt).Value=checked((long)evaluated.Checkpoint.NextGeneration);
                q.Parameters.Add("@qual",SqlDbType.Binary,32).Value=oldHash;
                q.Parameters.Add("@instance",SqlDbType.Decimal).Value=evaluated.Checkpoint.LastConsumedSample is {} last
                    ? (object)Convert.ToDecimal(last.InstanceId,CultureInfo.InvariantCulture):DBNull.Value;
                q.Parameters.Add("@sequence",SqlDbType.Decimal).Value=evaluated.Checkpoint.LastConsumedSample is {} tail
                    ? (object)Convert.ToDecimal(tail.Sequence,CultureInfo.InvariantCulture):DBNull.Value;
                q.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=snapshot;
                await q.ExecuteNonQueryAsync(ct);
            }
            await using(var receipt=new SqlCommand("""
                INSERT activity.ProcessorOperation(OperationId,MachineId,StreamKeyBytes,MappingId,
                    RequestHash,RequestBytes,ExpectedGeneration,AppliedGeneration,ResultBytes,RecordedAt)
                VALUES(@op,@machine,@stream,@mapping,@hash,@bytes,@expected,@applied,@result,
                    TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'))
                """,c,t))
            {
                BindScope(receipt,identity);
                receipt.Parameters.Add("@op",SqlDbType.UniqueIdentifier).Value=operationId;
                receipt.Parameters.Add("@hash",SqlDbType.Binary,32).Value=requestHash;
                receipt.Parameters.Add("@bytes",SqlDbType.VarBinary,-1).Value=request;
                receipt.Parameters.Add("@expected",SqlDbType.BigInt).Value=checked((long)expectedGeneration);
                receipt.Parameters.Add("@applied",SqlDbType.BigInt).Value=checked((long)evaluated.Checkpoint.NextGeneration);
                receipt.Parameters.Add("@result",SqlDbType.VarBinary,-1).Value=resultBytes;
                await receipt.ExecuteNonQueryAsync(ct);
            }
            foreach(var period in evaluated.ClosedPeriods)
            {
                await using var insert=new SqlCommand("""
                    INSERT activity.ClosedPeriod(EffectId,OperationId,MachineId,StreamKeyBytes,MappingId,
                        QualificationHash,InstanceId,StartSequence,EndSequence,State,StartedAt,
                        EndedAt,DurationTicks,StartRawValue,EndRawValue)
                    VALUES(@effect,@operation,@machine,@stream,@mapping,@qual,@instance,@start,@end,
                        @state,@started,@ended,@ticks,@startValue,@endValue)
                    """,c,t);
                BindScope(insert,identity);
                insert.Parameters.Add("@effect",SqlDbType.UniqueIdentifier).Value=PeriodEffect(identity,period);
                insert.Parameters.Add("@operation",SqlDbType.UniqueIdentifier).Value=operationId;
                insert.Parameters.Add("@qual",SqlDbType.Binary,32).Value=oldHash;
                insert.Parameters.Add("@instance",SqlDbType.Decimal).Value=Convert.ToDecimal(period.InstanceId,CultureInfo.InvariantCulture);
                insert.Parameters.Add("@start",SqlDbType.Decimal).Value=Convert.ToDecimal(period.StartSequence,CultureInfo.InvariantCulture);
                insert.Parameters.Add("@end",SqlDbType.Decimal).Value=Convert.ToDecimal(period.EndSequence,CultureInfo.InvariantCulture);
                insert.Parameters.Add("@state",SqlDbType.VarChar,20).Value=period.State.ToString();
                insert.Parameters.Add("@started",SqlDbType.DateTimeOffset).Value=period.StartedAt;
                insert.Parameters.Add("@ended",SqlDbType.DateTimeOffset).Value=period.EndedAt;
                insert.Parameters.Add("@ticks",SqlDbType.BigInt).Value=period.DurationTicks;
                insert.Parameters.Add("@startValue",SqlDbType.NVarChar,-1).Value=period.StartRawValue;
                insert.Parameters.Add("@endValue",SqlDbType.NVarChar,-1).Value=period.EndRawValue;
                await insert.ExecuteNonQueryAsync(ct);
            }
            if(injectRollback)throw new InvalidOperationException("Controlled atomic activity rollback fixture.");
            await t.CommitAsync(ct);
            return new(evaluated.Checkpoint,evaluated.ClosedPeriods,false);
        }
        catch
        {
            try{await t.RollbackAsync(CancellationToken.None);}
            catch(Exception error)when(error is SqlException or InvalidOperationException){}
            throw;
        }
    }

    /// <summary>A bounded, exact-qualification projection page. This store-level read
    /// is not an operator authorization boundary: HTTPS callers must obtain
    /// machine/site permission under the shared security gate first.</summary>
    public async Task<ActivityPeriodPage> ReadPeriodPageAsync(
        ActivityQualificationIdentity identity,int take=50,
        ActivityPeriodCursor? after=null,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if(take is <1 or >100)throw new ArgumentOutOfRangeException(nameof(take));
        await using var c=new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            // The checkpoint is the authority for exact qualification, including
            // continuity identity and the complete canonical value-map digest.
            // Never return historical periods after a missing or changed checkpoint.
            ActivityQualificationIdentity persistedQualification;
            await using(var validation=new SqlCommand("""
                SELECT QualificationHash,CheckpointJson,CheckpointHash
                FROM activity.ProcessorCheckpoint WITH(HOLDLOCK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                """,c,t))
            {
                BindScope(validation,identity);
                await using var reader=await validation.ExecuteReaderAsync(ct);
                if(!await reader.ReadAsync(ct))
                    throw new InvalidDataException("Qualified activity checkpoint does not exist.");
                if(!((byte[])reader[0]).AsSpan().SequenceEqual(QualificationHash(identity)))
                    throw new InvalidDataException("Period qualification or continuity identity changed.");
                var json=reader.GetString(1);
                if(!SHA256.HashData(Encoding.Unicode.GetBytes(json)).AsSpan()
                    .SequenceEqual((byte[])reader[2]))
                    throw new InvalidDataException("Activity checkpoint integrity mismatch.");
                var actual=JsonSerializer.Deserialize<ActivityProcessorCheckpoint>(json)
                    ?? throw new InvalidDataException("Activity checkpoint cannot be restored.");
                if(actual.Qualification!=identity)
                    throw new InvalidDataException("Activity checkpoint does not match requested qualification.");
                persistedQualification=actual.Qualification;
            }
            var results=new List<ActivityPeriodRow>();
            await using(var q=new SqlCommand("""
                SELECT TOP (@limit) EffectId,QualificationHash,State,StartedAt,EndedAt,
                    DurationTicks,InstanceId,StartSequence,EndSequence,StartRawValue,EndRawValue
                FROM activity.ClosedPeriod
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                    AND QualificationHash=@qualification
                    AND (@afterTime IS NULL OR StartedAt>@afterTime
                        OR (StartedAt=@afterTime AND
                            (InstanceId>@afterInstance OR
                            (InstanceId=@afterInstance AND
                                (StartSequence>@afterStart OR
                                (StartSequence=@afterStart AND EffectId>@afterEffect))))))
                ORDER BY StartedAt,InstanceId,StartSequence,EffectId
                """,c,t))
            {
                BindScope(q,identity);
                q.Parameters.Add("@qualification",SqlDbType.Binary,32).Value=QualificationHash(identity);
                q.Parameters.Add("@limit",SqlDbType.Int).Value=take+1;
                q.Parameters.Add("@afterTime",SqlDbType.DateTimeOffset).Value=(object?)after?.StartedAt??DBNull.Value;
                var instance=q.Parameters.Add("@afterInstance",SqlDbType.Decimal);
                instance.Precision=20;
                instance.Scale=0;
                instance.Value=after is null?DBNull.Value:Convert.ToDecimal(after.InstanceId,CultureInfo.InvariantCulture);
                var start=q.Parameters.Add("@afterStart",SqlDbType.Decimal);
                start.Precision=20;
                start.Scale=0;
                start.Value=after is null?DBNull.Value:Convert.ToDecimal(after.StartSequence,CultureInfo.InvariantCulture);
                q.Parameters.Add("@afterEffect",SqlDbType.UniqueIdentifier).Value=(object?)after?.EffectId??DBNull.Value;
                await using var reader=await q.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                {
                    if(!((byte[])reader[1]).AsSpan().SequenceEqual(QualificationHash(identity)))
                        throw new InvalidDataException("Period row qualification differs from checkpoint.");
                    var cursor=new ActivityPeriodCursor(reader.GetDateTimeOffset(3),
                        decimal.ToUInt64(reader.GetDecimal(6)),
                        decimal.ToUInt64(reader.GetDecimal(7)),reader.GetGuid(0));
                    var period=new ClosedActivityPeriod(identity.MachineId,identity.StreamKey,
                        identity.MappingId,Enum.Parse<ActivityState>(reader.GetString(2)),
                        reader.GetDateTimeOffset(3),reader.GetDateTimeOffset(4),
                        reader.GetInt64(5),decimal.ToUInt64(reader.GetDecimal(6)),
                        decimal.ToUInt64(reader.GetDecimal(7)),
                        decimal.ToUInt64(reader.GetDecimal(8)),
                        reader.GetString(9),reader.GetString(10));
                    results.Add(new(period,persistedQualification,
                        Convert.ToHexString((byte[])reader[1]),cursor));
                }
            }
            await t.CommitAsync(ct);
            var more=results.Count>take;
            if(more)results.RemoveAt(results.Count-1);
            return new(results.AsReadOnly(),more?results[^1].Cursor:null);
        }
        catch
        {
            try{await t.RollbackAsync(CancellationToken.None);}
            catch(Exception error)when(error is SqlException or InvalidOperationException){}
            throw;
        }
    }

    // Compatibility for the existing tiny SQL acceptance fixture only. No
    // public unbounded read exists: callers must page explicitly.
    public async Task<IReadOnlyList<ClosedActivityPeriod>> ReadPeriodsAsync(
        ActivityQualificationIdentity identity,CancellationToken ct=default)
    {
        var page=await ReadPeriodPageAsync(identity,100,null,ct);
        if(page.Next is not null)
            throw new InvalidOperationException("Use explicit activity period pagination.");
        return page.Rows.Select(row=>row.Period).ToArray();
    }

    private static byte[] QualificationHash(ActivityQualificationIdentity identity)
        => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity));

    private static Guid PeriodEffect(ActivityQualificationIdentity identity,ClosedActivityPeriod period)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
            identity,period.InstanceId,period.StartSequence,period.EndSequence
        });
        return new Guid(SHA256.HashData(bytes).AsSpan(0,16));
    }

    private static void BindScope(SqlCommand q,ActivityQualificationIdentity identity)
    {
        q.Parameters.Add("@machine",SqlDbType.UniqueIdentifier).Value=identity.MachineId;
        q.Parameters.Add("@stream",SqlDbType.VarBinary,512).Value=Utf8.GetBytes(identity.StreamKey);
        q.Parameters.Add("@mapping",SqlDbType.UniqueIdentifier).Value=identity.MappingId;
    }
}
