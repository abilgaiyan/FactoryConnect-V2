using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.ActivityProcessing;

/// <summary>
/// A verified approved-and-acknowledged activity signal definition.
/// This does not establish acquisition continuity, and does not authorize
/// checkpoint publication on its own.
/// </summary>
public sealed record ApprovedActivitySignal(
    Guid SiteId, Guid PackageRevisionId, Guid AcknowledgementId,
    string PackageSha256, QualifiedActivitySignal Signal,
    DateTimeOffset EffectiveFrom,DateTimeOffset? EffectiveTo);

/// <summary>
/// A narrow, versioned normalization profile held in the immutable,
/// independently approved package. Never interpret counter normalization
/// or caller-supplied dictionaries as a machine execution-state mapping.
/// </summary>
public static class ApprovedActivityValueMap
{
    public const string Prefix="activity-state-v1|";
    public static IReadOnlyDictionary<string,ActivityState> Parse(string normalization)
    {
        if(string.IsNullOrEmpty(normalization)||normalization.Length>100||
           !normalization.StartsWith(Prefix,StringComparison.Ordinal))
            throw new InvalidDataException("Approved activity-state normalization profile is missing.");
        var items=normalization[Prefix.Length..].Split('|');
        if(items.Length is <2 or >8)
            throw new InvalidDataException("Activity-state mapping requires 2–8 explicit source values.");
        var values=new Dictionary<string,ActivityState>(StringComparer.Ordinal);
        string? previous=null;
        foreach(var item in items)
        {
            var at=item.IndexOf('=');
            if(at<=0||at!=item.LastIndexOf('=')||at==item.Length-1)
                throw new InvalidDataException("Invalid canonical activity-state mapping entry.");
            var raw=item[..at];
            var literal=item[(at+1)..];
            if(raw.Any(c=>char.IsWhiteSpace(c)||c is '|' or '=')||
               previous is not null&&StringComparer.Ordinal.Compare(previous,raw)>=0||
               !Enum.TryParse<ActivityState>(literal,false,out var state)||
               !Enum.IsDefined(state)||
               !string.Equals(state.ToString(),literal,StringComparison.Ordinal)||
               !values.TryAdd(raw,state))
                throw new InvalidDataException("Non-canonical or ambiguous activity-state mapping.");
            previous=raw;
        }
        if(values.Values.Distinct().Count()<2)
            throw new InvalidDataException("Activity signal must distinguish at least two states.");
        return values;
    }
}

/// <summary>
/// Resolve the exact active acknowledgement, approved immutable package
/// revision, source mapping and versioned state values under the machine
/// configuration gate. This is a read-only qualification primitive.
/// </summary>
public sealed class SqlApprovedActivityMappingResolver(string connectionString,Guid installationId)
{
    private static readonly UTF8Encoding ExactUtf8=new(false,true);

    public async Task<ApprovedActivitySignal> ResolveAsync(
        Guid siteId,Guid machineId,Guid mappingId,string streamKey,
        CancellationToken ct=default)
    {
        if(installationId==Guid.Empty||siteId==Guid.Empty||machineId==Guid.Empty||
           mappingId==Guid.Empty||string.IsNullOrWhiteSpace(streamKey))
            throw new ArgumentException("Exact approved activity mapping scope required.");
        await using var connection=new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx=(SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,ct);
        try
        {
            await using(var gate=new SqlCommand("""
                DECLARE @code int;
                EXEC @code=sys.sp_getapplock @Resource=@resource,@LockMode='Shared',
                    @LockOwner='Transaction',@LockTimeout=10000;
                IF @code<0 THROW 51076,'Activity mapping configuration gate unavailable.',1;
                """,connection,tx))
            {
                gate.Parameters.Add("@resource",SqlDbType.NVarChar,255).Value=
                    ConfigurationLockResources.Machine(installationId,machineId);
                await gate.ExecuteNonQueryAsync(ct);
            }
            var resolved=await ResolveInTransactionAsync(connection,tx,siteId,machineId,mappingId,streamKey,ct);
            await tx.CommitAsync(ct);
            return resolved;
        }
        catch
        {
            try{await tx.RollbackAsync(CancellationToken.None);}
            catch(Exception error)when(error is SqlException or InvalidOperationException){}
            throw;
        }
    }

    internal async Task<ApprovedActivitySignal> ResolveInTransactionAsync(
        SqlConnection connection,SqlTransaction tx,Guid siteId,Guid machineId,
        Guid mappingId,string streamKey,CancellationToken ct=default)
    {
            Guid revision;
            Guid acknowledgement;
            DateTimeOffset appliedFrom,activationFrom,assignmentFrom,packageFrom;
            DateTimeOffset? appliedTo,activationTo,assignmentTo,packageTo;
            byte[] packageHash;
            byte[] canonical;
            string semantic;
            string protocol;
            string actualStream;
            string encoding;
            string normalization;
            byte[] address;
            await using(var query=new SqlCommand("""
                DECLARE @now datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');
                SELECT a.AcknowledgementId,a.PackageRevisionId,p.ContentHash,
                    p.CanonicalBytes,m.SemanticKey,m.Protocol,m.StreamKey,
                    m.AddressEncoding,m.Normalization,m.AddressBytes,
                    a.AppliedFrom,a.EffectiveTo,activation.EffectiveFrom,activation.EffectiveTo,
                    assignment.EffectiveFrom,assignment.EffectiveTo
                FROM configuration.MachineAcknowledgementHead h WITH(HOLDLOCK)
                JOIN configuration.RuntimeActivationAcknowledgement a WITH(HOLDLOCK)
                    ON a.SiteId=h.SiteId AND a.MachineId=h.MachineId
                    AND a.AcknowledgementId=h.AcknowledgementId
                    AND a.ActivationGeneration=h.ActivationGeneration
                JOIN configuration.ActivationRequest activation
                    ON activation.ActivationId=a.ActivationId
                    AND activation.SiteId=a.SiteId AND activation.MachineId=a.MachineId
                JOIN configuration.ConfigurationPackageRevision p WITH(HOLDLOCK)
                    ON p.SiteId=a.SiteId AND p.PackageRevisionId=a.PackageRevisionId
                    AND p.ContentHash=a.ContentHash
                JOIN configuration.PackageReview review WITH(HOLDLOCK)
                    ON review.SiteId=p.SiteId
                    AND review.PackageRevisionId=p.PackageRevisionId
                    AND review.ReviewId=a.ReviewId AND review.ContentHash=p.ContentHash
                    AND review.Decision='approved'
                JOIN configuration.PackageSignalMapping m WITH(HOLDLOCK)
                    ON m.PackageRevisionId=p.PackageRevisionId
                    AND m.MachineId=a.MachineId AND m.MappingId=@mapping
                JOIN configuration.PackageMachineAssignment assignment
                    ON assignment.PackageRevisionId=p.PackageRevisionId
                    AND assignment.MachineId=a.MachineId AND assignment.SiteId=a.SiteId
                JOIN configuration.MachineOperationalState operational
                    ON operational.SiteId=a.SiteId AND operational.MachineId=a.MachineId
                    AND operational.Generation=a.OperationalGeneration
                JOIN configuration.MachineOperationalTransition state
                    ON state.StateId=operational.StateId AND state.Status='enabled'
                JOIN configuration.RuntimeAssignmentState currentAssignment
                    ON currentAssignment.SiteId=a.SiteId
                    AND currentAssignment.MachineId=a.MachineId
                    AND currentAssignment.AssignmentId=a.AssignmentId
                    AND currentAssignment.AssignmentGeneration=a.AssignmentGeneration
                    AND currentAssignment.RuntimeIdentityId=a.RuntimeIdentityId
                WHERE h.SiteId=@site AND h.MachineId=@machine
                    AND p.SubmittedAt IS NOT NULL
                    AND a.AppliedFrom<=@now AND (a.EffectiveTo IS NULL OR a.EffectiveTo>@now)
                    AND activation.EffectiveFrom<=@now
                    AND (activation.EffectiveTo IS NULL OR activation.EffectiveTo>@now)
                    AND assignment.EffectiveFrom<=@now
                    AND (assignment.EffectiveTo IS NULL OR assignment.EffectiveTo>@now)
                    AND NOT EXISTS (SELECT 1 FROM configuration.PackageApprovalRevocation r
                        WHERE r.ReviewId=a.ReviewId)
                    AND NOT EXISTS (SELECT 1 FROM configuration.ActivationRequestRevocation r
                        WHERE r.ActivationId=a.ActivationId)
                    AND NOT EXISTS (SELECT 1 FROM configuration.RuntimeAssignmentRevocation r
                        WHERE r.AssignmentId=a.AssignmentId)
                    AND NOT EXISTS (SELECT 1 FROM security.RuntimeCredentialRevocation r
                        WHERE r.RuntimeIdentityId=a.RuntimeIdentityId
                            AND r.CredentialGeneration=a.CredentialGeneration)
                    AND NOT EXISTS (SELECT 1 FROM configuration.PolicySafetyRevocation r
                        WHERE r.SiteId=a.SiteId AND r.EffectiveFrom<=@now
                          AND (
                            (r.PolicyKind='timezone' AND r.TimezoneRevisionId=p.TimezoneRevisionId)
                            OR (r.PolicyKind='schedule' AND r.ScheduleRevisionId=assignment.ScheduleRevisionId)
                            OR (r.PolicyKind='break' AND r.BreakPolicyRevisionId=assignment.BreakPolicyRevisionId)
                          ));
                """,connection,tx))
            {
                query.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=siteId;
                query.Parameters.Add("@machine",SqlDbType.UniqueIdentifier).Value=machineId;
                query.Parameters.Add("@mapping",SqlDbType.UniqueIdentifier).Value=mappingId;
                await using var reader=await query.ExecuteReaderAsync(ct);
                if(!await reader.ReadAsync(ct))
                    throw new InvalidDataException("Approved active activity mapping does not exist.");
                acknowledgement=reader.GetGuid(0);
                revision=reader.GetGuid(1);
                packageHash=(byte[])reader[2];
                canonical=(byte[])reader[3];
                semantic=reader.GetString(4);
                protocol=reader.GetString(5);
                actualStream=reader.GetString(6);
                encoding=reader.GetString(7);
                normalization=reader.GetString(8);
                address=(byte[])reader[9];
                appliedFrom=reader.GetDateTimeOffset(10);
                appliedTo=reader.IsDBNull(11)?null:reader.GetDateTimeOffset(11);
                activationFrom=reader.GetDateTimeOffset(12);
                activationTo=reader.IsDBNull(13)?null:reader.GetDateTimeOffset(13);
                assignmentFrom=reader.GetDateTimeOffset(14);
                assignmentTo=reader.IsDBNull(15)?null:reader.GetDateTimeOffset(15);
                packageFrom=assignmentFrom;
                packageTo=assignmentTo;
                if(await reader.ReadAsync(ct))
                    throw new InvalidDataException("Ambiguous active mapping identity.");
            }
            if(canonical.Length is <1 or >1048576 ||
               !CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(canonical),packageHash)||
               !string.Equals(semantic,"machine-activity",StringComparison.Ordinal)||
               !string.Equals(protocol,"mtconnect",StringComparison.Ordinal)||
               !string.Equals(encoding,"utf-8",StringComparison.Ordinal)||
               !string.Equals(actualStream,streamKey,StringComparison.Ordinal)||
               address.Length is <1 or >512)
                throw new InvalidDataException("Approved mapping has unsupported activity semantics or invalid source scope.");
            var sourceAddress=ExactUtf8.GetString(address);
            using(var doc=JsonDocument.Parse(canonical))
            {
                var root=doc.RootElement;
                if(root.GetProperty("siteId").GetGuid()!=siteId||
                   root.GetProperty("revisionId").GetGuid()!=revision)
                    throw new InvalidDataException("Approved package identity differs from SQL projection.");
                var machine=root.GetProperty("machines").EnumerateArray()
                    .Where(m=>m.GetProperty("machineId").GetGuid()==machineId).ToArray();
                if(machine.Length!=1)
                    throw new InvalidDataException("Approved package machine scope missing or ambiguous.");
                var signal=machine[0].GetProperty("signals").EnumerateArray()
                    .Where(m=>m.GetProperty("mappingId").GetGuid()==mappingId).ToArray();
                packageFrom=machine[0].GetProperty("effectiveFrom").GetDateTimeOffset();
                packageTo=machine[0].GetProperty("effectiveTo").ValueKind==JsonValueKind.Null
                    ?null:machine[0].GetProperty("effectiveTo").GetDateTimeOffset();
                if(packageFrom!=assignmentFrom||packageTo!=assignmentTo)
                    throw new InvalidDataException("Approved package assignment effective interval differs from projection.");
                if(signal.Length!=1 ||
                   !string.Equals(signal[0].GetProperty("semanticKey").GetString(),semantic,StringComparison.Ordinal)||
                   !string.Equals(signal[0].GetProperty("protocol").GetString(),protocol,StringComparison.Ordinal)||
                   !string.Equals(signal[0].GetProperty("streamKey").GetString(),actualStream,StringComparison.Ordinal)||
                   !string.Equals(signal[0].GetProperty("addressEncoding").GetString(),encoding,StringComparison.Ordinal)||
                   !string.Equals(signal[0].GetProperty("normalization").GetString(),normalization,StringComparison.Ordinal)||
                   !signal[0].GetProperty("addressBytes").GetBytesFromBase64()
                       .AsSpan().SequenceEqual(address))
                    throw new InvalidDataException("Approved package signal differs from SQL mapping projection.");
            }
            var map=ApprovedActivityValueMap.Parse(normalization);
            var from=new[]{appliedFrom,activationFrom,assignmentFrom,packageFrom}.Max();
            var ends=new DateTimeOffset?[]{appliedTo,activationTo,assignmentTo,packageTo};
            var to=ends.Where(x=>x is not null).Select(x=>x!.Value)
                .DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            if(to<=from)
                throw new InvalidDataException("Approved mapping has no effective interpretation interval.");
            return new(siteId,revision,acknowledgement,Convert.ToHexString(packageHash),
                new(machineId,streamKey,mappingId,sourceAddress,map),from,
                to==DateTimeOffset.MaxValue?null:to);
    }
}
