using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    // Read-side receipt guard. This verifies immutable publication identity and
    // witness bindings, not physical completeness or reconstructed source documents.
    private static async Task VerifyActivityPublicationReceiptAsync(SqlConnection c,SqlTransaction t,
        Guid operationId,ActivityQualificationIdentity identity,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            SELECT o.RequestBytes,o.RequestHash,o.ResultBytes,o.ResultHash,
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
            ("@stream",ActivityUtf8.GetBytes(identity.StreamKey)),("@mapping",identity.MappingId));
        q.Parameters["@stream"].Size=512;
        byte[] request,requestHash,resultBytes;
        byte[]? resultHash,proofHash,parentRequestHash,parentResultHash,parentProofHash;
        Guid? parentId;long? parentGeneration;long? generation;
        await using(var rd=await q.ExecuteReaderAsync(ct))
        {
            if(!await rd.ReadAsync(ct))throw new InvalidDataException("Activity publication receipt missing.");
            request=(byte[])rd[0];requestHash=(byte[])rd[1];resultBytes=(byte[])rd[2];
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

        await using var witnesses=Command(c,t,"""
            SELECT TOP (102) w.BatchId,w.DocumentHash,w.IngestionRequestHash
            FROM activity.PublicationSourceWitness w WITH(HOLDLOCK)
            WHERE w.OperationId=@op AND w.MachineId=@machine AND w.StreamKeyBytes=@stream
            ORDER BY w.BatchId;
            """);
        Add(witnesses,("@op",operationId),("@machine",identity.MachineId),
            ("@stream",ActivityUtf8.GetBytes(identity.StreamKey)));
        witnesses.Parameters["@stream"].Size=512;
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
        await using var wr=await witnesses.ExecuteReaderAsync(ct);
        while(await wr.ReadAsync(ct))
        {
            if(found.Count>=101)throw new InvalidDataException("Version-4 source witness bound exceeded.");
            var batch=wr.GetGuid(0);
            if(!expected.TryGetValue(batch,out var pin) || !found.Add(batch) ||
                !string.Equals(pin.DocumentSha256,Convert.ToHexString((byte[])wr[1]),StringComparison.Ordinal) ||
                !string.Equals(pin.RequestSha256,Convert.ToHexString((byte[])wr[2]),StringComparison.Ordinal))
                throw new InvalidDataException("Version-4 publication source witness mismatch.");
        }
        if(found.Count!=expected.Count)throw new InvalidDataException("Version-4 publication source witness missing.");
    }
}
