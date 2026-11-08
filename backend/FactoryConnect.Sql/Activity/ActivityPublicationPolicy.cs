namespace FactoryConnect.Sql.ActivityProcessing;

public static class ActivityPublicationPolicy
{
    public const string Legacy="retained-batch-continuity-v1";
    public const string CrossBatch="retained-cross-batch-receipt-v1";
    internal static void Validate(string profile)
    {if(profile is not (Legacy or CrossBatch))throw new ArgumentException("Explicit supported activity publication profile required.");}
}

internal sealed record ActivityPublicationWitness(Guid BatchId,string DocumentSha256,string RequestSha256);
internal sealed record ActivityPublicationAncestry(string Profile,Guid? ParentOperationId,
    string? ParentRequestSha256,string? ParentProofSha256,ActivityPublicationWitness[] Batches,ActivityPublicationWitness? AnchorWitness);
