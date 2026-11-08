using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    private sealed record PublicationReadInput(Guid OperationId,ActivityQualificationIdentity Identity,byte[]? PreviouslyLoadedResult);
    private sealed record PublicationReadRow(byte[] Request,byte[] RequestHash,byte[] Result,byte[]? ResultHash,
        long? Generation,byte[]? ProofHash,Guid? ParentId,long? ParentGeneration,
        byte[]? ParentRequestHash,byte[]? ParentResultHash,byte[]? ParentProofHash);
    private sealed record PublicationWitness(Guid BatchId,byte[] DocumentHash,byte[] RequestHash);

    // One bounded SQL batch returns receipt/ancestry and witness result sets.
    private static async Task VerifyActivityPublicationReceiptsAsync(SqlConnection c,SqlTransaction t,
        IReadOnlyList<PublicationReadInput> items,CancellationToken ct)
    {
        if(items.Count==0)return;
        if(items.Count>101 || items.Select(x=>x.OperationId).Distinct().Count()!=items.Count ||
            items.Any(x=>x.OperationId==Guid.Empty || x.Identity.MachineId==Guid.Empty ||
                x.Identity.MappingId==Guid.Empty || x.PreviouslyLoadedResult?.Length>8388608))
            throw new InvalidDataException("Invalid bounded publication receipt selection.");
        var scope=items[0].Identity;
        if(items.Any(x=>x.Identity.MachineId!=scope.MachineId || x.Identity.StreamKey!=scope.StreamKey))
            throw new InvalidDataException("Publication receipts cross machine streams.");
        var selected=items.ToDictionary(x=>x.OperationId);
        await using var q=Command(c,t,"""
            DECLARE @selected TABLE(OperationId uniqueidentifier NOT NULL PRIMARY KEY,
                MappingId uniqueidentifier NOT NULL,ReuseResult bit NOT NULL);
            INSERT @selected(OperationId,MappingId,ReuseResult)
            SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.OperationId')),
                   TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.MappingId')),
                   CONVERT(bit,JSON_VALUE(value,'$.ReuseResult'))
            FROM OPENJSON(@ids);
            IF (SELECT COUNT(*) FROM @selected)<>@count
                THROW 51091,'Invalid publication receipt selection.',1;
            IF EXISTS(
                SELECT 1 FROM @selected s JOIN activity.ProcessorOperation o WITH(HOLDLOCK)
                    ON o.OperationId=s.OperationId AND o.MappingId=s.MappingId
                WHERE o.MachineId=@machine AND o.StreamKeyBytes=@stream AND
                    (DATALENGTH(o.RequestBytes)>8388608 OR DATALENGTH(o.ResultBytes)>8388608 OR
                     CONVERT(bigint,DATALENGTH(o.RequestBytes))+CONVERT(bigint,DATALENGTH(o.ResultBytes))>8388608))
                THROW 51091,'Activity publication receipt exceeds byte budget.',1;
            IF COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(o.RequestBytes))+
                    CONVERT(bigint,DATALENGTH(o.ResultBytes)))
                FROM @selected s JOIN activity.ProcessorOperation o WITH(HOLDLOCK)
                    ON o.OperationId=s.OperationId AND o.MappingId=s.MappingId
                WHERE o.MachineId=@machine AND o.StreamKeyBytes=@stream),0)>8388608
                THROW 51091,'Activity publication receipts exceed aggregate byte budget.',1;
            IF EXISTS(
                SELECT 1 FROM @selected s JOIN activity.PublicationSourceWitness w WITH(HOLDLOCK)
                    ON w.OperationId=s.OperationId AND w.MachineId=@machine AND w.StreamKeyBytes=@stream
                GROUP BY w.OperationId HAVING COUNT_BIG(*)>101)
                THROW 51091,'Activity publication per-operation witness count exceeds bound.',1;
            IF COALESCE((SELECT COUNT_BIG(*)
                FROM @selected s JOIN activity.PublicationSourceWitness w WITH(HOLDLOCK)
                    ON w.OperationId=s.OperationId AND w.MachineId=@machine AND w.StreamKeyBytes=@stream),0)>10302
                THROW 51091,'Activity publication witness count exceeds bound.',1;
            IF COALESCE((SELECT SUM(
                    CONVERT(bigint,DATALENGTH(w.DocumentHash))+
                    CONVERT(bigint,DATALENGTH(w.IngestionRequestHash)))
                FROM @selected s JOIN activity.PublicationSourceWitness w WITH(HOLDLOCK)
                    ON w.OperationId=s.OperationId AND w.MachineId=@machine AND w.StreamKeyBytes=@stream),0)>1048576
                THROW 51091,'Activity publication witness bytes exceed bound.',1;
            SELECT s.OperationId,o.RequestBytes,o.RequestHash,
                CASE WHEN s.ReuseResult=1 THEN CONVERT(varbinary(max),NULL) ELSE o.ResultBytes END,
                o.ResultHash,a.Generation,a.ProofHash,a.ParentOperationId,a.ParentGeneration,
                a.ParentRequestHash,a.ParentResultHash,a.ParentProofHash
            FROM @selected s JOIN activity.ProcessorOperation o WITH(HOLDLOCK)
                ON o.OperationId=s.OperationId AND o.MappingId=s.MappingId
                AND o.MachineId=@machine AND o.StreamKeyBytes=@stream
            LEFT JOIN activity.PublicationReceiptAncestry a WITH(HOLDLOCK)
                ON a.OperationId=o.OperationId AND a.MachineId=o.MachineId
                AND a.StreamKeyBytes=o.StreamKeyBytes AND a.MappingId=o.MappingId
                AND a.Generation=o.AppliedGeneration AND a.RequestHash=o.RequestHash AND a.ResultHash=o.ResultHash;
            SELECT w.OperationId,w.BatchId,w.DocumentHash,w.IngestionRequestHash
            FROM @selected s JOIN activity.PublicationSourceWitness w WITH(HOLDLOCK)
                ON w.OperationId=s.OperationId AND w.MachineId=@machine AND w.StreamKeyBytes=@stream
            ORDER BY w.OperationId,w.BatchId;
            """);
        q.Parameters.Add("@ids",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(
            items.Select(x=>new {x.OperationId,x.Identity.MappingId,ReuseResult=x.PreviouslyLoadedResult is not null}));
        Add(q,("@count",items.Count),("@machine",scope.MachineId),("@stream",ActivityUtf8.GetBytes(scope.StreamKey)));
        q.Parameters["@stream"].Size=512;
        var loaded=new Dictionary<Guid,PublicationReadRow>();
        var witnesses=new Dictionary<Guid,List<PublicationWitness>>();
        await using(var rd=await q.ExecuteReaderAsync(ct))
        {
            while(await rd.ReadAsync(ct))
            {
                var id=rd.GetGuid(0);
                if(!selected.TryGetValue(id,out var input))throw new InvalidDataException("Unexpected publication receipt.");
                var row=new PublicationReadRow((byte[])rd[1],(byte[])rd[2],
                    input.PreviouslyLoadedResult??(byte[])rd[3],rd.IsDBNull(4)?null:(byte[])rd[4],
                    rd.IsDBNull(5)?null:rd.GetInt64(5),rd.IsDBNull(6)?null:(byte[])rd[6],
                    rd.IsDBNull(7)?null:rd.GetGuid(7),rd.IsDBNull(8)?null:rd.GetInt64(8),
                    rd.IsDBNull(9)?null:(byte[])rd[9],rd.IsDBNull(10)?null:(byte[])rd[10],
                    rd.IsDBNull(11)?null:(byte[])rd[11]);
                if(!loaded.TryAdd(id,row))throw new InvalidDataException("Duplicate publication receipt.");
            }
            await rd.NextResultAsync(ct);
            while(await rd.ReadAsync(ct))
            {
                var id=rd.GetGuid(0);
                if(!selected.ContainsKey(id))throw new InvalidDataException("Unexpected publication witness.");
                if(!witnesses.TryGetValue(id,out var list))witnesses[id]=list=new();
                if(list.Count>=101)throw new InvalidDataException("Version-4 source witness bound exceeded.");
                list.Add(new(rd.GetGuid(1),(byte[])rd[2],(byte[])rd[3]));
            }
        }
        if(loaded.Count!=items.Count)throw new InvalidDataException("Activity publication receipt missing.");
        foreach(var input in items)
            VerifyActivityPublicationReceiptInMemory(input.OperationId,input.Identity,loaded[input.OperationId],
                witnesses.GetValueOrDefault(input.OperationId)??new List<PublicationWitness>());
    }

    // Read-side receipt guard. This verifies immutable publication identity and
    // witness bindings, not physical completeness or reconstructed source documents.
    private static async Task VerifyActivityPublicationReceiptAsync(SqlConnection c,SqlTransaction t,
        Guid operationId,ActivityQualificationIdentity identity,CancellationToken ct)
        => await VerifyActivityPublicationReceiptCoreAsync(c,t,operationId,identity,null,ct);

    private static async Task VerifyActivityPublicationReceiptCoreAsync(SqlConnection c,SqlTransaction t,
        Guid operationId,ActivityQualificationIdentity identity,byte[]? previouslyLoadedResult,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            IF EXISTS(
                SELECT 1 FROM activity.ProcessorOperation o WITH(HOLDLOCK)
                WHERE o.OperationId=@op AND o.MachineId=@machine
                    AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping
                    AND (DATALENGTH(o.RequestBytes)>8388608 OR DATALENGTH(o.ResultBytes)>8388608
                        OR CONVERT(bigint,DATALENGTH(o.RequestBytes))+CONVERT(bigint,DATALENGTH(o.ResultBytes))>8388608))
                THROW 51091,'Activity publication receipt exceeds byte budget.',1;
            SELECT o.RequestBytes,o.RequestHash,CASE WHEN @reuseResult=1 THEN CONVERT(varbinary(max),NULL) ELSE o.ResultBytes END,o.ResultHash,
                a.Generation,a.ProofHash,a.ParentOperationId,a.ParentGeneration,
                a.ParentRequestHash,a.ParentResultHash,a.ParentProofHash
            FROM activity.ProcessorOperation o WITH(HOLDLOCK)
            LEFT JOIN activity.PublicationReceiptAncestry a WITH(HOLDLOCK) ON a.OperationId=o.OperationId
                AND a.MachineId=o.MachineId AND a.StreamKeyBytes=o.StreamKeyBytes
                AND a.MappingId=o.MappingId AND a.Generation=o.AppliedGeneration
                AND a.RequestHash=o.RequestHash AND a.ResultHash=o.ResultHash
            WHERE o.OperationId=@op AND o.MachineId=@machine AND o.StreamKeyBytes=@stream AND o.MappingId=@mapping;
            """);
        Add(q,("@op",operationId),("@machine",identity.MachineId),
            ("@stream",ActivityUtf8.GetBytes(identity.StreamKey)),("@mapping",identity.MappingId),
            ("@reuseResult",previouslyLoadedResult is null?0:1));
        q.Parameters["@stream"].Size=512;
        byte[] request,requestHash,resultBytes;
        byte[]? resultHash,proofHash,parentRequestHash,parentResultHash,parentProofHash;
        Guid? parentId;long? parentGeneration;long? generation;
        await using(var rd=await q.ExecuteReaderAsync(ct))
        {
            if(!await rd.ReadAsync(ct))throw new InvalidDataException("Activity publication receipt missing.");
            request=(byte[])rd[0];requestHash=(byte[])rd[1];resultBytes=previouslyLoadedResult??(byte[])rd[2];
            resultHash=rd.IsDBNull(3)?null:(byte[])rd[3];
            generation=rd.IsDBNull(4)?null:rd.GetInt64(4);
            proofHash=rd.IsDBNull(5)?null:(byte[])rd[5];
            parentId=rd.IsDBNull(6)?null:rd.GetGuid(6);
            parentGeneration=rd.IsDBNull(7)?null:rd.GetInt64(7);
            parentRequestHash=rd.IsDBNull(8)?null:(byte[])rd[8];
            parentResultHash=rd.IsDBNull(9)?null:(byte[])rd[9];
            parentProofHash=rd.IsDBNull(10)?null:(byte[])rd[10];
            if(await rd.ReadAsync(ct))throw new InvalidDataException("Duplicate publication receipt.");
        }
        if(request.Length>8388608 || resultBytes.Length>8388608 ||
            !SHA256.HashData(request).AsSpan().SequenceEqual(requestHash))
            throw new InvalidDataException("Activity receipt request integrity mismatch.");
        try
        {
        using var json=JsonDocument.Parse(request);
        var root=json.RootElement;
        if(!root.TryGetProperty("version",out var version) || !version.TryGetInt32(out var number))
            throw new InvalidDataException("Activity publication request version missing.");
        if(number!=4)
        {
            if(number is not (2 or 3) || generation is not null || resultHash is not null)
                throw new InvalidDataException("Legacy publication receipt profile mismatch.");
            return;
        }
        if(resultHash is null || proofHash is null || generation is null ||
            !SHA256.HashData(resultBytes).AsSpan().SequenceEqual(resultHash) ||
            root.GetProperty("profile").GetString()!=ActivityPublicationPolicy.CrossBatch ||
            root.GetProperty("operationId").GetGuid()!=operationId ||
            root.GetProperty("identity").Deserialize<ActivityQualificationIdentity>()!=identity ||
            root.GetProperty("expectedGeneration").GetUInt64()!=checked((ulong)(generation.Value-1)))
            throw new InvalidDataException("Version-4 publication receipt identity mismatch.");
        var receipt=JsonSerializer.Deserialize<ActivityDurableResult>(resultBytes)
            ?? throw new InvalidDataException("Version-4 publication result missing.");
        var source=receipt.Checkpoint.AcceptedSource;
        if(receipt.Replayed || source is null || source.OperationId!=operationId ||
            receipt.Checkpoint.Qualification!=identity || receipt.Checkpoint.NextGeneration!=(ulong)generation.Value ||
            source.PublicationAncestryJson is null || source.PublicationProofSha256 is null ||
            !SHA256.HashData(Encoding.UTF8.GetBytes(source.PublicationAncestryJson)).AsSpan().SequenceEqual(proofHash) ||
            !string.Equals(source.PublicationProofSha256,Convert.ToHexString(proofHash),StringComparison.Ordinal) ||
            root.GetProperty("publicationProof").GetString()!=source.PublicationAncestryJson ||
            root.GetProperty("publicationProofSha256").GetString()!=source.PublicationProofSha256)
            throw new InvalidDataException("Version-4 publication proof differs from result.");
        var proof=JsonSerializer.Deserialize<ActivityPublicationAncestry>(source.PublicationAncestryJson)
            ?? throw new InvalidDataException("Version-4 publication ancestry missing.");
        if(proof.Profile!=ActivityPublicationPolicy.CrossBatch || proof.Batches is not {Length:>0 and <=100} ||
            proof.Batches.Select(x=>x.BatchId).Distinct().Count()!=proof.Batches.Length ||
            proof.ParentOperationId!=parentId || (generation==1)!=(parentId is null) ||
            (parentId is null ? parentGeneration is not null || parentRequestHash is not null || parentResultHash is not null || parentProofHash is not null
                : parentGeneration!=generation-1 || parentRequestHash is null || parentResultHash is null || parentProofHash is null ||
                  !string.Equals(proof.ParentRequestSha256,Convert.ToHexString(parentRequestHash),StringComparison.Ordinal) ||
                  !string.Equals(proof.ParentProofSha256,Convert.ToHexString(parentProofHash),StringComparison.Ordinal)))
            throw new InvalidDataException("Version-4 publication predecessor identity mismatch.");

        var expected=proof.Batches.ToDictionary(x=>x.BatchId);
        if(proof.AnchorWitness is {} anchor)
        {
            if(expected.TryGetValue(anchor.BatchId,out var existing))
            {
                if(existing!=anchor)throw new InvalidDataException("Version-4 anchor witness conflicts with source batch.");
            }
            else expected.Add(anchor.BatchId,anchor);
        }
        if((receipt.Checkpoint.OpenAnchor is null)!=(proof.AnchorWitness is null) ||
            proof.AnchorWitness is {} anchored &&
            (source.OpenAnchorObservation?.BatchId!=anchored.BatchId ||
             source.OpenAnchorObservation.DocumentSha256!=anchored.DocumentSha256))
            throw new InvalidDataException("Version-4 open-anchor witness mismatch.");
        var found=new HashSet<Guid>();
        var witnessList=new List<PublicationWitness>();
        await using var witnesses=Command(c,t,"""
            SELECT TOP (102) w.BatchId,w.DocumentHash,w.IngestionRequestHash
            FROM activity.PublicationSourceWitness w WITH(HOLDLOCK)
            WHERE w.OperationId=@op AND w.MachineId=@machine AND w.StreamKeyBytes=@stream
            ORDER BY w.BatchId;
            """);
        Add(witnesses,("@op",operationId),("@machine",identity.MachineId),
            ("@stream",ActivityUtf8.GetBytes(identity.StreamKey)));
        witnesses.Parameters["@stream"].Size=512;
        await using(var wr=await witnesses.ExecuteReaderAsync(ct))
            while(await wr.ReadAsync(ct))
            {
                if(witnessList.Count>=101)throw new InvalidDataException("Version-4 source witness bound exceeded.");
                witnessList.Add(new(wr.GetGuid(0),(byte[])wr[1],(byte[])wr[2]));
            }
        foreach(var witness in witnessList)
        {
            if(!expected.TryGetValue(witness.BatchId,out var pin) || !found.Add(witness.BatchId) ||
                !string.Equals(pin.DocumentSha256,Convert.ToHexString(witness.DocumentHash),StringComparison.Ordinal) ||
                !string.Equals(pin.RequestSha256,Convert.ToHexString(witness.RequestHash),StringComparison.Ordinal))
                throw new InvalidDataException("Version-4 publication source witness mismatch.");
        }
        if(found.Count!=expected.Count)throw new InvalidDataException("Version-4 publication source witness missing.");
        }
        catch(JsonException e){throw new InvalidDataException("Malformed activity publication receipt JSON.",e);}
        catch(KeyNotFoundException e){throw new InvalidDataException("Activity publication receipt property missing.",e);}
        catch(InvalidOperationException e){throw new InvalidDataException("Invalid activity publication receipt property type.",e);}
        catch(FormatException e){throw new InvalidDataException("Invalid activity publication receipt property value.",e);}
        catch(OverflowException e){throw new InvalidDataException("Invalid activity publication receipt numeric value.",e);}
    }
    private static void VerifyActivityPublicationReceiptInMemory(Guid operationId,
        ActivityQualificationIdentity identity,PublicationReadRow row,IReadOnlyList<PublicationWitness> witnessList)
    {
        var request=row.Request;var requestHash=row.RequestHash;var resultBytes=row.Result;
        var resultHash=row.ResultHash;var generation=row.Generation;var proofHash=row.ProofHash;
        var parentId=row.ParentId;var parentGeneration=row.ParentGeneration;
        var parentRequestHash=row.ParentRequestHash;var parentResultHash=row.ParentResultHash;
        var parentProofHash=row.ParentProofHash;
        if(request.Length>8388608 || resultBytes.Length>8388608 ||
            !SHA256.HashData(request).AsSpan().SequenceEqual(requestHash))
            throw new InvalidDataException("Activity receipt request integrity mismatch.");
        try
        {
        using var json=JsonDocument.Parse(request);
        var root=json.RootElement;
        if(!root.TryGetProperty("version",out var version) || !version.TryGetInt32(out var number))
            throw new InvalidDataException("Activity publication request version missing.");
        if(number!=4)
        {
            if(number is not (2 or 3) || generation is not null || resultHash is not null)
                throw new InvalidDataException("Legacy publication receipt profile mismatch.");
            return;
        }
        if(resultHash is null || proofHash is null || generation is null ||
            !SHA256.HashData(resultBytes).AsSpan().SequenceEqual(resultHash) ||
            root.GetProperty("profile").GetString()!=ActivityPublicationPolicy.CrossBatch ||
            root.GetProperty("operationId").GetGuid()!=operationId ||
            root.GetProperty("identity").Deserialize<ActivityQualificationIdentity>()!=identity ||
            root.GetProperty("expectedGeneration").GetUInt64()!=checked((ulong)(generation.Value-1)))
            throw new InvalidDataException("Version-4 publication receipt identity mismatch.");
        var receipt=JsonSerializer.Deserialize<ActivityDurableResult>(resultBytes)
            ?? throw new InvalidDataException("Version-4 publication result missing.");
        var source=receipt.Checkpoint.AcceptedSource;
        if(receipt.Replayed || source is null || source.OperationId!=operationId ||
            receipt.Checkpoint.Qualification!=identity || receipt.Checkpoint.NextGeneration!=(ulong)generation.Value ||
            source.PublicationAncestryJson is null || source.PublicationProofSha256 is null ||
            !SHA256.HashData(Encoding.UTF8.GetBytes(source.PublicationAncestryJson)).AsSpan().SequenceEqual(proofHash) ||
            !string.Equals(source.PublicationProofSha256,Convert.ToHexString(proofHash),StringComparison.Ordinal) ||
            root.GetProperty("publicationProof").GetString()!=source.PublicationAncestryJson ||
            root.GetProperty("publicationProofSha256").GetString()!=source.PublicationProofSha256)
            throw new InvalidDataException("Version-4 publication proof differs from result.");
        var proof=JsonSerializer.Deserialize<ActivityPublicationAncestry>(source.PublicationAncestryJson)
            ?? throw new InvalidDataException("Version-4 publication ancestry missing.");
        if(proof.Profile!=ActivityPublicationPolicy.CrossBatch || proof.Batches is not {Length:>0 and <=100} ||
            proof.Batches.Select(x=>x.BatchId).Distinct().Count()!=proof.Batches.Length ||
            proof.ParentOperationId!=parentId || (generation==1)!=(parentId is null) ||
            (parentId is null ? parentGeneration is not null || parentRequestHash is not null || parentResultHash is not null || parentProofHash is not null
                : parentGeneration!=generation-1 || parentRequestHash is null || parentResultHash is null || parentProofHash is null ||
                  !string.Equals(proof.ParentRequestSha256,Convert.ToHexString(parentRequestHash),StringComparison.Ordinal) ||
                  !string.Equals(proof.ParentProofSha256,Convert.ToHexString(parentProofHash),StringComparison.Ordinal)))
            throw new InvalidDataException("Version-4 publication predecessor identity mismatch.");

        var expected=proof.Batches.ToDictionary(x=>x.BatchId);
        if(proof.AnchorWitness is {} anchor)
        {
            if(expected.TryGetValue(anchor.BatchId,out var existing))
            {
                if(existing!=anchor)throw new InvalidDataException("Version-4 anchor witness conflicts with source batch.");
            }
            else expected.Add(anchor.BatchId,anchor);
        }
        if((receipt.Checkpoint.OpenAnchor is null)!=(proof.AnchorWitness is null) ||
            proof.AnchorWitness is {} anchored &&
            (source.OpenAnchorObservation?.BatchId!=anchored.BatchId ||
             source.OpenAnchorObservation.DocumentSha256!=anchored.DocumentSha256))
            throw new InvalidDataException("Version-4 open-anchor witness mismatch.");
        var found=new HashSet<Guid>();
        foreach(var witness in witnessList)
        {
            if(found.Count>=101 || !expected.TryGetValue(witness.BatchId,out var pin) ||
                !found.Add(witness.BatchId) ||
                !string.Equals(pin.DocumentSha256,Convert.ToHexString(witness.DocumentHash),StringComparison.Ordinal) ||
                !string.Equals(pin.RequestSha256,Convert.ToHexString(witness.RequestHash),StringComparison.Ordinal))
                throw new InvalidDataException("Version-4 publication source witness mismatch.");
        }
        if(found.Count!=expected.Count)throw new InvalidDataException("Version-4 publication source witness missing.");
        }
        catch(JsonException e){throw new InvalidDataException("Malformed activity publication receipt JSON.",e);}
        catch(KeyNotFoundException e){throw new InvalidDataException("Activity publication receipt property missing.",e);}
        catch(InvalidOperationException e){throw new InvalidDataException("Invalid activity publication receipt property type.",e);}
        catch(FormatException e){throw new InvalidDataException("Invalid activity publication receipt property value.",e);}
        catch(OverflowException e){throw new InvalidDataException("Invalid activity publication receipt numeric value.",e);}
    }

}
