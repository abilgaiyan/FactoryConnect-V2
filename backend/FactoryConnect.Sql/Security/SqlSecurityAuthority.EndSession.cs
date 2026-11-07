using System.Security.Cryptography;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    /// <summary>End only the caller's session, with the same exclusive revocation gate and atomic audit.</summary>
    public Task EndOwnSessionAsync(byte[] sessionHash, CancellationToken ct=default)
    {
        var token=sessionHash.ToArray();
        if(token.Length!=32)throw new SecurityDeniedException();
        var op=Guid.NewGuid();
        return Transaction(op,true,async(c,t)=>
        {
            var state=await State(c,t,ct);
            var actor=await Session(c,t,token,state.Now,ct);
            var request=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="end-own-session"});
            var result=JsonSerializer.SerializeToUtf8Bytes(new{status="revoked"});
            await Exec(c,t,"""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                    VALUES(@op,@actor,@request,@hash,'I',@result);
                INSERT security.SessionRevocation(TokenHash,OperationId) VALUES(@token,@op);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                    VALUES(@audit,@op,@actor,'end-own-session',N'Operator ended own session');
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@op",op),("@actor",actor),("@request",request),("@hash",SHA256.HashData(request)),
                ("@result",result),("@token",token),("@audit",Guid.NewGuid()));
            return true;
        },ct);
    }
}
