using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Sql.ActivityProcessing;

// Candidate evidence only. Publication deliberately continues to use retained-batch-v1.
internal sealed record AcceptedActivityBoundaryProof(
    ActivityQualificationIdentity Qualification,
    SqlAcceptedActivityEvidenceReader.AcceptedRow Predecessor,
    SqlAcceptedActivityEvidenceReader.AcceptedRow Successor,
    string PredecessorRequestSha256,string SuccessorRequestSha256,
    bool RetainedLink,string Disposition)
{
    internal const string Profile="accepted-receipt-boundary-candidate-v1-not-physical-completeness";
}


// Deterministic transaction-evidence digest. This is not an authorization token:
// a publisher must reconstruct all witnesses on its own locked transaction
// before recording this digest in the immutable operation request and result.
internal static class AcceptedActivityBoundaryManifest
{
    internal const string Profile="retained-cross-batch-proof-manifest-v1";

    internal static string Fingerprint(IReadOnlyList<AcceptedActivityBoundaryProof> proofs)
    {
        ArgumentNullException.ThrowIfNull(proofs);
        if(proofs.Count is <1 or >100)
            throw new InvalidDataException("Bounded cross-batch proof manifest required.");
        for(var i=0;i<proofs.Count;i++)
        {
            var proof=proofs[i];
            if(!proof.RetainedLink || proof.Disposition!="exact-direct-retained-predecessor" ||
               proof.Predecessor.BatchId==proof.Successor.BatchId ||
               proof.Predecessor.InstanceId!=proof.Successor.InstanceId ||
               proof.Predecessor.Sequence>=proof.Successor.Sequence ||
               proof.Predecessor.ObservedAt>=proof.Successor.ObservedAt ||
               proof.PredecessorRequestSha256.Length!=64 ||
               proof.SuccessorRequestSha256.Length!=64 ||
               !proof.PredecessorRequestSha256.All(Uri.IsHexDigit) ||
               !proof.SuccessorRequestSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Unqualified cross-batch witness cannot enter a publication manifest.");
            if(i>0 && (proofs[i-1].Successor.Sequence>=proof.Predecessor.Sequence ||
               proofs[i-1].Successor.InstanceId!=proof.Predecessor.InstanceId ||
               proofs[i-1].Qualification!=proof.Qualification))
                throw new InvalidDataException("Cross-batch manifest witness order or qualification changed.");
        }
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{
            profile=Profile,
            witnesses=proofs.Select(p=>new{
                qualification=p.Qualification,
                predecessor=p.Predecessor,
                successor=p.Successor,
                predecessorReceipt=p.PredecessorRequestSha256,
                successorReceipt=p.SuccessorRequestSha256,
                disposition=p.Disposition
            }).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

internal sealed record ActivityBoundaryBatch(Guid RuntimeIdentityId,
    SqlAcceptedActivityEvidenceReader.BatchProof Header,byte[] Document,byte[] Request,byte[] RequestHash);

internal static class AcceptedActivityBoundaryVerifier
{
    internal static AcceptedActivityBoundaryProof Assess(ApprovedActivitySignal approved,
        SqlAcceptedActivityEvidenceReader.AcceptedRow prior,SqlAcceptedActivityEvidenceReader.AcceptedRow next,
        ActivityBoundaryBatch preceding,ActivityBoundaryBatch following)
    {
        if(prior.BatchId==next.BatchId)throw new InvalidDataException("A cross-batch boundary requires distinct batches.");
        var qualification=AcceptedActivityContinuityQualifier.Qualify(approved,[prior,next]).Qualification;
        var left=Verify(approved,prior,preceding);
        var right=Verify(approved,next,following);
        var leftStates=left.Frame.Observations.Where(x=>x.DataItemId==approved.Signal.SourceAddress).ToArray();
        var rightStates=right.Frame.Observations.Where(x=>x.DataItemId==approved.Signal.SourceAddress).ToArray();
        string disposition;
        if(prior.InstanceId!=next.InstanceId)disposition="source-instance-change";
        else if(preceding.RuntimeIdentityId!=following.RuntimeIdentityId)disposition="runtime-change";
        else if(right.Predecessor!=prior.BatchId || right.Instance!=prior.InstanceId)disposition="unproven-ingestion-predecessor";
        else if(preceding.Header.NextSequence!=following.Header.FromSequence)disposition="overlap-or-gap";
        else if(leftStates[^1].Sequence!=prior.Sequence || rightStates[0].Sequence!=next.Sequence)disposition="omitted-boundary-observation";
        else if(next.ObservedAt<=prior.ObservedAt)disposition="nonpositive-observation-time";
        else if(prior.Quality!="source-reported" || next.Quality!="source-reported" ||
            !approved.Signal.ExactValues.ContainsKey(prior.RawValue) || !approved.Signal.ExactValues.ContainsKey(next.RawValue))disposition="unqualified-endpoint";
        else disposition="exact-direct-retained-predecessor";
        return new(qualification,prior,next,Convert.ToHexString(preceding.RequestHash),
            Convert.ToHexString(following.RequestHash),disposition=="exact-direct-retained-predecessor",disposition);
    }

    private static (MtConnectSampleFrame Frame,Guid? Predecessor,ulong? Instance) Verify(
        ApprovedActivitySignal approved,SqlAcceptedActivityEvidenceReader.AcceptedRow row,ActivityBoundaryBatch batch)
    {
        var h=batch.Header;
        if(batch.RuntimeIdentityId==Guid.Empty || batch.Request.Length is <1 or >16384 ||
            h.BatchId!=row.BatchId || h.AcknowledgementId!=approved.AcknowledgementId || h.InstanceId!=row.InstanceId ||
            h.DocumentSha256!=row.DocumentSha256 || h.DocumentSha256!=Convert.ToHexString(SHA256.HashData(batch.Document)) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(batch.Request),batch.RequestHash))
            throw new InvalidDataException("Retained boundary batch or ingestion receipt integrity differs.");
        const string prefix="mtconnect:uuid:";
        if(!row.StreamKey.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Exact MTConnect stream required.");
        var frame=MtConnectSampleParser.Parse(batch.Document,row.StreamKey[prefix.Length..],h.FromSequence);
        if(frame.InstanceId!=h.InstanceId || frame.FirstSequence!=h.FirstAvailableSequence || frame.NextSequence!=h.NextSequence)
            throw new InvalidDataException("Boundary source headers differ from retained batch.");
        var source=frame.Observations.SingleOrDefault(x=>x.Sequence==row.Sequence);
        if(source is null || source.DataItemId!=row.Address || source.RawValue!=row.RawValue || source.Quality!=row.Quality ||
            source.SourceTimestamp!=row.SourceTimestamp || source.Timestamp!=row.ObservedAt || source.ContentHash!=row.CanonicalSha256)
            throw new InvalidDataException("Boundary endpoint differs from verified source document.");
        try
        {
            using var document=JsonDocument.Parse(batch.Request);var root=document.RootElement;
            var predecessor=root.GetProperty("ExpectedBatchId").ValueKind==JsonValueKind.Null ? (Guid?)null : root.GetProperty("ExpectedBatchId").GetGuid();
            var instanceText=root.GetProperty("expectedInstance").GetString();
            ulong? instance=instanceText is null ? null : ulong.Parse(instanceText,NumberStyles.None,CultureInfo.InvariantCulture);
            if((predecessor is null)!=(instance is null))throw new InvalidDataException("Incomplete ingestion predecessor identity.");
            var expected=JsonSerializer.SerializeToUtf8Bytes(new {
                version=1,action="ingest-mtconnect",SiteId=row.SiteId,MachineId=row.MachineId,
                AcknowledgementId=h.AcknowledgementId,StreamKey=row.StreamKey,ExpectedBatchId=predecessor,
                expectedInstance=instance?.ToString(CultureInfo.InvariantCulture),from=h.FromSequence.ToString(CultureInfo.InvariantCulture),documentHash=h.DocumentSha256
            });
            if(!expected.AsSpan().SequenceEqual(batch.Request))throw new InvalidDataException("Boundary receipt does not bind exact batch scope and document.");
            return (frame,predecessor,instance);
        }
        catch(Exception e) when(e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or OverflowException)
        {throw new InvalidDataException("Malformed retained ingestion receipt.",e);}
    }
}
