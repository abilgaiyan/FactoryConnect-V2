using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record PermissionGrantRequest(Guid OperationId,Guid GrantId,Guid GranteeId,Guid ParentGrantId,
    string Permission,AuthorizationScope Scope,bool UseAllowed,bool CanDelegate,
    DateTimeOffset EffectiveFrom,DateTimeOffset ExpiresAt,long ExpectedEpoch,string Reason);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Appends a child grant for an existing human. Requires both access management and the exact
    /// live delegation parent. Does not enroll users, create root grants, or enable HTTP writes.</summary>
    public Task<byte[]> GrantPermissionAsync(byte[] sessionHash,PermissionGrantRequest request,CancellationToken ct=default)
    {
        if(request.OperationId==Guid.Empty||request.GrantId==Guid.Empty||request.GranteeId==Guid.Empty||
            request.ParentGrantId==Guid.Empty||request.GrantId==request.ParentGrantId||request.ExpectedEpoch<0||
            request.ExpiresAt<=request.EffectiveFrom||!request.UseAllowed&&!request.CanDelegate||
            string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>500)
            throw new ArgumentException("Invalid bounded delegated grant.");
        _=new UTF8Encoding(false,true).GetBytes(request.Reason);
        var token=sessionHash.ToArray();var scope=request.Scope??throw new ArgumentException("Explicit grant scope is required.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="grant-permission",request.GrantId,request.GranteeId,
            request.ParentGrantId,request.Permission,scope.Kind,scope.SiteId,scope.MachineId,request.UseAllowed,request.CanDelegate,
            effectiveFrom=request.EffectiveFrom.ToUniversalTime(),expiresAt=request.ExpiresAt.ToUniversalTime(),request.ExpectedEpoch,request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"security.access.manage",scope,state.Now,ct);
            // Completed results need current access management authority, but must not reapply the old grant.
            var completed=await FindOperation(c,t,request.OperationId,ct);
            if(completed is not null)
            {
                if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();
                return completed.Result;
            }
            if(state.Epoch!=request.ExpectedEpoch)throw new SecurityConflictException();
            if(actor==request.GranteeId||request.ExpiresAt<=state.Now)throw new SecurityDeniedException();
            var depth=await Authorize(c,t,actor,request.Permission,scope,state.Now,ct,true,request.ParentGrantId);
            if(depth>=64)throw new SecurityDeniedException();
            // Authorize validates the complete live ancestry. This check additionally bounds the new interval.
            await using(var q=Command(c,t,"""
                SELECT 1 FROM security.PermissionGrant p JOIN security.Principal h ON h.PrincipalId=@grantee
                WHERE p.GrantId=@parent AND h.Kind='H'
                    AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation r WHERE r.PrincipalId=h.PrincipalId)
                    AND p.EffectiveFrom<=@from AND p.ExpiresAt>=@until;
                """))
            {
                Add(q,("@grantee",request.GranteeId),("@parent",request.ParentGrantId),
                    ("@from",request.EffectiveFrom.ToUniversalTime()),("@until",request.ExpiresAt.ToUniversalTime()));
                if(await q.ExecuteScalarAsync(ct) is null)throw new SecurityDeniedException();
            }
            await using(var q=Command(c,t,"SELECT 1 FROM security.PermissionGrant WHERE GrantId=@grant"))
            {Add(q,("@grant",request.GrantId));if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,grantId=request.GrantId,status="granted"});
            await Exec(c,t,"""
                INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,ParentGrantId,Permission,ScopeKind,SiteId,MachineId,
                    UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                VALUES(@grant,@grantee,@actor,@parent,@permission,@kind,@site,@machine,@use,@delegate,@from,@until);
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                VALUES(@op,@actor,@bytes,@hash,@kind,@site,@machine,@result);
                INSERT security.GrantIssuance(GrantId,OperationId,ActorPrincipalId) VALUES(@grant,@op,@actor);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                VALUES(@audit,@op,@actor,'grant-permission',@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@grant",request.GrantId),("@grantee",request.GranteeId),("@actor",actor),("@parent",request.ParentGrantId),
                ("@permission",request.Permission),("@kind",scope.Kind),("@site",(object?)scope.SiteId??DBNull.Value),
                ("@machine",(object?)scope.MachineId??DBNull.Value),("@use",request.UseAllowed),("@delegate",request.CanDelegate),
                ("@from",request.EffectiveFrom.ToUniversalTime()),("@until",request.ExpiresAt.ToUniversalTime()),
                ("@op",request.OperationId),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),("@result",result),
                ("@audit",Guid.NewGuid()),("@reason",request.Reason));
            return result;
        },ct);
    }
}
