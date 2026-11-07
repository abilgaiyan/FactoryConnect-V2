using System.Data;
using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
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
    private static readonly ActivitySource Diagnostics=new("FactoryConnect.V2.Activity");

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

    // Existing typed-sample entry point remains for disposable durability
    // fixtures. It is not an approved production worker entry point.
    internal Task<ActivityDurableResult> ApplyAsync(
        Guid operationId,QualifiedActivitySignal signal,ActivityQualificationIdentity identity,
        ulong expectedGeneration,IReadOnlyList<ActivityInputSample> samples,
        bool injectRollback=false,CancellationToken ct=default)
        => ApplyCoreAsync(operationId,signal,identity,expectedGeneration,samples,
            null,injectRollback,ct);

    private sealed record AcceptedSourceBinding(
        Guid SiteId,Guid InstallationId,ApprovedActivitySignal Approved,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> Rows,
        ulong InstanceId,ulong AfterSequence,ulong ThroughSequence,string ProofJson);

    /// <summary>
    /// Evidence-bound write candidate. Rechecks the active approved mapping and
    /// the *entire* selected accepted source cut under the checkpoint's serializable
    /// transaction, before receipt/checkpoint/period insertion. Cross-batch and
    /// restart continuity remain conservatively fenced until broader qualification.
    /// </summary>
    public async Task<ActivityDurableResult> ApplyAcceptedAsync(
        Guid operationId,Guid installationId,Guid siteId,Guid machineId,
        Guid mappingId,string streamKey,ulong instanceId,ulong afterSequence,
        ulong throughSequence,ulong expectedGeneration,
        bool injectRollback=false,CancellationToken ct=default)
    {
        if(installationId==Guid.Empty||siteId==Guid.Empty||machineId==Guid.Empty||
           mappingId==Guid.Empty||operationId==Guid.Empty||
           string.IsNullOrWhiteSpace(streamKey)||afterSequence>=throughSequence)
            throw new ArgumentException("Exact accepted evidence operation and frontier required.");
        var resolver=new SqlApprovedActivityMappingResolver(connectionString,installationId);
        var approved=await resolver.ResolveAsync(siteId,machineId,mappingId,streamKey,ct);
        var discovery=ActivityQualificationIdentity.From(
            approved.Signal,"accepted-observation-discovery-only");
        var rows=await new SqlAcceptedActivityEvidenceReader(connectionString)
            .ReadBoundedAsync(siteId,discovery,instanceId,afterSequence,throughSequence,100,ct);
        if(rows.Count==0)
            throw new InvalidDataException("No accepted activity observations within the requested source cut.");
        ValidateInterpretationPeriod(approved,rows);
        var cut=AcceptedActivityContinuityQualifier.Qualify(approved,rows);
        var proof=JsonSerializer.Serialize(rows.Select(row=>new {
            sequence=row.Sequence.ToString(CultureInfo.InvariantCulture),
            batch=row.BatchId,ack=row.AcknowledgementId,
            canonical=row.CanonicalSha256,document=row.DocumentSha256,
            value=row.RawValue,quality=row.Quality,timestamp=row.SourceTimestamp,
            observed=row.ObservedAt
        }));
        var binding=new AcceptedSourceBinding(siteId,installationId,approved,
            rows,instanceId,afterSequence,throughSequence,proof);
        return await ApplyCoreAsync(operationId,approved.Signal,cut.Qualification,
            expectedGeneration,cut.Samples,binding,injectRollback,ct);
    }

    private async Task<ActivityDurableResult> ApplyCoreAsync(
        Guid operationId,QualifiedActivitySignal signal,ActivityQualificationIdentity identity,
        ulong expectedGeneration,IReadOnlyList<ActivityInputSample> samples,
        AcceptedSourceBinding? accepted,bool injectRollback,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(samples);
        if(operationId==Guid.Empty || samples.Count is <1 or >1000 ||
           expectedGeneration>long.MaxValue-1 ||
           identity!=ActivityQualificationIdentity.From(signal,identity.ContinuityIdentity))
            throw new ArgumentException("Exact bounded activity operation required.");
        using var activity=Diagnostics.StartActivity("activity.sql.apply",ActivityKind.Internal);
        var started=Stopwatch.GetTimestamp();
        var insertElapsed=TimeSpan.Zero;
        var periodInsertCommands=0;
        var evaluatedPeriodRows=0;
        var savedSamples=samples.ToArray();
        var request=JsonSerializer.SerializeToUtf8Bytes(new {
            version=1,operationId,identity,expectedGeneration,samples=savedSamples,
            acceptedSourceProof=accepted?.ProofJson
        });
        var requestHash=SHA256.HashData(request);
        var resource=$"fc2:activity:{identity.MachineId:N}:{identity.MappingId:N}:{Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(identity.StreamKey)))}";
        await using var c=new SqlConnection(connectionString);
        if(activity is not null)c.StatisticsEnabled=true;
        await c.OpenAsync(ct);
        if(activity is not null)c.ResetStatistics();
        await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            if(accepted is not null)
            {
                // Join the existing authority revocation protocol before any
                // machine/processor lock. Authority writers hold this Exclusive
                // before their policy/machine gates; no policy lock is acquired
                // after the machine gate here.
                await SqlApprovedActivityMappingResolver.LockSecurityAuthorityAsync(
                    c,t,accepted.InstallationId,ct);
                // Then machine Shared, processor Exclusive, source/checkpoint rows.
                await using(var machineGate=new SqlCommand("""
                    DECLARE @r int;
                    EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Shared',
                        @LockOwner='Transaction',@LockTimeout=10000;
                    IF @r<0 THROW 51077,'Activity source authority machine gate unavailable.',1;
                    """,c,t))
                {
                    machineGate.Parameters.Add("@resource",SqlDbType.NVarChar,255).Value=
                        ConfigurationLockResources.Machine(
                            accepted.InstallationId,identity.MachineId);
                    await machineGate.ExecuteNonQueryAsync(ct);
                }
            }
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
            if(accepted is not null)
            {
                // Re-resolve on the *writer connection and transaction*, after
                // both potentially blocking gates. The SQL query obtains its
                // eligibility clock after the waits, and its locks are held
                // until the source proof and checkpoint/periods commit.
                var current=await new SqlApprovedActivityMappingResolver(
                    connectionString,accepted.InstallationId)
                    .ResolveInTransactionAsync(c,t,accepted.SiteId,
                        identity.MachineId,identity.MappingId,identity.StreamKey,ct);
                if(current.SiteId!=accepted.Approved.SiteId||
                   current.PackageRevisionId!=accepted.Approved.PackageRevisionId||
                   current.AcknowledgementId!=accepted.Approved.AcknowledgementId||
                   current.EffectiveFrom!=accepted.Approved.EffectiveFrom||
                   current.EffectiveTo!=accepted.Approved.EffectiveTo||
                   !string.Equals(current.PackageSha256,
                       accepted.Approved.PackageSha256,StringComparison.Ordinal)||
                   ActivityQualificationIdentity.From(
                       current.Signal,identity.ContinuityIdentity)!=identity)
                    throw new InvalidDataException(
                        "Approved activity mapping changed before durable source binding.");
                ValidateInterpretationPeriod(current,accepted.Rows);
                await VerifyAcceptedSourceInTransactionAsync(c,t,identity,accepted,ct);
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
            // One command per bounded page of derived periods, not one command
            // per transition. The transaction, operation receipt and immutable
            // effect IDs are unchanged. Each input batch admits at most 1000
            // samples, so payload size is bounded independently of history.
            evaluatedPeriodRows=evaluated.ClosedPeriods.Count;
            if(evaluated.ClosedPeriods.Count>0)
            {
                const int periodPageSize=100;
                for(var offset=0;offset<evaluated.ClosedPeriods.Count;offset+=periodPageSize)
                {
                    var page=evaluated.ClosedPeriods.Skip(offset).Take(periodPageSize)
                        .Select(period=>new {
                            effect=PeriodEffect(identity,period),
                            instance=period.InstanceId.ToString(CultureInfo.InvariantCulture),
                            start=period.StartSequence.ToString(CultureInfo.InvariantCulture),
                            end=period.EndSequence.ToString(CultureInfo.InvariantCulture),
                            state=period.State.ToString(),
                            started=period.StartedAt.ToString("O",CultureInfo.InvariantCulture),
                            ended=period.EndedAt.ToString("O",CultureInfo.InvariantCulture),
                            ticks=period.DurationTicks,
                            startValue=period.StartRawValue,endValue=period.EndRawValue
                        }).ToArray();
                    var payload=JsonSerializer.Serialize(page);
                    await using var insert=new SqlCommand("""
                        INSERT activity.ClosedPeriod(
                            EffectId,OperationId,MachineId,StreamKeyBytes,MappingId,
                            QualificationHash,InstanceId,StartSequence,EndSequence,
                            State,StartedAt,EndedAt,DurationTicks,StartRawValue,EndRawValue)
                        SELECT r.EffectId,@operation,@machine,@stream,@mapping,@qual,
                            r.InstanceId,r.StartSequence,r.EndSequence,r.State,
                            r.StartedAt,r.EndedAt,r.DurationTicks,r.StartRawValue,r.EndRawValue
                        FROM OPENJSON(@periods) WITH(
                            EffectId uniqueidentifier '$.effect',
                            InstanceId decimal(20,0) '$.instance',
                            StartSequence decimal(20,0) '$.start',
                            EndSequence decimal(20,0) '$.end',
                            State varchar(20) '$.state',
                            StartedAt datetimeoffset(7) '$.started',
                            EndedAt datetimeoffset(7) '$.ended',
                            DurationTicks bigint '$.ticks',
                            StartRawValue nvarchar(max) '$.startValue',
                            EndRawValue nvarchar(max) '$.endValue'
                        ) AS r;
                        """,c,t);
                    BindScope(insert,identity);
                    insert.Parameters.Add("@operation",SqlDbType.UniqueIdentifier).Value=operationId;
                    insert.Parameters.Add("@qual",SqlDbType.Binary,32).Value=oldHash;
                    insert.Parameters.Add("@periods",SqlDbType.NVarChar,-1).Value=payload;
                    var insertStarted=Stopwatch.GetTimestamp();
                    await insert.ExecuteNonQueryAsync(ct);
                    insertElapsed+=Stopwatch.GetElapsedTime(insertStarted);
                    periodInsertCommands++;
                }
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
        finally
        {
            if(activity is not null)
            {
                activity.SetTag("activity.sql.period.insert.commands",periodInsertCommands);
                activity.SetTag("activity.sql.period.insert.rows",evaluatedPeriodRows);
                activity.SetTag("activity.sql.period.insert.elapsed.ms",insertElapsed.TotalMilliseconds);
                activity.SetTag("activity.sql.apply.elapsed.ms",Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if(c.State==ConnectionState.Open)
                    activity.SetTag("activity.sql.roundtrips",c.RetrieveStatistics()["ServerRoundtrips"]);
            }
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

    private static void ValidateInterpretationPeriod(
        ApprovedActivitySignal mapping,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> observations)
    {
        // Strict live-only rule for FC2-034: historical pre-activation
        // interpretation requires a separately approved historical-revision
        // eligibility contract. Neither the current head nor a later
        // acknowledgement retroactively authorizes retained evidence.
        if(observations.Count is <1 or >100 ||
           mapping.EffectiveTo is { } to && to<=mapping.EffectiveFrom)
            throw new InvalidDataException("Missing or invalid activity interpretation interval.");
        foreach(var observation in observations)
        {
            if(observation.ObservedAt.Offset!=TimeSpan.Zero ||
               observation.ObservedAt<mapping.EffectiveFrom ||
               mapping.EffectiveTo is { } until && observation.ObservedAt>=until)
                throw new InvalidDataException(
                    "Accepted observation is outside the exact approved activity interpretation interval.");
        }
    }

    private static async Task VerifyAcceptedSourceInTransactionAsync(
        SqlConnection c,SqlTransaction t,ActivityQualificationIdentity identity,
        AcceptedSourceBinding binding,CancellationToken ct)
    {
        if(binding.Rows.Count is <1 or >100||
           identity.MachineId!=binding.Approved.Signal.MachineId||
           identity.MappingId!=binding.Approved.Signal.MappingId)
            throw new InvalidDataException("Accepted source binding cannot be empty or ambiguous.");
        await using var q=new SqlCommand("""
            DECLARE @expected TABLE(
                Sequence decimal(20,0) NOT NULL PRIMARY KEY,
                BatchId uniqueidentifier NOT NULL,
                Ack uniqueidentifier NOT NULL,
                Canonical binary(32) NOT NULL,
                Document binary(32) NOT NULL,
                RawValue nvarchar(max) NOT NULL,
                Quality varchar(32) NOT NULL,
                SourceTimestamp nvarchar(128) NOT NULL,
                ObservedAt datetimeoffset(7) NOT NULL
            );
            INSERT @expected(Sequence,BatchId,Ack,Canonical,Document,
                RawValue,Quality,SourceTimestamp,ObservedAt)
            SELECT j.Sequence,j.BatchId,j.Ack,CONVERT(binary(32),j.Canonical,2),
                CONVERT(binary(32),j.Document,2),j.RawValue,j.Quality,
                j.SourceTimestamp,j.ObservedAt
            FROM OPENJSON(@proof) WITH(
                Sequence decimal(20,0) '$.sequence',
                BatchId uniqueidentifier '$.batch',
                Ack uniqueidentifier '$.ack',
                Canonical varchar(64) '$.canonical',
                Document varchar(64) '$.document',
                RawValue nvarchar(max) '$.value',
                Quality varchar(32) '$.quality',
                SourceTimestamp nvarchar(128) '$.timestamp',
                ObservedAt datetimeoffset(7) '$.observed') j;
            IF (SELECT COUNT_BIG(*) FROM @expected)<>@count
                THROW 51078,'Changed activity source proof cardinality.',1;
            IF (SELECT COUNT_BIG(*) FROM acquisition.MtConnectObservation o WITH(HOLDLOCK)
                WHERE o.SiteId=@site AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND o.AddressBytes=@address AND o.InstanceId=@instance
                    AND o.Sequence>@after AND o.Sequence<=@through)<>@count
                THROW 51078,'Source cut changed before activity checkpoint commit.',1;
            IF EXISTS(
                SELECT 1 FROM @expected e
                LEFT JOIN acquisition.MtConnectObservation o WITH(HOLDLOCK)
                    ON o.SiteId=@site AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND o.AddressBytes=@address AND o.InstanceId=@instance
                    AND o.Sequence=e.Sequence
                LEFT JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK)
                    ON b.BatchId=o.BatchId AND b.SiteId=o.SiteId
                    AND b.MachineId=o.MachineId AND b.StreamKeyBytes=o.StreamKeyBytes
                WHERE o.MachineId IS NULL OR b.BatchId IS NULL
                    OR e.Sequence<=@after OR e.Sequence>@through
                    OR o.BatchId<>e.BatchId OR b.AcknowledgementId<>@ack
                    OR b.AcknowledgementId<>e.Ack
                    OR b.InstanceId<>@instance OR e.Sequence<b.FromSequence
                    OR e.Sequence>=b.NextSequence
                    OR o.ContentHash<>e.Canonical OR b.DocumentHash<>e.Document
                    OR o.ContentHash<>HASHBYTES('SHA2_256',o.CanonicalBytes)
                    OR CONVERT(varbinary(max),o.RawValue)<>
                        CONVERT(varbinary(max),e.RawValue)
                    OR CONVERT(varbinary(max),o.Quality)<>
                        CONVERT(varbinary(max),e.Quality)
                    OR CONVERT(varbinary(max),o.SourceTimestamp)<>
                        CONVERT(varbinary(max),e.SourceTimestamp)
                    OR o.ObservedAt<>e.ObservedAt
            ) THROW 51078,'Accepted observation no longer matches operation proof.',1;
            IF EXISTS(
                SELECT 1 FROM acquisition.MtConnectBatch b WITH(HOLDLOCK)
                JOIN (SELECT DISTINCT BatchId FROM @expected) distinctBatches
                    ON distinctBatches.BatchId=b.BatchId
                WHERE b.DocumentHash<>HASHBYTES('SHA2_256',b.RawDocument)
            ) THROW 51078,'Accepted source batch document integrity changed.',1;
            """,c,t);
        BindScope(q,identity);
        q.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=binding.SiteId;
        q.Parameters.Add("@address",SqlDbType.VarBinary,512).Value=
            Utf8.GetBytes(identity.SourceAddress);
        q.Parameters.Add("@ack",SqlDbType.UniqueIdentifier).Value=
            binding.Approved.AcknowledgementId;
        q.Parameters.Add("@proof",SqlDbType.NVarChar,-1).Value=binding.ProofJson;
        q.Parameters.Add("@count",SqlDbType.Int).Value=binding.Rows.Count;
        static void Number(SqlCommand command,string name,ulong value)
        {
            var parameter=command.Parameters.Add(name,SqlDbType.Decimal);
            parameter.Precision=20;
            parameter.Scale=0;
            parameter.Value=(decimal)value;
        }
        Number(q,"@instance",binding.InstanceId);
        Number(q,"@after",binding.AfterSequence);
        Number(q,"@through",binding.ThroughSequence);
        await q.ExecuteNonQueryAsync(ct);
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
