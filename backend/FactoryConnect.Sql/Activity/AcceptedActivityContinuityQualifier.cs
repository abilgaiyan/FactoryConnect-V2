using System.Security.Cryptography;
using System.Text;

namespace FactoryConnect.Sql.ActivityProcessing;

/// <summary>
/// A bounded candidate cut derived from verified accepted-observation rows and
/// a separately resolved, approved mapping. This is not an authorization token
/// and is NOT a commit-ready input until reader, mapping and writer share one
/// protected source/checkpoint transaction.
/// </summary>
public sealed record QualifiedActivityCut(
    ActivityQualificationIdentity Qualification,
    IReadOnlyList<ActivityInputSample> Samples,
    string Assurance,
    int ProvenInternalLinks,
    int UnprovenBoundaryLinks);

public static class AcceptedActivityContinuityQualifier
{
    public const string Assurance="accepted-same-batch-observation-continuity-not-physical-completeness";
    private const string Profile="retained-batch-continuity-v1";

    public static QualifiedActivityCut Qualify(
        ApprovedActivitySignal approved,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> accepted)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(accepted);
        if(approved.SiteId==Guid.Empty||approved.PackageRevisionId==Guid.Empty||
           approved.AcknowledgementId==Guid.Empty||accepted.Count is <1 or >100)
            throw new InvalidDataException("Exact bounded approved activity cut required.");
        if(approved.PackageSha256.Length!=64||
           !approved.PackageSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Canonical approved package hash required.");
        // The identity is stable across source batches, but the per-sample
        // continuity flag is not: only same verified batch provenance earns
        // an internal link. Do not infer coverage across batches or restarts.
        var policy=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Profile}|{approved.PackageRevisionId:N}|{approved.AcknowledgementId:N}|{approved.PackageSha256}")));
        var qualification=ActivityQualificationIdentity.From(approved.Signal,policy);
        var samples=new List<ActivityInputSample>(accepted.Count);
        SqlAcceptedActivityEvidenceReader.AcceptedRow? previous=null;
        var linked=0;
        var held=0;
        foreach(var row in accepted)
        {
            if(row.SiteId!=approved.SiteId||
               row.MachineId!=approved.Signal.MachineId||
               !string.Equals(row.StreamKey,approved.Signal.StreamKey,StringComparison.Ordinal)||
               row.MappingId!=approved.Signal.MappingId||
               !string.Equals(row.Address,approved.Signal.SourceAddress,StringComparison.Ordinal)||
               row.AcknowledgementId!=approved.AcknowledgementId||
               row.BatchId==Guid.Empty||
               row.ObservedAt.Offset!=TimeSpan.Zero||
               row.Quality is not ("source-reported" or "unavailable")||
               row.CanonicalSha256.Length!=64||!row.CanonicalSha256.All(Uri.IsHexDigit)||
               row.DocumentSha256.Length!=64||!row.DocumentSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Activity candidate conflicts with approved accepted evidence.");
            if(previous is not null&&
               ((row.InstanceId==previous.InstanceId&&row.Sequence<=previous.Sequence)||
                row.ObservedAt<previous.ObservedAt))
                throw new InvalidDataException("Non-advancing or time-reversing accepted source cut.");
            var sameBatch=previous is not null &&
                previous.InstanceId==row.InstanceId &&
                previous.BatchId==row.BatchId &&
                string.Equals(previous.DocumentSha256,row.DocumentSha256,StringComparison.Ordinal) &&
                previous.AcknowledgementId==row.AcknowledgementId;
            if(sameBatch)linked++;
            else if(previous is not null)held++;
            samples.Add(new(row.MachineId,row.StreamKey,row.MappingId,row.Address,
                row.InstanceId,row.Sequence,row.ObservedAt,row.RawValue,row.Quality,
                sameBatch));
            previous=row;
        }
        return new(qualification,samples.AsReadOnly(),Assurance,linked,held);
    }
}
