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
    // Distinct immutable identities: introducing the new profile must never
    // reinterpret a legacy checkpoint or silently adopt its operation receipts.
    internal const string LegacyProfile="retained-batch-continuity-v1";
    internal const string TransactionBoundProfile="retained-cross-batch-receipt-v1";

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
        var qualification=QualificationFor(approved);
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
    internal static ActivityQualificationIdentity QualificationFor(ApprovedActivitySignal approved,string profile=LegacyProfile)
        => QualificationForProfile(approved,profile);

    // Identity construction only. This does not authorize publication or
    // upgrade an existing checkpoint; writer-side proof and replay gates remain
    // mandatory before the new profile can be selected by a worker.
    internal static ActivityQualificationIdentity QualificationForProfile(
        ApprovedActivitySignal approved,string profile)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if(profile is not (LegacyProfile or TransactionBoundProfile))
            throw new ArgumentOutOfRangeException(nameof(profile),"Unknown activity continuity profile.");
        if(approved.PackageRevisionId==Guid.Empty || approved.AcknowledgementId==Guid.Empty ||
           approved.PackageSha256.Length!=64 || !approved.PackageSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Exact approved activity policy identity required.");
        var policy=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{profile}|{approved.PackageRevisionId:N}|{approved.AcknowledgementId:N}|{approved.PackageSha256}")));
        return ActivityQualificationIdentity.From(approved.Signal,policy);
    }

}
