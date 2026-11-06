using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    /// <summary>One-time recovery trust provisioning. Current installation administration plus two
    /// bootstrap custodians are required; this command never executes administrator recovery.</summary>
    public Task<byte[]> ProvisionRecoveryTrustAsync(byte[] sessionHash,RecoveryTrustFile file,SignedRecoveryTrustProvision request,CancellationToken ct=default)
    {
        var trust=new RecoveryTrustManifest(file);
        var p=request.Payload with{Nonce=request.Payload.Nonce.ToArray(),BootstrapTrustHash=request.Payload.BootstrapTrustHash.ToArray(),RecoveryTrustHash=request.Payload.RecoveryTrustHash.ToArray()};
        var proofs=request.Signatures.Select(s=>s with{Signature=s.Signature.ToArray()}).ToArray();
        var recoveryProofs=request.RecoverySignatures.Select(s=>s with{Signature=s.Signature.ToArray()}).ToArray();
        var canonical=RecoveryTrustManifest.Canonicalize(p);var token=sessionHash.ToArray();
        return Transaction(p.OperationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"security.access.manage",AuthorizationScope.Installation,state.Now,ct);
            if(p.InstallationId!=installationId||p.ActorPrincipalId!=actor)throw new SecurityDeniedException();
            BootstrapTrustManifest bootstrap;
            await using(var q=Command(c,t,"SELECT ManifestBytes FROM security.BootstrapTrust WHERE Singleton=1"))
            {
                var stored=(byte[]?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();
                var input=JsonSerializer.Deserialize<BootstrapTrustFile>(stored,new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new SecurityDeniedException();
                bootstrap=new(input);
                if(!stored.AsSpan().SequenceEqual(bootstrap.CanonicalBytes))throw new SecurityDeniedException();
            }
            trust.VerifyProvision(bootstrap,new(p,proofs,recoveryProofs),state.Now);
            var encoding=new UTF8Encoding(false,true);
            await using(var q=Command(c,t,"SELECT IssuerBytes,SubjectBytes FROM security.Principal WHERE PrincipalId=@actor"))
            {
                Add(q,("@actor",actor));await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)||!((byte[])r[0]).AsSpan().SequenceEqual(encoding.GetBytes(p.ActorIssuer))||
                    !((byte[])r[1]).AsSpan().SequenceEqual(encoding.GetBytes(p.ActorSubject)))throw new SecurityDeniedException();
            }
            await using(var q=Command(c,t,"SELECT COUNT(*) FROM security.BootstrapCustodianRevocation WHERE KeyId IN (@first,@second)"))
            {
                Add(q,("@first",p.ApproverKeyId),("@second",p.ExecutorKeyId));
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();
            }
            // Fresh custody and session authorization are required even for completed retries.
            var completed=await FindOperation(c,t,p.OperationId,ct);
            if(completed is not null)
            {
                if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(canonical))throw new SecurityConflictException();
                return completed.Result;
            }
            if(state.Epoch!=p.ExpectedEpoch)throw new SecurityConflictException();
            await using(var q=Command(c,t,"SELECT (SELECT COUNT(*) FROM security.RecoveryTrustProvisioning)+(SELECT COUNT(*) FROM security.RecoveryTrustRevision)+(SELECT COUNT(*) FROM security.RecoveryTrustState)+(SELECT COUNT(*) FROM security.RecoveryCustodianKey)"))
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=p.OperationId,trustId=p.TrustId,generation=1,status="recovery-trust-provisioned"});
            await Exec(c,t,"""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                    VALUES(@op,@actor,@bytes,@hash,'I',@result);
                INSERT security.RecoveryTrustRevision(TrustId,Generation,ManifestBytes,ManifestHash,OperationId)
                    VALUES(@trust,1,@manifest,@manifestHash,@op);
                INSERT security.RecoveryTrustState(Singleton,CurrentTrustId) VALUES(1,@trust);
                """,ct,("@op",p.OperationId),("@actor",actor),("@bytes",canonical),("@hash",SHA256.HashData(canonical)),
                ("@result",result),("@trust",p.TrustId),("@manifest",trust.CanonicalBytes),("@manifestHash",trust.Hash));
            foreach(var key in trust.Keys)
                await Exec(c,t,"""
                    INSERT security.RecoveryCustodianKey(KeyId,TrustId,CustodianId,IdentityHash,PublicKeySpki,PublicKeyHash,ValidFrom,ExpiresAt)
                    VALUES(@key,@trust,@custodian,@identity,@spki,@hash,@from,@until);
                    """,ct,("@key",key.KeyId),("@trust",p.TrustId),("@custodian",key.CustodianId),
                    ("@identity",SHA256.HashData(BootstrapTrustManifest.IdentityBytes(key.Issuer,key.Subject))),("@spki",key.PublicKeySpki),
                    ("@hash",SHA256.HashData(key.PublicKeySpki)),("@from",key.ValidFrom),("@until",key.ExpiresAt));
            await Exec(c,t,"""
                INSERT security.RecoveryTrustProvisioning(Singleton,TrustId,OperationId,NonceHash,ApproverKeyId,ExecutorKeyId,ApproverSignature,ExecutorSignature,RecoveryFirstKeyId,RecoverySecondKeyId,RecoveryFirstSignature,RecoverySecondSignature)
                    VALUES(1,@trust,@op,@nonce,@first,@second,@firstProof,@secondProof,@recoveryFirst,@recoverySecond,@recoveryFirstProof,@recoverySecondProof);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                    VALUES(@audit,@op,@actor,'provision-recovery-trust',@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@trust",p.TrustId),("@op",p.OperationId),("@nonce",SHA256.HashData(p.Nonce)),("@first",p.ApproverKeyId),
                ("@second",p.ExecutorKeyId),("@firstProof",proofs.Single(s=>s.KeyId==p.ApproverKeyId).Signature),
                ("@secondProof",proofs.Single(s=>s.KeyId==p.ExecutorKeyId).Signature),
                ("@recoveryFirst",trust.Keys[0].KeyId),("@recoverySecond",trust.Keys[1].KeyId),
                ("@recoveryFirstProof",recoveryProofs.Single(s=>s.KeyId==trust.Keys[0].KeyId).Signature),
                ("@recoverySecondProof",recoveryProofs.Single(s=>s.KeyId==trust.Keys[1].KeyId).Signature),("@audit",Guid.NewGuid()),("@actor",actor),("@reason",p.Reason));
            return result;
        },ct);
    }
}
