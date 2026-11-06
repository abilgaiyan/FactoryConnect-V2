using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record HumanEnrollmentRequest(Guid OperationId,Guid PrincipalId,string Issuer,string Subject,
    long ExpectedEpoch,string Reason);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Records an intended human identity under the installation's pinned issuer.
    /// Enrollment authenticates nobody and grants no permissions.</summary>
    public Task<byte[]> EnrollHumanAsync(byte[] sessionHash,HumanEnrollmentRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.PrincipalId,request.ExpectedEpoch,request.Reason);
        _=BootstrapTrustManifest.IdentityBytes(request.Issuer,request.Subject);
        if(!Uri.TryCreate(request.Issuer,UriKind.Absolute,out var uri)||uri.Scheme!="https"||
            uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Query.Length!=0)
            throw new ArgumentException("An exact HTTPS identity issuer is required.");
        var utf8=new UTF8Encoding(false,true);var issuer=utf8.GetBytes(request.Issuer);var subject=utf8.GetBytes(request.Subject);
        var token=sessionHash.ToArray();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="enroll-human",request.PrincipalId,request.Issuer,
            request.Subject,request.ExpectedEpoch,request.Reason});
        if(bytes.Length>16384)throw new ArgumentException("Encoded enrollment request exceeds the operation limit.");
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"security.access.manage",AuthorizationScope.Installation,state.Now,ct);
            var completed=await FindOperation(c,t,request.OperationId,ct);
            if(completed is not null)
            {
                if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();
                return completed.Result;
            }
            if(state.Epoch!=request.ExpectedEpoch)throw new SecurityConflictException();
            // The committed bootstrap manifest is immutable trust, not a caller-supplied issuer allowlist.
            await using(var q=Command(c,t,"SELECT ManifestBytes FROM security.BootstrapTrust WHERE Singleton=1"))
            {
                var manifest=(byte[]?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();
                using var document=JsonDocument.Parse(manifest);
                if(document.RootElement.GetProperty("allowedAdministratorIssuer").GetString()!=request.Issuer)
                    throw new SecurityDeniedException();
            }
            await using(var q=Command(c,t,"""
                SELECT 1 FROM security.Principal WHERE PrincipalId=@principal OR
                    (IssuerBytes=@issuer AND DATALENGTH(IssuerBytes)=DATALENGTH(@issuer)
                    AND SubjectBytes=@subject AND DATALENGTH(SubjectBytes)=DATALENGTH(@subject));
                """))
            {
                Add(q,("@principal",request.PrincipalId),("@issuer",issuer),("@subject",subject));
                if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();
            }
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,principalId=request.PrincipalId,status="enrolled"});
            await Exec(c,t,"""
                INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@principal,'H',@issuer,@subject);
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                    VALUES(@op,@actor,@bytes,@hash,'I',@result);
                INSERT security.HumanIdentityEnrollment(PrincipalId,OperationId,ActorPrincipalId) VALUES(@principal,@op,@actor);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                    VALUES(@audit,@op,@actor,'enroll-human',@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@principal",request.PrincipalId),("@issuer",issuer),("@subject",subject),("@op",request.OperationId),
                ("@actor",actor),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),("@result",result),("@audit",Guid.NewGuid()),("@reason",request.Reason));
            return result;
        },ct);
    }

    /// <summary>Permanently revokes an existing human and its dependent authority. Caller must retain an
    /// independent installation-management chain; ordinary revocation cannot consume its own authority.</summary>
    public Task<byte[]> RevokeHumanAsync(byte[] sessionHash,Guid operationId,Guid principalId,long expectedEpoch,
        string reason,CancellationToken ct=default)
    {
        ValidateIdentityChange(operationId,principalId,expectedEpoch,reason);
        var token=sessionHash.ToArray();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="revoke-human",principalId,expectedEpoch,reason});
        return Transaction(operationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"security.access.manage",AuthorizationScope.Installation,state.Now,ct);
            var completed=await FindOperation(c,t,operationId,ct);
            if(completed is not null)
            {
                if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();
                return completed.Result;
            }
            if(state.Epoch!=expectedEpoch)throw new SecurityConflictException();
            if(actor==principalId)throw new SecurityDeniedException();
            await Authorize(c,t,actor,"security.access.manage",AuthorizationScope.Installation,state.Now,ct,
                excludedPrincipal:principalId);
            await using(var q=Command(c,t,"SELECT Kind FROM security.Principal WHERE PrincipalId=@principal"))
            {Add(q,("@principal",principalId));if((string?)(await q.ExecuteScalarAsync(ct))!="H")throw new SecurityDeniedException();}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,principalId,status="revoked"});
            await Exec(c,t,"""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                    VALUES(@op,@actor,@bytes,@hash,'I',@result);
                IF NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=@principal)
                    INSERT security.PrincipalRevocation(PrincipalId,OperationId) VALUES(@principal,@op);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                    VALUES(@audit,@op,@actor,'revoke-human',@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@principal",principalId),("@op",operationId),("@actor",actor),("@bytes",bytes),
                ("@hash",SHA256.HashData(bytes)),("@result",result),("@audit",Guid.NewGuid()),("@reason",reason));
            return result;
        },ct);
    }

    private static void ValidateIdentityChange(Guid operation,Guid principal,long epoch,string reason)
    {
        if(operation==Guid.Empty||principal==Guid.Empty||epoch<0||string.IsNullOrWhiteSpace(reason)||reason.Length>500)
            throw new ArgumentException("Explicit identity, operation, epoch and reason are required.");
        _=new UTF8Encoding(false,true).GetBytes(reason);
    }
}
