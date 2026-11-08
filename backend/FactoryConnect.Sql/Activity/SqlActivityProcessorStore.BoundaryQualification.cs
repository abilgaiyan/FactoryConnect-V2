using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

public sealed partial class SqlActivityProcessorStore
{
    // Internal qualification primitive. A returned snapshot is NOT publication authority
    // and must never be converted into ContinuityFromPrevious by a caller.
    internal async Task<AcceptedActivityBoundaryProof> AssessRetainedBoundaryAsync(ActivityWorkerScope scope,
        SqlAcceptedActivityEvidenceReader.AcceptedRow prior,SqlAcceptedActivityEvidenceReader.AcceptedRow next,CancellationToken ct=default)
    {
        if(scope.InstallationId==Guid.Empty || scope.SiteId==Guid.Empty || scope.MachineId==Guid.Empty || scope.MappingId==Guid.Empty ||
            prior.BatchId==next.BatchId || new[]{prior,next}.Any(x=>x.SiteId!=scope.SiteId || x.MachineId!=scope.MachineId ||
                x.MappingId!=scope.MappingId || x.StreamKey!=scope.StreamKey || x.InstanceId!=scope.InstanceId))
            throw new InvalidDataException("Exact scoped cross-batch endpoints required.");
        await using var c=new SqlConnection(connectionString);await c.OpenAsync(ct);
        await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        await SqlApprovedActivityMappingResolver.LockSecurityAuthorityAsync(c,t,scope.InstallationId,ct);
        await using(var gates=new SqlCommand("""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource=@machine,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51094,'Boundary machine gate unavailable.',1;
            EXEC @r=sys.sp_getapplock @Resource=@processor,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51094,'Boundary processor gate unavailable.',1;
            """,c,t))
        {
            gates.Parameters.AddWithValue("@machine",ConfigurationLockResources.Machine(scope.InstallationId,scope.MachineId));
            gates.Parameters.AddWithValue("@processor",ProcessorLockResource(scope.MachineId,scope.MappingId,scope.StreamKey));
            await gates.ExecuteNonQueryAsync(ct);
        }
        var proof=await AssessRetainedBoundaryInTransactionAsync(c,t,scope,prior,next,ct);
        await t.CommitAsync(ct);return proof;
    }
    // Reconstruct every selected cross-batch boundary on the caller's writer
    // connection. No PR #38 assessment object is accepted as input. This is
    // intentionally not wired to publication until the policy-specific
    // checkpoint, receipt and quiet-cut contracts are implemented.
    private async Task<IReadOnlyList<AcceptedActivityBoundaryProof>> VerifySelectedBoundariesInTransactionAsync(
        SqlConnection c,SqlTransaction t,ActivityWorkerScope scope,
        SqlAcceptedActivityEvidenceReader.AcceptedRow? predecessor,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> selected,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if(selected.Count is <1 or >100)
            throw new InvalidDataException("Exact bounded cross-batch source cut required.");
        if(selected.Any(x=>x.SiteId!=scope.SiteId || x.MachineId!=scope.MachineId ||
            x.MappingId!=scope.MappingId || x.StreamKey!=scope.StreamKey ||
            x.InstanceId!=scope.InstanceId) ||
           predecessor is not null &&
           (predecessor.SiteId!=scope.SiteId || predecessor.MachineId!=scope.MachineId ||
            predecessor.MappingId!=scope.MappingId || predecessor.StreamKey!=scope.StreamKey ||
            predecessor.InstanceId!=scope.InstanceId))
            throw new InvalidDataException("Cross-batch witness endpoints differ from exact worker scope.");
        var proofs=new List<AcceptedActivityBoundaryProof>();
        var previous=predecessor;
        foreach(var current in selected)
        {
            if(previous is not null)
            {
                if(previous.InstanceId!=current.InstanceId ||
                   previous.Sequence>=current.Sequence ||
                   previous.ObservedAt>current.ObservedAt)
                    throw new InvalidDataException("Cross-batch source order changed.");
                if(previous.BatchId!=current.BatchId)
                {
                    // The verifier reopens both original documents, immutable
                    // ingestion requests and indexed endpoint projections in
                    // this transaction; a missing or conflicting witness throws.
                    // A negative RetainedLink remains a break, not continuity.
                    proofs.Add(await AssessRetainedBoundaryInTransactionAsync(
                        c,t,scope,previous,current,ct));
                }
            }
            previous=current;
        }
        return proofs.AsReadOnly();
    }

    // This overload performs no independent lock acquisition or commit.
    // A future publisher MUST call it only after obtaining its security,
    // machine and exclusive processor gates in the publishing transaction.
    // The returned assessment remains evidence, never a caller-supplied
    // publication authorization token.
    private async Task<AcceptedActivityBoundaryProof> AssessRetainedBoundaryInTransactionAsync(
        SqlConnection c,SqlTransaction t,ActivityWorkerScope scope,
        SqlAcceptedActivityEvidenceReader.AcceptedRow prior,
        SqlAcceptedActivityEvidenceReader.AcceptedRow next,CancellationToken ct)
    {
        if(scope.InstallationId==Guid.Empty || scope.SiteId==Guid.Empty ||
           scope.MachineId==Guid.Empty || scope.MappingId==Guid.Empty ||
           prior.BatchId==next.BatchId ||
           new[]{prior,next}.Any(x=>x.SiteId!=scope.SiteId || x.MachineId!=scope.MachineId ||
               x.MappingId!=scope.MappingId || x.StreamKey!=scope.StreamKey ||
               x.InstanceId!=scope.InstanceId))
            throw new InvalidDataException("Exact scoped cross-batch endpoints required.");
        var approved=await new SqlApprovedActivityMappingResolver(connectionString,scope.InstallationId)
            .ResolveInTransactionAsync(c,t,scope.SiteId,scope.MachineId,scope.MappingId,scope.StreamKey,ct);
        ValidateInterpretationPeriod(approved,[prior,next]);
        var batches=new Dictionary<Guid,ActivityBoundaryBatch>();
        await using(var q=new SqlCommand("""
            IF NOT EXISTS(SELECT 1 FROM acquisition.MtConnectCursor WITH(HOLDLOCK)
                WHERE SiteId=@site AND MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND NextSequence>@nextSequence)
                THROW 51094,'Boundary source frontier is not durably accepted.',1;
            SELECT b.BatchId,b.AcknowledgementId,b.InstanceId,b.FromSequence,b.NextSequence,b.FirstAvailableSequence,
                b.DocumentHash,b.RawDocument,b.RuntimeIdentityId,s.RequestBytes,s.RequestHash,
                o.RawValue,o.Quality,o.SourceTimestamp,o.ObservedAt,o.ContentHash,o.CanonicalBytes
            FROM (VALUES(@prior,@priorSequence),(@next,@nextSequence)) e(BatchId,Sequence)
            INNER LOOP JOIN acquisition.MtConnectBatch b WITH(HOLDLOCK,FORCESEEK) ON b.BatchId=e.BatchId
            INNER LOOP JOIN security.SecurityOperation s WITH(HOLDLOCK,FORCESEEK) ON s.OperationId=b.BatchId
            INNER LOOP JOIN acquisition.MtConnectObservation o WITH(HOLDLOCK,FORCESEEK)
                ON o.MachineId=@machine AND o.StreamKeyBytes=@stream AND o.InstanceId=@instance AND o.Sequence=e.Sequence
            WHERE b.SiteId=@site AND b.MachineId=@machine AND b.StreamKeyBytes=@stream AND b.InstanceId=@instance
                AND s.ActorPrincipalId=b.RuntimeIdentityId AND s.ScopeKind='M' AND s.SiteId=@site AND s.MachineId=@machine
                AND o.SiteId=@site AND o.BatchId=b.BatchId AND o.MappingId=@mapping AND o.AddressBytes=@address
                AND DATALENGTH(b.RawDocument)<=1048576 AND DATALENGTH(s.RequestBytes)<=16384
                AND DATALENGTH(o.CanonicalBytes)<=1048576 AND DATALENGTH(o.RawValue)<=2097152;
            """,c,t))
        {
            BindScope(q,ActivityQualificationIdentity.From(approved.Signal,"boundary-assessment-only"));
            q.Parameters.AddWithValue("@site",scope.SiteId);q.Parameters.AddWithValue("@prior",prior.BatchId);q.Parameters.AddWithValue("@next",next.BatchId);
            WorkerNumber(q,"@instance",scope.InstanceId);WorkerNumber(q,"@priorSequence",prior.Sequence);WorkerNumber(q,"@nextSequence",next.Sequence);
            q.Parameters.Add("@address",SqlDbType.VarBinary,512).Value=Utf8.GetBytes(approved.Signal.SourceAddress);
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var id=r.GetGuid(0);var endpoint=id==prior.BatchId?prior:next;
                if(r.GetString(11)!=endpoint.RawValue || r.GetString(12)!=endpoint.Quality || r.GetString(13)!=endpoint.SourceTimestamp ||
                    r.GetDateTimeOffset(14)!=endpoint.ObservedAt || Convert.ToHexString((byte[])r[15])!=endpoint.CanonicalSha256 ||
                    !SHA256.HashData((byte[])r[16]).AsSpan().SequenceEqual((byte[])r[15]))
                    throw new InvalidDataException("Retained boundary endpoint projection changed.");
                batches.Add(id,new(r.GetGuid(8),new(id,r.GetGuid(1),decimal.ToUInt64(r.GetDecimal(2)),
                    decimal.ToUInt64(r.GetDecimal(3)),decimal.ToUInt64(r.GetDecimal(4)),decimal.ToUInt64(r.GetDecimal(5)),
                    Convert.ToHexString((byte[])r[6])),(byte[])r[7],(byte[])r[9],(byte[])r[10]));
            }
        }
        if(batches.Count!=2)throw new InvalidDataException("Retained boundary batch, endpoint or immutable receipt is missing.");
        var proof=AcceptedActivityBoundaryVerifier.Assess(approved,prior,next,batches[prior.BatchId],batches[next.BatchId]);
        return proof;
    }

}
