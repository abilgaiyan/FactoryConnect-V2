using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

public sealed record ActivityWorkerScope(Guid InstallationId,Guid SiteId,Guid MachineId,
    Guid MappingId,string StreamKey,ulong InstanceId,ulong BootstrapAfterSequence,string PublicationProfile=ActivityPublicationPolicy.Legacy);
public sealed record ActivityWorkCandidate(Guid OperationId,ActivityWorkerScope Scope,
    ActivityQualificationIdentity Qualification,ulong ExpectedGeneration,ulong AfterSequence,ulong ThroughSequence,bool IsEmpty=false);
public sealed record ActivityWorkDiscovery(ulong Generation,ulong AfterSequence,ulong? AcceptedThrough,
    ActivityWorkCandidate? Candidate);

public sealed partial class SqlActivityProcessorStore
{
    /// <summary>Discover a bounded candidate, never publication permission. ApplyAcceptedAsync
    /// independently revalidates authority, complete source cut and predecessor at commit.</summary>
    public async Task<ActivityWorkDiscovery> DiscoverAcceptedWorkAsync(ActivityWorkerScope scope,int take=100,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ActivityPublicationPolicy.Validate(scope.PublicationProfile);
        if(new[]{scope.InstallationId,scope.SiteId,scope.MachineId,scope.MappingId}.Any(x=>x==Guid.Empty) ||
            string.IsNullOrWhiteSpace(scope.StreamKey) || !scope.StreamKey.StartsWith("mtconnect:uuid:",StringComparison.Ordinal) || scope.StreamKey.Length=="mtconnect:uuid:".Length ||
            Utf8.GetByteCount(scope.StreamKey)>512 || scope.BootstrapAfterSequence==ulong.MaxValue || take is <1 or >100)
            throw new ArgumentException("Explicit bounded activity worker scope required.");
        using var diagnostic=Diagnostics.StartActivity("activity.work.discover",ActivityKind.Internal);
        await using var c=new SqlConnection(connectionString);
        if(diagnostic is not null)c.StatisticsEnabled=true;
        await c.OpenAsync(ct);if(diagnostic is not null)c.ResetStatistics();
        await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            await SqlApprovedActivityMappingResolver.LockSecurityAuthorityAsync(c,t,scope.InstallationId,ct);
            await using(var gates=new SqlCommand("""
                DECLARE @r int;
                EXEC @r=sys.sp_getapplock @Resource=@machineGate,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
                IF @r<0 THROW 51092,'Activity discovery machine gate unavailable.',1;
                EXEC @r=sys.sp_getapplock @Resource=@processorGate,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
                IF @r<0 THROW 51092,'Activity discovery processor gate unavailable.',1;
                """,c,t))
            {
                gates.Parameters.AddWithValue("@machineGate",ConfigurationLockResources.Machine(scope.InstallationId,scope.MachineId));
                gates.Parameters.AddWithValue("@processorGate",ProcessorLockResource(scope.MachineId,scope.MappingId,scope.StreamKey));
                await gates.ExecuteNonQueryAsync(ct);
            }
            var approved=await new SqlApprovedActivityMappingResolver(connectionString,scope.InstallationId)
                .ResolveInTransactionAsync(c,t,scope.SiteId,scope.MachineId,scope.MappingId,scope.StreamKey,ct);
            var identity=AcceptedActivityContinuityQualifier.QualificationFor(approved,scope.PublicationProfile);
            var checkpoint=ActivityCheckpointEvaluator.Initial(identity);
            await using(var q=new SqlCommand("""
                IF EXISTS(SELECT 1 FROM activity.ProcessorCheckpoint WITH(HOLDLOCK)
                    WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                    AND DATALENGTH(CheckpointJson)>8388608) THROW 51092,'Activity discovery checkpoint exceeds byte bound.',1;
                SELECT CheckpointJson,CheckpointHash,QualificationHash,Generation
                FROM activity.ProcessorCheckpoint WITH(HOLDLOCK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping;
                """,c,t))
            {
                BindScope(q,identity);
                await using var r=await q.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    var json=r.GetString(0);
                    if(!SHA256.HashData(Encoding.Unicode.GetBytes(json)).AsSpan().SequenceEqual((byte[])r[1]) ||
                        !QualificationHash(identity).AsSpan().SequenceEqual((byte[])r[2]))
                        throw new InvalidDataException("Worker checkpoint checksum or qualification changed.");
                    checkpoint=JsonSerializer.Deserialize<ActivityProcessorCheckpoint>(json)??throw new InvalidDataException("Missing worker checkpoint.");
                    if(checkpoint.Qualification!=identity || checkpoint.NextGeneration!=(ulong)r.GetInt64(3) ||
                        checkpoint.NextGeneration==0 || checkpoint.AcceptedSource is null)
                        throw new InvalidDataException("Worker checkpoint lacks accepted ancestry.");
                }
            }
            await using(var ledger=new SqlCommand("SELECT TOP(1) AppliedGeneration,OperationId FROM activity.ProcessorOperation WITH(HOLDLOCK,INDEX(IX_ActivityOperation_Generation),FORCESEEK) WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping ORDER BY AppliedGeneration DESC",c,t))
            {
                BindScope(ledger,identity);await using var r=await ledger.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    if(checkpoint.NextGeneration!=(ulong)r.GetInt64(0) || checkpoint.AcceptedSource?.OperationId!=r.GetGuid(1))
                        throw new InvalidDataException("Worker checkpoint does not name the latest immutable operation generation.");
                }
                else if(checkpoint.NextGeneration!=0)throw new InvalidDataException("Worker checkpoint operation ledger is missing.");
            }
            var after=checkpoint.AcceptedSource?.ThroughSequence??scope.BootstrapAfterSequence;
            ulong? acceptedThrough=null;
            await using(var cursor=new SqlCommand("""
                SELECT InstanceId,NextSequence FROM acquisition.MtConnectCursor WITH(HOLDLOCK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND SiteId=@site;
                """,c,t))
            {
                BindScope(cursor,identity);cursor.Parameters.AddWithValue("@site",scope.SiteId);
                await using var r=await cursor.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    var next=decimal.ToUInt64(r.GetDecimal(1));
                    if(decimal.ToUInt64(r.GetDecimal(0))!=scope.InstanceId || next==0 || after>=next)
                        throw new InvalidDataException("Worker source instance/frontier requires explicit recovery.");
                    acceptedThrough=next-1;
                }
                else if(checkpoint.AcceptedSource is not null)throw new InvalidDataException("Worker accepted cursor is missing.");
            }
            if(acceptedThrough is null)
            { await t.CommitAsync(ct);return new(checkpoint.NextGeneration,after,null,null); }
            if(checkpoint.AcceptedSource is { } predecessor)
            {
                await using(var budget=new SqlCommand("IF EXISTS(SELECT 1 FROM activity.ProcessorOperation WHERE OperationId=@op AND DATALENGTH(ResultBytes)>8388608) THROW 51092,'Worker ancestry exceeds byte bound.',1;",c,t))
                {budget.Parameters.AddWithValue("@op",predecessor.OperationId);await budget.ExecuteNonQueryAsync(ct);}
                await VerifyPredecessorAsync(c,t,identity,checkpoint,new(scope.SiteId,scope.InstallationId,approved,
                    Array.Empty<SqlAcceptedActivityEvidenceReader.AcceptedRow>(),scope.InstanceId,after,after,"[]",PublicationProfile:scope.PublicationProfile),ct);
            }
            else
            {
                // Check bootstrap even for an empty poll: no caller-selected later start.
                await VerifyPredecessorAsync(c,t,identity,checkpoint,new(scope.SiteId,scope.InstallationId,approved,
                    Array.Empty<SqlAcceptedActivityEvidenceReader.AcceptedRow>(),scope.InstanceId,after,after,"[]",PublicationProfile:scope.PublicationProfile),ct);
            }
            if(after==acceptedThrough.Value)
            {await t.CommitAsync(ct);return new(checkpoint.NextGeneration,after,acceptedThrough,null);}
            ulong? windowThrough=null;long windowBytes=0;
            await using(var window=new SqlCommand("""
                DECLARE @selected TABLE(BatchId uniqueidentifier PRIMARY KEY,NextSequence decimal(20,0));
                INSERT @selected SELECT TOP(100) BatchId,NextSequence FROM acquisition.MtConnectBatch WITH(HOLDLOCK,INDEX(IX_MtConnectBatch_ActivityWindow),FORCESEEK)
                    WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance
                        AND NextSequence>@after+1 AND FromSequence<=@through ORDER BY NextSequence,BatchId;
                SELECT s.NextSequence,CONVERT(bigint,DATALENGTH(b.RawDocument))+512 FROM @selected s
                    INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK) ON b.BatchId=s.BatchId
                    ORDER BY s.NextSequence,s.BatchId;
                """,c,t))
            {
                BindScope(window,identity);window.Parameters.AddWithValue("@site",scope.SiteId);
                WorkerNumber(window,"@instance",scope.InstanceId);WorkerNumber(window,"@after",after);WorkerNumber(window,"@through",acceptedThrough.Value);
                await using var r=await window.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                {
                    var next=decimal.ToUInt64(r.GetDecimal(0));
                    if(next==0 || next-1>acceptedThrough.Value)throw new InvalidDataException("Accepted batch exceeds the durable cursor.");
                    if(windowBytes+r.GetInt64(1)>4L*1024*1024)break;
                    windowBytes+=r.GetInt64(1);windowThrough=next-1;
                }
            }
            if(windowThrough is null || windowThrough<=after)throw new InvalidDataException("No bounded retained batch covers pending source.");
            var sequences=new List<ulong>(take);
            await using(var q=new SqlCommand("""
                SELECT TOP (@take) Sequence FROM acquisition.MtConnectObservation WITH(HOLDLOCK,INDEX(IX_MtConnectObservation_ActivityNext),FORCESEEK)
                WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                    AND InstanceId=@instance AND Sequence>@after AND Sequence<=@through
                ORDER BY Sequence;
                """,c,t))
            {
                BindScope(q,identity);q.Parameters.AddWithValue("@take",take);
                WorkerNumber(q,"@instance",scope.InstanceId);WorkerNumber(q,"@after",after);WorkerNumber(q,"@through",windowThrough.Value);
                await using var r=await q.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))sequences.Add(decimal.ToUInt64(r.GetDecimal(0)));
            }
            var through=sequences.Count>0?sequences[^1]:windowThrough.Value;
            var empty=sequences.Count==0;
            var op=new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                contract="fc2-activity-worker-cut-v2",scope.InstallationId,scope.SiteId,identity,
                scope.InstanceId,after,through,generation=checkpoint.NextGeneration,empty
            }))[..16]);
            var candidate=new ActivityWorkCandidate(op,scope,identity,checkpoint.NextGeneration,after,through,empty);
            await t.CommitAsync(ct);
            return new(checkpoint.NextGeneration,after,acceptedThrough,candidate);
        }
        catch
        {
            try{await t.RollbackAsync(CancellationToken.None);}catch(Exception e)when(e is SqlException or InvalidOperationException){}
            throw;
        }
        finally
        {
            if(diagnostic is not null && c.State==ConnectionState.Open)
                diagnostic.SetTag("activity.discovery.sql.roundtrips",c.RetrieveStatistics()["ServerRoundtrips"]);
        }
    }
    private static void WorkerNumber(SqlCommand q,string name,ulong value)
    {var p=q.Parameters.Add(name,SqlDbType.Decimal);p.Precision=20;p.Scale=0;p.Value=(decimal)value;}

}
