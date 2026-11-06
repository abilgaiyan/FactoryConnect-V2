using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    public Task<byte[]> ChangeRuntimeCredentialAsync(byte[] sessionHash,SignedRuntimeCredential request,CancellationToken ct=default)
        => ChangeRuntimeCredentialCoreAsync(sessionHash,request,null,ct);

    /// <summary>Controls exact X.509/CA registration with the next signing-key generation; not TLS authentication.</summary>
    public Task<byte[]> EnrollRuntimeCertificateAsync(byte[] sessionHash,SignedRuntimeCertificate request,CancellationToken ct=default)
        => ChangeRuntimeCredentialCoreAsync(sessionHash,new(request.Payload,request.Signatures,request.PossessionSignature),
            new(request.CertificateDer.ToArray(),request.TrustAnchorDer.ToArray()),ct);

    private Task<byte[]> ChangeRuntimeCredentialCoreAsync(byte[] sessionHash,SignedRuntimeCredential request,RuntimeCertificateRegistration? certificate,CancellationToken ct)
    {
        var p=request.Payload with{Nonce=request.Payload.Nonce.ToArray(),BootstrapTrustHash=request.Payload.BootstrapTrustHash.ToArray(),PublicKeySpki=request.Payload.PublicKeySpki.ToArray(),
            ValidFrom=request.Payload.ValidFrom.ToUniversalTime(),CredentialExpiresAt=request.Payload.CredentialExpiresAt.ToUniversalTime()};
        var signed=new SignedRuntimeCredential(p,request.Signatures.Select(s=>s with{Signature=s.Signature.ToArray()}).ToArray(),request.PossessionSignature.ToArray());
        var bytes=certificate is null?RuntimeCredentialProof.Canonicalize(p):RuntimeCertificateProfile.Canonicalize(p,certificate);var token=sessionHash.ToArray();
        return Transaction(p.OperationId,true,async(c,t)=>
        {
            if(certificate is not null)Activity.Current?.SetTag("security.certificate.registration",true);
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"runtime.credential.manage",AuthorizationScope.Installation,state.Now,ct);
            if(p.InstallationId!=installationId||p.ActorPrincipalId!=actor)throw new SecurityDeniedException();
            BootstrapTrustManifest trust;
            await using(var q=Command(c,t,"SELECT ManifestBytes FROM security.BootstrapTrust WHERE Singleton=1"))
            {
                var stored=(byte[]?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();
                trust=new(JsonSerializer.Deserialize<BootstrapTrustFile>(stored,new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new SecurityDeniedException());
                if(!stored.AsSpan().SequenceEqual(trust.CanonicalBytes))throw new SecurityDeniedException();
            }
            RuntimeCredentialProof.VerifyAuthorizedBytes(trust,signed,bytes,state.Now);
            if(certificate is not null)RuntimeCertificateProfile.ValidateRegistration(p,certificate,state.Now);
            var utf8=new UTF8Encoding(false,true);
            await using(var q=Command(c,t,"SELECT IssuerBytes,SubjectBytes FROM security.Principal WHERE PrincipalId=@actor"))
            {
                Add(q,("@actor",actor));await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)||!((byte[])r[0]).AsSpan().SequenceEqual(utf8.GetBytes(p.ActorIssuer))||!((byte[])r[1]).AsSpan().SequenceEqual(utf8.GetBytes(p.ActorSubject)))throw new SecurityDeniedException();
            }
            await using(var q=Command(c,t,"SELECT COUNT(*) FROM security.BootstrapCustodianRevocation WHERE KeyId IN (@a,@b)"))
            {Add(q,("@a",p.ApproverKeyId),("@b",p.ExecutorKeyId));if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();}
            // Security gate is exclusive across every assignment of this runtime; no machine lock is sufficient here.
            // Refresh SQL time after crypto/reads, including on exact completed retries.
            state=await State(c,t,ct);actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"runtime.credential.manage",AuthorizationScope.Installation,state.Now,ct);
            if(p.ExpiresAt<=state.Now||p.Action!="revoke"&&p.CredentialExpiresAt<=state.Now)throw new SecurityDeniedException();
            if(certificate is not null&&RuntimeCertificateProfile.CertificateInterval(certificate).Until<=state.Now)throw new SecurityDeniedException();
            var completed=await FindOperation(c,t,p.OperationId,ct);
            if(completed is not null)
            {if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            if(state.Epoch!=p.ExpectedEpoch)throw new SecurityConflictException();
            long current=0;
            await using(var q=Command(c,t,"""
                SELECT s.CredentialGeneration,p.Kind,p.IssuerBytes,p.SubjectBytes,
                    CASE WHEN v.PrincipalId IS NULL THEN 0 ELSE 1 END
                FROM security.Principal p LEFT JOIN security.RuntimeCredentialState s ON s.RuntimeIdentityId=p.PrincipalId
                LEFT JOIN security.PrincipalRevocation v ON v.PrincipalId=p.PrincipalId WHERE p.PrincipalId=@runtime
                """))
            {
                Add(q,("@runtime",p.RuntimeIdentityId));await using var r=await q.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    if(p.Action=="enroll"||r.GetString(1)!="R"||r.GetInt32(4)!=0||
                        !((byte[])r[2]).AsSpan().SequenceEqual(utf8.GetBytes(p.RuntimeIssuer))||!((byte[])r[3]).AsSpan().SequenceEqual(utf8.GetBytes(p.RuntimeSubject))||r.IsDBNull(0))throw new SecurityDeniedException();
                    current=r.GetInt64(0);
                }
                else if(p.Action!="enroll")throw new SecurityDeniedException();
            }
            if(current!=p.ExpectedGeneration)throw new SecurityConflictException();
            await using(var q=Command(c,t,"SELECT COUNT(*) FROM security.RuntimeCredentialCeremony WHERE NonceHash=@nonce"))
            {Add(q,("@nonce",SHA256.HashData(p.Nonce)));if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityConflictException();}
            if(p.Action!="revoke")
            {
                await using var q=Command(c,t,"""
                    SELECT (SELECT COUNT(*) FROM security.RuntimeCredentialPublicKey WHERE PublicKeyHash=@hash)
                         + (SELECT COUNT(*) FROM security.RecoveryCustodianKey WHERE PublicKeyHash=@hash)
                         + (SELECT COUNT(*) FROM security.RuntimeCredentialGeneration WHERE CredentialBindingHash=@hash)
                         + (SELECT CASE WHEN EXISTS(SELECT 1 FROM security.RuntimeCertificateBinding WHERE AnchorPublicKeyHash=@hash) THEN 1 ELSE 0 END)
                    """);Add(q,("@hash",SHA256.HashData(p.PublicKeySpki)));
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();
            }
            else
            {
                await using var q=Command(c,t,"SELECT COUNT(*) FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=@runtime AND CredentialGeneration=@generation");
                Add(q,("@runtime",p.RuntimeIdentityId),("@generation",current));if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityConflictException();
            }
            if(certificate is not null)
            {
                await using var q=Command(c,t,"""
                    SELECT (SELECT COUNT(*) FROM security.RuntimeCredentialPublicKey WHERE PublicKeyHash=@anchorKey)
                         + (SELECT COUNT(*) FROM security.BootstrapCustodianKey WHERE PublicKeyHash=@anchorKey)
                         + (SELECT COUNT(*) FROM security.RecoveryCustodianKey WHERE PublicKeyHash=@anchorKey)
                    """);Add(q,("@anchorKey",RuntimeCertificateProfile.AnchorKeyHash(certificate)));
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();
            }
            var generation=p.Action=="revoke"?current:current+1;
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=p.OperationId,runtimeIdentityId=p.RuntimeIdentityId,credentialGeneration=generation.ToString(System.Globalization.CultureInfo.InvariantCulture),status=p.Action=="revoke"?"credential-revoked-not-runtime-stop":certificate is null?"credential-enrolled-not-runtime-acknowledgement":"certificate-registered-not-runtime-authentication"});
            await Exec(c,t,"""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                VALUES(@op,@actor,@bytes,@hash,'I',@result);
                """,ct,("@op",p.OperationId),("@actor",actor),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),("@result",result));
            if(p.Action=="enroll")await Exec(c,t,"""
                INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@runtime,'R',@issuer,@subject);
                INSERT security.RuntimeIdentity(RuntimeIdentityId,PrincipalKind,OperationId) VALUES(@runtime,'R',@op);
                """,ct,("@runtime",p.RuntimeIdentityId),("@issuer",utf8.GetBytes(p.RuntimeIssuer)),("@subject",utf8.GetBytes(p.RuntimeSubject)),("@op",p.OperationId));
            if(current>0)await Exec(c,t,"""
                IF NOT EXISTS(SELECT 1 FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=@runtime AND CredentialGeneration=@old)
                INSERT security.RuntimeCredentialRevocation(RuntimeIdentityId,CredentialGeneration,OperationId) VALUES(@runtime,@old,@op);
                """,ct,("@runtime",p.RuntimeIdentityId),("@old",current),("@op",p.OperationId));
            if(p.Action!="revoke")await Exec(c,t,"""
                INSERT security.RuntimeCredentialGeneration(RuntimeIdentityId,CredentialGeneration,CredentialBindingHash,ValidFrom,ExpiresAt,OperationId)
                VALUES(@runtime,@generation,@hash,@from,@until,@op);
                INSERT security.RuntimeCredentialPublicKey(RuntimeIdentityId,CredentialGeneration,PublicKeySpki,PublicKeyHash,OperationId)
                VALUES(@runtime,@generation,@spki,@hash,@op);
                UPDATE security.RuntimeCredentialState SET CredentialGeneration=@generation WHERE RuntimeIdentityId=@runtime;
                IF @@ROWCOUNT=0 INSERT security.RuntimeCredentialState VALUES(@runtime,@generation);
                """,ct,("@runtime",p.RuntimeIdentityId),("@generation",generation),("@hash",SHA256.HashData(p.PublicKeySpki)),("@from",p.ValidFrom),("@until",p.CredentialExpiresAt),("@op",p.OperationId),("@spki",p.PublicKeySpki));
            await Exec(c,t,"""
                INSERT security.RuntimeCredentialCeremony(OperationId,RuntimeIdentityId,CredentialGeneration,NonceHash,ApproverKeyId,ExecutorKeyId,ApproverSignature,ExecutorSignature,PossessionSignature)
                VALUES(@op,@runtime,@generation,@nonce,@first,@second,@firstProof,@secondProof,@possession);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@audit,@op,@actor,@action,@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@op",p.OperationId),("@runtime",p.RuntimeIdentityId),("@generation",generation),("@nonce",SHA256.HashData(p.Nonce)),("@first",p.ApproverKeyId),("@second",p.ExecutorKeyId),
                ("@firstProof",signed.Signatures.Single(s=>s.KeyId==p.ApproverKeyId).Signature),("@secondProof",signed.Signatures.Single(s=>s.KeyId==p.ExecutorKeyId).Signature),("@possession",signed.PossessionSignature),
                ("@audit",Guid.NewGuid()),("@actor",actor),("@action","runtime-credential-"+p.Action),("@reason",p.Reason));
            if(certificate is not null)
            {
                var interval=RuntimeCertificateProfile.CertificateInterval(certificate);
                await Exec(c,t,"""
                    INSERT security.RuntimeCertificateBinding(RuntimeIdentityId,CredentialGeneration,CertificateDer,CertificateHash,TrustAnchorDer,TrustAnchorHash,AnchorPublicKeyHash,ValidFrom,ExpiresAt,OperationId)
                    VALUES(@runtime,@generation,@cert,@certHash,@anchor,@anchorHash,@anchorKeyHash,@from,@until,@op);
                    """,ct,("@runtime",p.RuntimeIdentityId),("@generation",generation),("@cert",certificate.CertificateDer),("@certHash",SHA256.HashData(certificate.CertificateDer)),
                    ("@anchor",certificate.TrustAnchorDer),("@anchorHash",SHA256.HashData(certificate.TrustAnchorDer)),("@anchorKeyHash",RuntimeCertificateProfile.AnchorKeyHash(certificate)),("@from",interval.From),("@until",interval.Until),("@op",p.OperationId));
            }
            return result;
        },ct);
    }
}
