using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record ActivationAcknowledgementRequest(Guid OperationId,Guid AcknowledgementId,Guid SiteId,Guid MachineId,
    Guid ActivationId,long ActivationGeneration,Guid PackageRevisionId,byte[] ContentHash,Guid ApprovalReviewId,
    Guid RuntimeIdentityId,Guid AssignmentId,long AssignmentGeneration,long CredentialGeneration,string Reason);

public sealed partial class SqlSecurityAuthority
{
    public Task<byte[]> AcknowledgeActivationAsync(RuntimeMtlsChannel channel,ActivationAcknowledgementRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.AcknowledgementId,0,request.Reason);
        if(new[]{request.SiteId,request.MachineId,request.ActivationId,request.PackageRevisionId,request.ApprovalReviewId,request.RuntimeIdentityId,request.AssignmentId}.Any(id=>id==Guid.Empty)||
            request.ActivationGeneration<=0||request.AssignmentGeneration<=0||request.CredentialGeneration<=0||request.ContentHash.Length!=32)throw new ArgumentException("Exact acknowledgement identities and generations required.");
        ArgumentNullException.ThrowIfNull(channel);var hash=request.ContentHash.ToArray();var evidence=channel.Revalidate(ct);
        var certificateHash=SHA256.HashData(evidence.Certificate);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="acknowledge-activation",request.AcknowledgementId,request.SiteId,request.MachineId,request.ActivationId,
            activationGeneration=Decimal(request.ActivationGeneration),request.PackageRevisionId,contentHash=Convert.ToHexString(hash),request.ApprovalReviewId,request.RuntimeIdentityId,request.AssignmentId,
            assignmentGeneration=Decimal(request.AssignmentGeneration),credentialGeneration=Decimal(request.CredentialGeneration),certificateHash=Convert.ToHexString(certificateHash),request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.runtime.acknowledgement",true);
            var initial=await State(c,t,ct);var actor=await ValidateRuntimeMachineAuthority(c,t,evidence,request.SiteId,request.MachineId,initial.Now,ct);
            MatchAcknowledgingRuntime(request,actor);
            var completed=await FindOperation(c,t,request.OperationId,ct);
            if(completed is not null)
            {if(completed.Actor!=actor.RuntimeIdentityId||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            byte[] canonical;DateTimeOffset from;DateTimeOffset? until;
            await using(var q=Command(c,t,"""
                SELECT p.CanonicalBytes,a.EffectiveFrom,a.EffectiveTo FROM configuration.ActivationRequest a
                JOIN configuration.ConfigurationPackageRevision p ON p.SiteId=a.SiteId AND p.PackageRevisionId=a.PackageRevisionId AND p.ContentHash=a.ContentHash
                JOIN configuration.PackageReview v ON v.ReviewId=a.ReviewId AND v.Decision='approved' AND v.PackageRevisionId=p.PackageRevisionId AND v.ContentHash=p.ContentHash
                WHERE a.ActivationId=@activation AND a.SiteId=@site AND a.MachineId=@machine AND a.ActivationGeneration=@generation
                    AND a.PackageRevisionId=@revision AND a.ContentHash=@hash AND a.ReviewId=@review
                    AND a.RuntimeIdentityId=@runtime AND a.AssignmentId=@assignment AND a.AssignmentGeneration=@assignmentGeneration AND a.CredentialGeneration=@credentialGeneration;
                """))
            {
                Add(q,("@activation",request.ActivationId),("@site",request.SiteId),("@machine",request.MachineId),("@generation",request.ActivationGeneration),
                    ("@revision",request.PackageRevisionId),("@hash",hash),("@review",request.ApprovalReviewId),("@runtime",request.RuntimeIdentityId),("@assignment",request.AssignmentId),
                    ("@assignmentGeneration",request.AssignmentGeneration),("@credentialGeneration",request.CredentialGeneration));
                await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
                canonical=(byte[])r[0];from=r.GetDateTimeOffset(1);until=r.IsDBNull(2)?null:r.GetDateTimeOffset(2);
            }
            if(!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(canonical)))throw new SecurityDeniedException();
            using var document=JsonDocument.Parse(canonical);var root=document.RootElement;
            var machines=root.GetProperty("machines").EnumerateArray().Where(machine=>machine.GetProperty("machineId").GetGuid()==request.MachineId).ToArray();
            if(root.GetProperty("siteId").GetGuid()!=request.SiteId||root.GetProperty("revisionId").GetGuid()!=request.PackageRevisionId||machines.Length!=1)throw new SecurityDeniedException();
            var machine=machines[0];
            if(!Contains(machine.GetProperty("effectiveFrom").GetDateTimeOffset(),OptionalInstant(machine,"effectiveTo"),from,until))throw new SecurityDeniedException();
            await ConfigurationLocks(c,t,root,installationId,ct,request.MachineId);
            var final=await State(c,t,ct);actor=await ValidateRuntimeMachineAuthority(c,t,evidence,request.SiteId,request.MachineId,final.Now,ct);MatchAcknowledgingRuntime(request,actor);
            if(from>final.Now||until is {} expired&&expired<=final.Now)throw new SecurityDeniedException();
            await ValidateActivationPrerequisites(c,t,new(request.OperationId,request.ActivationId,request.SiteId,request.MachineId,request.PackageRevisionId,hash,request.ApprovalReviewId,
                request.RuntimeIdentityId,request.AssignmentId,request.AssignmentGeneration,request.CredentialGeneration,from,until,0,null,0,request.Reason),machine,from,until,final.Now,ct);
            await ValidatePinnedPolicies(c,t,root,machine,request.SiteId,final.Now,ct);
            await using(var q=Command(c,t,"""
                SELECT 1 FROM configuration.MachineActivationRequestHead WHERE SiteId=@site AND MachineId=@machine AND ActivationId=@activation AND ActivationGeneration=@generation
                    AND NOT EXISTS(SELECT 1 FROM configuration.ActivationRequestRevocation WHERE ActivationId=@activation);
                """))
            {
                Add(q,("@site",request.SiteId),("@machine",request.MachineId),("@activation",request.ActivationId),("@generation",request.ActivationGeneration));
                if(await q.ExecuteScalarAsync(ct) is null)throw new SecurityDeniedException();
            }
            await using(var q=Command(c,t,"""
                SELECT CASE WHEN EXISTS(SELECT 1 FROM configuration.RuntimeActivationAcknowledgement WHERE ActivationId=@activation)
                    OR EXISTS(SELECT 1 FROM configuration.RuntimeActivationAcknowledgement WHERE AcknowledgementId=@ack)
                    OR EXISTS(SELECT 1 FROM configuration.MachineAcknowledgementHead WHERE SiteId=@site AND MachineId=@machine AND ActivationGeneration>=@generation)
                    THEN 1 ELSE 0 END;
                """))
            {
                Add(q,("@activation",request.ActivationId),("@ack",request.AcknowledgementId),("@site",request.SiteId),("@machine",request.MachineId),("@generation",request.ActivationGeneration));
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityConflictException();
            }
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,acknowledgementId=request.AcknowledgementId,activationId=request.ActivationId,
                activationGeneration=Decimal(request.ActivationGeneration),appliedFrom=final.Now,effectiveTo=until,status="runtime-acknowledgement-recorded-not-connectivity"});
            await Exec(c,t,"""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                    VALUES(@op,@runtime,@bytes,@requestHash,'M',@site,@machine,@result);
                INSERT configuration.RuntimeActivationAcknowledgement(AcknowledgementId,SiteId,MachineId,ActivationId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,
                    RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration,CertificateHash,AppliedFrom,EffectiveTo,OperationId)
                    VALUES(@ack,@site,@machine,@activation,@generation,@revision,@hash,@review,@runtime,@assignment,@assignmentGeneration,@credentialGeneration,@certificateHash,@now,@until,@op);
                UPDATE configuration.MachineAcknowledgementHead SET AcknowledgementId=@ack,ActivationGeneration=@generation WHERE SiteId=@site AND MachineId=@machine;
                IF @@ROWCOUNT=0 INSERT configuration.MachineAcknowledgementHead(SiteId,MachineId,AcknowledgementId,ActivationGeneration) VALUES(@site,@machine,@ack,@generation);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@audit,@op,@runtime,'runtime-acknowledge-activation',@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@op",request.OperationId),("@runtime",actor.RuntimeIdentityId),("@bytes",bytes),("@requestHash",SHA256.HashData(bytes)),("@site",request.SiteId),("@machine",request.MachineId),("@result",result),
                ("@ack",request.AcknowledgementId),("@activation",request.ActivationId),("@generation",request.ActivationGeneration),("@revision",request.PackageRevisionId),("@hash",hash),("@review",request.ApprovalReviewId),
                ("@assignment",request.AssignmentId),("@assignmentGeneration",request.AssignmentGeneration),("@credentialGeneration",request.CredentialGeneration),("@certificateHash",certificateHash),("@now",final.Now),
                ("@until",(object?)until??DBNull.Value),("@audit",Guid.NewGuid()),("@reason",request.Reason));
            return result;
        },ct);
    }

    public Task<byte[]> ReadRuntimeAcknowledgementOperationAsync(RuntimeMtlsChannel channel,Guid siteId,Guid machineId,Guid operationId,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Exact operation required.");
        return ReadRuntimeMachineAsync(channel,siteId,machineId,async(c,t,authority)=>
        {
            var completed=await FindOperation(c,t,operationId,ct)??throw new SecurityDeniedException();
            if(completed.Actor!=authority.RuntimeIdentityId||completed.Scope!=AuthorizationScope.Machine(siteId,machineId))throw new SecurityDeniedException();
            using var parsed=JsonDocument.Parse(completed.Request);var root=parsed.RootElement;
            if(!root.TryGetProperty("action",out var action)||action.GetString()!="acknowledge-activation"||
                root.GetProperty("assignmentId").GetGuid()!=authority.AssignmentId||root.GetProperty("assignmentGeneration").GetString()!=Decimal(authority.AssignmentGeneration)||
                root.GetProperty("credentialGeneration").GetString()!=Decimal(authority.CredentialGeneration))throw new SecurityDeniedException();return completed.Result;
        },ct);
    }
    private static void MatchAcknowledgingRuntime(ActivationAcknowledgementRequest request,RuntimeMachineAuthority authority)
    {
        if(request.RuntimeIdentityId!=authority.RuntimeIdentityId||request.AssignmentId!=authority.AssignmentId||
            request.AssignmentGeneration!=authority.AssignmentGeneration||request.CredentialGeneration!=authority.CredentialGeneration)throw new SecurityDeniedException();
    }
}
