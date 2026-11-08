using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

public sealed partial class SqlActivityProcessorStore
{
    internal async Task<ActivityDurableResult> ApplyPublicationWorkAsync(ActivityWorkCandidate work,bool injectRollback,CancellationToken ct=default)
    {
        var scope=work.Scope;
        if(scope.PublicationProfile!=ActivityPublicationPolicy.CrossBatch || work.OperationId==Guid.Empty || work.AfterSequence>=work.ThroughSequence ||
            work.ThroughSequence==ulong.MaxValue)throw new ArgumentException("Explicit advancing cross-batch publication work required.");
        var approved=await new SqlApprovedActivityMappingResolver(connectionString,scope.InstallationId)
            .ResolveAsync(scope.SiteId,scope.MachineId,scope.MappingId,scope.StreamKey,ct);
        var identity=AcceptedActivityContinuityQualifier.QualificationFor(approved,ActivityPublicationPolicy.CrossBatch);
        if(identity!=work.Qualification)throw new InvalidDataException("Publication work policy or mapping changed.");
        var cut=await new SqlAcceptedActivityEvidenceReader(connectionString).ReadVerifiedCutAsync(scope.SiteId,identity,
            scope.InstanceId,work.AfterSequence,work.ThroughSequence,100,ct);
        if((cut.Rows.Count==0)!=work.IsEmpty)throw new InvalidDataException("Publication work cannot omit or fabricate observations.");
        if(cut.Rows.Count>0)ValidateInterpretationPeriod(approved,cut.Rows);
        var samples=cut.Rows.Count==0?Array.Empty<ActivityInputSample>():AcceptedActivityContinuityQualifier.Qualify(approved,cut.Rows).Samples.ToArray();
        var binding=new AcceptedSourceBinding(scope.SiteId,scope.InstallationId,approved,cut.Rows,scope.InstanceId,work.AfterSequence,
            work.ThroughSequence,SourceProof(cut.Rows),work.IsEmpty?cut.Batches:null,ActivityPublicationPolicy.CrossBatch,cut.Batches);
        if(work.IsEmpty && (cut.Batches.Count==0 || cut.Batches.Max(x=>x.NextSequence)!=work.ThroughSequence+1))
            throw new InvalidDataException("No-state publication must end at its verified batch frontier.");
        return await ApplyCoreAsync(work.OperationId,approved.Signal,identity,work.ExpectedGeneration,samples,binding,injectRollback,ct);
    }

    private sealed record PublicationBatch(ActivityBoundaryBatch Evidence,MtConnectSampleFrame Frame,Guid? Parent,ulong? ParentInstance);
    private sealed record PublicationParent(ActivityProcessorCheckpoint Checkpoint,string? RequestHash);

    private static async Task<PublicationParent> LoadPublicationParentAsync(SqlConnection c,SqlTransaction t,
        ActivityQualificationIdentity identity,ulong generation,CancellationToken ct)
    {
        if(generation==0)return new(ActivityCheckpointEvaluator.Initial(identity),null);
        await using var q=new SqlCommand("""
            IF EXISTS(SELECT 1 FROM activity.ProcessorOperation WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping
                AND AppliedGeneration=@generation AND (DATALENGTH(ResultBytes)>8388608 OR DATALENGTH(RequestBytes)>8388608))
                THROW 51095,'Publication ancestry exceeds byte bound.',1;
            SELECT OperationId,RequestBytes,RequestHash,ResultBytes,ResultHash FROM activity.ProcessorOperation WITH(HOLDLOCK,INDEX(IX_ActivityOperation_Generation),FORCESEEK)
            WHERE MachineId=@machine AND StreamKeyBytes=@stream AND MappingId=@mapping AND AppliedGeneration=@generation;
            """,c,t);
        BindScope(q,identity);q.Parameters.AddWithValue("@generation",checked((long)generation));
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))throw new InvalidDataException("Publication predecessor receipt is missing.");
        var bytes=(byte[])r[1];var hash=(byte[])r[2];
        if(!SHA256.HashData(bytes).AsSpan().SequenceEqual(hash))throw new InvalidDataException("Publication predecessor request hash differs.");
        if(r.IsDBNull(4) || !SHA256.HashData((byte[])r[3]).AsSpan().SequenceEqual((byte[])r[4]))
            throw new InvalidDataException("Publication predecessor result hash differs or is unqualified.");
        var saved=JsonSerializer.Deserialize<ActivityDurableResult>((byte[])r[3]);
        if(saved is null || saved.Replayed || saved.Checkpoint.Qualification!=identity || saved.Checkpoint.NextGeneration!=generation ||
            saved.Checkpoint.AcceptedSource?.OperationId!=r.GetGuid(0) || saved.Checkpoint.AcceptedSource.PublicationAncestryJson is null)
            throw new InvalidDataException("Publication predecessor policy or ancestry differs; legacy adoption is forbidden.");
        using var request=JsonDocument.Parse(bytes);
        if(request.RootElement.GetProperty("version").GetInt32()!=4 || request.RootElement.GetProperty("profile").GetString()!=ActivityPublicationPolicy.CrossBatch ||
            request.RootElement.GetProperty("operationId").GetGuid()!=r.GetGuid(0) ||
            request.RootElement.GetProperty("identity").Deserialize<ActivityQualificationIdentity>()!=identity ||
            request.RootElement.GetProperty("expectedGeneration").GetUInt64()!=generation-1 ||
            request.RootElement.GetProperty("acceptedThrough").GetUInt64()!=saved.Checkpoint.AcceptedSource.ThroughSequence ||
            request.RootElement.GetProperty("acceptedInstance").GetUInt64()!=saved.Checkpoint.AcceptedSource.InstanceId ||
            request.RootElement.GetProperty("publicationProofSha256").GetString()!=saved.Checkpoint.AcceptedSource.PublicationProofSha256 ||
            request.RootElement.GetProperty("publicationProof").GetString()!=saved.Checkpoint.AcceptedSource.PublicationAncestryJson)
            throw new InvalidDataException("Publication predecessor does not bind its proof.");
        var operation=r.GetGuid(0);var resultHash=(byte[])r[4];await r.DisposeAsync();
        await using(var edge=new SqlCommand("SELECT 1 FROM activity.PublicationReceiptAncestry WITH(HOLDLOCK) WHERE OperationId=@op AND RequestHash=@hash AND ResultHash=@result",c,t))
        {
            edge.Parameters.AddWithValue("@op",operation);edge.Parameters.Add("@hash",SqlDbType.Binary,32).Value=hash;
            edge.Parameters.Add("@result",SqlDbType.Binary,32).Value=resultHash;
            if(await edge.ExecuteScalarAsync(ct) is null)throw new InvalidDataException("Publication predecessor ancestry link is missing.");
        }
        return new(saved.Checkpoint,Convert.ToHexString(hash));
    }

    private static async Task<Dictionary<Guid,PublicationBatch>> LoadPublicationBatchesAsync(SqlConnection c,SqlTransaction t,
        ActivityQualificationIdentity identity,AcceptedSourceBinding binding,IEnumerable<Guid> requested,CancellationToken ct)
    {
        var ids=requested.Distinct().Order().ToArray();
        if(ids.Length is <1 or >202 || ids.Contains(Guid.Empty))throw new InvalidDataException("Bounded exact publication batches required.");
        await using var q=new SqlCommand("""
            DECLARE @ids TABLE(BatchId uniqueidentifier PRIMARY KEY);
            INSERT @ids SELECT CONVERT(uniqueidentifier,value) FROM OPENJSON(@idsJson);
            IF (SELECT COUNT(*) FROM @ids)<>@count OR COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(b.RawDocument))+DATALENGTH(s.RequestBytes)+512)
                FROM @ids i INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK) ON b.BatchId=i.BatchId
                INNER LOOP JOIN security.SecurityOperation s WITH(HOLDLOCK,FORCESEEK) ON s.OperationId=b.BatchId),0)>8388608
                THROW 51095,'Publication source bytes exceed bound.',1;
            SELECT b.BatchId,b.AcknowledgementId,b.InstanceId,b.FromSequence,b.NextSequence,b.FirstAvailableSequence,
                b.DocumentHash,b.RawDocument,b.RuntimeIdentityId,s.RequestBytes,s.RequestHash
            FROM @ids i INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK) ON b.BatchId=i.BatchId
            INNER LOOP JOIN security.SecurityOperation s WITH(HOLDLOCK,FORCESEEK) ON s.OperationId=b.BatchId
            WHERE b.SiteId=@site AND b.MachineId=@machine AND b.StreamKeyBytes=@stream AND b.InstanceId=@instance AND b.AcknowledgementId=@ack
                AND s.ActorPrincipalId=b.RuntimeIdentityId AND s.ScopeKind='M' AND s.SiteId=@site AND s.MachineId=@machine
                AND DATALENGTH(b.RawDocument)<=1048576 AND DATALENGTH(s.RequestBytes)<=16384;
            """,c,t);
        BindScope(q,identity);q.Parameters.AddWithValue("@site",binding.SiteId);q.Parameters.AddWithValue("@ack",binding.Approved.AcknowledgementId);
        WorkerNumber(q,"@instance",binding.InstanceId);q.Parameters.AddWithValue("@count",ids.Length);
        q.Parameters.Add("@idsJson",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(ids);
        var materialized=new List<ActivityBoundaryBatch>(ids.Length);
        await using(var r=await q.ExecuteReaderAsync(ct))
        {while(await r.ReadAsync(ct))materialized.Add(new(r.GetGuid(8),new(r.GetGuid(0),r.GetGuid(1),decimal.ToUInt64(r.GetDecimal(2)),
            decimal.ToUInt64(r.GetDecimal(3)),decimal.ToUInt64(r.GetDecimal(4)),decimal.ToUInt64(r.GetDecimal(5)),Convert.ToHexString((byte[])r[6])),
            (byte[])r[7],(byte[])r[9],(byte[])r[10]));}
        if(materialized.Count!=ids.Length)throw new InvalidDataException("Retained publication document or immutable ingestion receipt is missing.");
        return materialized.ToDictionary(x=>x.Header.BatchId,x=>{
            var verified=AcceptedActivityBoundaryVerifier.VerifyReceipt(binding.SiteId,identity.MachineId,identity.StreamKey,x);
            return new PublicationBatch(x,verified.Frame,verified.Predecessor,verified.Instance);
        });
    }

    private static ActivityPublicationAncestry ParsePublicationAncestry(ActivityProcessorCheckpoint cp)
    {
        var json=cp.AcceptedSource?.PublicationAncestryJson??throw new InvalidDataException("Publication proof ancestry is missing.");
        if(cp.AcceptedSource.PublicationProofSha256!=Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(json))))
            throw new InvalidDataException("Publication proof identity differs from persisted ancestry.");
        var proof=JsonSerializer.Deserialize<ActivityPublicationAncestry>(json)??throw new InvalidDataException("Publication proof ancestry is malformed.");
        if(proof.Profile!=ActivityPublicationPolicy.CrossBatch || proof.Batches is not {Length:>0 and <=100} ||
            proof.Batches.Select(x=>x.BatchId).Distinct().Count()!=proof.Batches.Length ||
            (cp.OpenAnchor is null)!=(proof.AnchorWitness is null) ||
            proof.AnchorWitness is {} anchor && (cp.AcceptedSource?.OpenAnchorObservation?.BatchId!=anchor.BatchId ||
                cp.AcceptedSource.OpenAnchorObservation.DocumentSha256!=anchor.DocumentSha256) ||
            (cp.NextGeneration==1)!=(proof.ParentOperationId is null))
            throw new InvalidDataException("Publication proof policy or predecessor differs.");
        return proof;
    }

    private static async Task VerifyPublicationCheckpointAsync(SqlConnection c,SqlTransaction t,ActivityQualificationIdentity identity,
        ActivityProcessorCheckpoint cp,AcceptedSourceBinding binding,CancellationToken ct)
    {
        var proof=ParsePublicationAncestry(cp);
        await using(var edge=new SqlCommand("""
            SELECT 1 FROM activity.PublicationReceiptAncestry a WITH(HOLDLOCK)
            JOIN activity.ProcessorOperation o WITH(HOLDLOCK) ON o.OperationId=a.OperationId
            WHERE a.OperationId=@op AND a.MachineId=@machine AND a.StreamKeyBytes=@stream AND a.MappingId=@mapping AND a.Generation=@generation
                AND a.RequestHash=o.RequestHash AND a.ResultHash=o.ResultHash AND a.ProofHash=@proof;
            """,c,t))
        {
            BindScope(edge,identity);edge.Parameters.AddWithValue("@op",cp.AcceptedSource!.OperationId);
            edge.Parameters.AddWithValue("@generation",checked((long)cp.NextGeneration));
            edge.Parameters.Add("@proof",SqlDbType.Binary,32).Value=Convert.FromHexString(cp.AcceptedSource.PublicationProofSha256!);
            if(await edge.ExecuteScalarAsync(ct) is null)throw new InvalidDataException("Publication checkpoint lacks database-enforced complete receipt ancestry.");
        }

        var self=await LoadPublicationParentAsync(c,t,identity,cp.NextGeneration,ct);
        if(self.Checkpoint!=cp)throw new InvalidDataException("Publication checkpoint differs from its proof-bearing receipt.");
        if(cp.NextGeneration>1)
        {
            var parent=await LoadPublicationParentAsync(c,t,identity,cp.NextGeneration-1,ct);
            if(parent.Checkpoint.AcceptedSource?.OperationId!=proof.ParentOperationId || parent.RequestHash!=proof.ParentRequestSha256 ||
                Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(parent.Checkpoint.AcceptedSource!.PublicationAncestryJson!)))!=proof.ParentProofSha256)
                throw new InvalidDataException("Publication proof predecessor hash differs.");
        }
        else if(proof.ParentRequestSha256 is not null || proof.ParentProofSha256 is not null)
            throw new InvalidDataException("Initial publication proof cannot invent a parent.");
        var extra=new[]{cp.AcceptedSource?.LastObservation?.BatchId,cp.AcceptedSource?.OpenAnchorObservation?.BatchId}.OfType<Guid>();
        var batches=await LoadPublicationBatchesAsync(c,t,identity,binding,proof.Batches.Select(x=>x.BatchId).Concat(extra),ct);
        foreach(var witness in proof.Batches)
        {
            var b=batches[witness.BatchId].Evidence;
            if(b.Header.DocumentSha256!=witness.DocumentSha256 || Convert.ToHexString(b.RequestHash)!=witness.RequestSha256)
                throw new InvalidDataException("Persisted publication document or receipt witness changed.");
        }
        if(proof.AnchorWitness is {} anchorWitness)
        {
            var anchor=batches[anchorWitness.BatchId].Evidence;
            if(anchor.Header.DocumentSha256!=anchorWitness.DocumentSha256 || Convert.ToHexString(anchor.RequestHash)!=anchorWitness.RequestSha256)
                throw new InvalidDataException("Original open-anchor ingestion receipt witness changed.");
        }
    }

    private static async Task<(string Proof,ActivityInputSample[] Samples)> BuildPublicationProofAsync(SqlConnection c,SqlTransaction t,
        ActivityQualificationIdentity identity,AcceptedSourceBinding binding,PublicationParent parent,ActivityInputSample[] samples,CancellationToken ct)
    {
        var cut=binding.PublicationBatches??binding.EmptyBatches??throw new InvalidDataException("Complete publication cut proof required.");
        if(cut.Count is <1 or >100)throw new InvalidDataException("Bounded publication cut proof required.");
        await using(var q=new SqlCommand("""
            DECLARE @expected TABLE(BatchId uniqueidentifier PRIMARY KEY);
            INSERT @expected SELECT CONVERT(uniqueidentifier,value) FROM OPENJSON(@ids);
            DECLARE @actual TABLE(BatchId uniqueidentifier PRIMARY KEY);
            INSERT @actual SELECT TOP(101) BatchId FROM acquisition.MtConnectBatch WITH(HOLDLOCK,INDEX(IX_MtConnectBatch_ActivityWindow),FORCESEEK)
                WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND FromSequence<=@through AND NextSequence>@after+1;
            IF EXISTS(SELECT BatchId FROM @expected EXCEPT SELECT BatchId FROM @actual) OR
                EXISTS(SELECT BatchId FROM @actual EXCEPT SELECT BatchId FROM @expected)
                THROW 51095,'Publication source cut batch set changed.',1;
            """,c,t))
        {
            BindScope(q,identity);q.Parameters.AddWithValue("@site",binding.SiteId);WorkerNumber(q,"@instance",binding.InstanceId);
            WorkerNumber(q,"@after",binding.AfterSequence);WorkerNumber(q,"@through",binding.ThroughSequence);
            q.Parameters.Add("@ids",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(cut.Select(x=>x.BatchId));await q.ExecuteNonQueryAsync(ct);
        }
        var source=parent.Checkpoint.AcceptedSource;
        var parentBatch=source?.FrontierBatch?.BatchId??source?.LastObservation?.BatchId;
        var ids=cut.Select(x=>x.BatchId).Concat(parentBatch is {} p?new[]{p}:Array.Empty<Guid>())
            .Concat(source?.OpenAnchorObservation is {} oldAnchor?new[]{oldAnchor.BatchId}:Array.Empty<Guid>());
        var batches=await LoadPublicationBatchesAsync(c,t,identity,binding,ids,ct);
        foreach(var pin in cut)if(batches[pin.BatchId].Evidence.Header!=pin)throw new InvalidDataException("Preflight source batch changed before publication.");
        PublicationBatch? previousBatch=parentBatch is {} priorId?batches[priorId]:null;
        var covered=checked(binding.AfterSequence+1);
        foreach(var b in cut.OrderBy(x=>x.FromSequence).ThenBy(x=>x.NextSequence))
        {
            var current=batches[b.BatchId];
            if(b.FromSequence>covered || b.NextSequence<=covered || b.FirstAvailableSequence>b.FromSequence)
                throw new InvalidDataException("Publication source cut has a gap or overlap.");
            if(previousBatch is not null && previousBatch.Evidence.Header.BatchId!=b.BatchId &&
                (b.FromSequence!=previousBatch.Evidence.Header.NextSequence || current.Parent!=previousBatch.Evidence.Header.BatchId ||
                    current.ParentInstance!=binding.InstanceId || current.Evidence.RuntimeIdentityId!=previousBatch.Evidence.RuntimeIdentityId))
                throw new InvalidDataException("Publication cut does not continue exact retained ingestion ancestry.");
            covered=b.NextSequence;previousBatch=current;
        }
        if(covered<=binding.ThroughSequence)throw new InvalidDataException("Publication cut coverage is incomplete.");
        var expected=cut.SelectMany(x=>batches[x.BatchId].Frame.Observations)
            .Where(x=>x.DataItemId==identity.SourceAddress && x.Sequence>binding.AfterSequence && x.Sequence<=binding.ThroughSequence)
            .Select(x=>x.Sequence).Order().ToArray();
        if(!expected.SequenceEqual(binding.Rows.Select(x=>x.Sequence)))throw new InvalidDataException("Publication cut omits or duplicates source observations.");
        var result=samples.ToArray();var prior=source?.LastObservation;
        for(var i=0;i<binding.Rows.Count;i++)
        {
            var row=binding.Rows[i];var link=false;
            if(prior is not null)
            {
                if(prior.BatchId==row.BatchId)link=prior.DocumentSha256==row.DocumentSha256;
                else if(batches.TryGetValue(prior.BatchId,out var left))
                {
                    var right=batches[row.BatchId];
                    link=right.Parent==prior.BatchId && right.ParentInstance==prior.InstanceId &&
                        left.Evidence.Header.NextSequence==right.Evidence.Header.FromSequence &&
                        left.Frame.Observations.Last(x=>x.DataItemId==identity.SourceAddress).Sequence==prior.Sequence &&
                        right.Frame.Observations.First(x=>x.DataItemId==identity.SourceAddress).Sequence==row.Sequence &&
                        row.ObservedAt>prior.ObservedAt;
                }
            }
            result[i]=result[i] with{ContinuityFromPrevious=link};prior=row;
        }
        var evaluated=ActivityCheckpointEvaluator.Advance(binding.Approved.Signal,identity,parent.Checkpoint,result);
        var open=binding.EmptyBatches is not null?null:evaluated.Checkpoint.OpenAnchor;
        var anchorRow=open is null?null:binding.Rows.FirstOrDefault(x=>x.Sequence==open.Sequence)??source?.OpenAnchorObservation;
        var anchorBatch=anchorRow is null?null:batches[anchorRow.BatchId].Evidence;
        var ancestry=new ActivityPublicationAncestry(ActivityPublicationPolicy.CrossBatch,source?.OperationId,parent.RequestHash,
            source?.PublicationAncestryJson is {} json?Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(json))):null,
            cut.OrderBy(x=>x.BatchId).Select(x=>new ActivityPublicationWitness(x.BatchId,x.DocumentSha256,
                Convert.ToHexString(batches[x.BatchId].Evidence.RequestHash))).ToArray(),
            anchorBatch is null?null:new(anchorBatch.Header.BatchId,anchorBatch.Header.DocumentSha256,Convert.ToHexString(anchorBatch.RequestHash)));
        return (JsonSerializer.Serialize(ancestry),result);
    }
}
