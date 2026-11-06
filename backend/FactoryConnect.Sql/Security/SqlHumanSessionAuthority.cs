using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

/// <summary>Output of a trusted server-side authentication adapter, never deserialized from a client request.</summary>
public sealed record VerifiedHumanIdentity(string Issuer,string Subject,string AssertionId,
    string AuthenticationContext,DateTimeOffset AuthenticatedAt,DateTimeOffset ExpiresAt);

/// <summary>The host must validate signature, issuer, audience, nonce/state, expiry and proof binding.
/// No permissive implementation is supplied or registered by the foundation.</summary>
public interface IHumanIdentityVerifier
{
    Task<VerifiedHumanIdentity> VerifyAsync(string assertion,CancellationToken cancellationToken);
}

public sealed record IssuedHumanSession(byte[] Token,Guid PrincipalId,DateTimeOffset ExpiresAt);

/// <summary>Consumes a verified one-use authentication proof for an existing human identity.
/// Returns a random bearer secret once; only its SHA-256 is persisted. No HTTP route is registered.</summary>
public sealed class SqlHumanSessionAuthority(string connectionString,Guid installationId,IHumanIdentityVerifier verifier)
{
    public async Task<IssuedHumanSession> IssueAsync(string assertion,CancellationToken ct=default)
    {
        if(installationId==Guid.Empty||string.IsNullOrWhiteSpace(assertion)||assertion.Length>65536)
            throw new ArgumentException("Explicit installation and bounded authentication assertion are required.");
        var identity=await verifier.VerifyAsync(assertion,ct)??throw new SecurityDeniedException();
        var encoding=new UTF8Encoding(false,true);
        var issuer=encoding.GetBytes(identity.Issuer);var subject=encoding.GetBytes(identity.Subject);
        if(!Uri.TryCreate(identity.Issuer,UriKind.Absolute,out var uri)||uri.Scheme!="https"||
            string.IsNullOrWhiteSpace(identity.Subject)||issuer.Length>2048||subject.Length>2048||
            string.IsNullOrWhiteSpace(identity.AssertionId)||encoding.GetByteCount(identity.AssertionId)>2048||
            string.IsNullOrWhiteSpace(identity.AuthenticationContext)||identity.AuthenticationContext.Length>200)
            throw new SecurityDeniedException();
        _=encoding.GetBytes(identity.AuthenticationContext);
        // Exact issuer-scoped proof identity; no collation/normalization-based matching.
        var proof=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{version=1,issuer=identity.Issuer,assertionId=identity.AssertionId}));
        var operation=Guid.NewGuid();var secret=RandomNumberGenerator.GetBytes(32);var tokenHash=SHA256.HashData(secret);
        try
        {
            await using var c=new SqlConnection(connectionString);await c.OpenAsync(ct);
            await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
            try
            {
                await Lock($"fc2:operation:{installationId:N}:{operation:N}","Exclusive");
                await Lock(SqlSecurityAuthority.GateResource(installationId),"Shared");
                // Serialize consumption of the same verified proof before testing uniqueness.
                await Lock($"fc2:login-proof:{installationId:N}:{Convert.ToHexString(proof)}","Exclusive");
                await using(var q=Command("SELECT InstallationId FROM platform.InstallationIdentity WHERE Singleton=1"))
                    if((Guid?)(await q.ExecuteScalarAsync(ct))!=installationId)throw new SecurityDeniedException();
                DateTimeOffset now;long epoch;
                await using(var q=Command("SELECT IsSealed,AuthorityEpoch,TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00') FROM security.InstallationState WHERE Singleton=1"))
                await using(var r=await q.ExecuteReaderAsync(ct))
                {
                    if(!await r.ReadAsync(ct)||r.GetBoolean(0))throw new SecurityDeniedException();
                    epoch=r.GetInt64(1);now=r.GetDateTimeOffset(2);
                }
                if(identity.AuthenticatedAt>now||identity.AuthenticatedAt<now.AddMinutes(-5)||identity.ExpiresAt<=now||identity.ExpiresAt<=identity.AuthenticatedAt)
                    throw new SecurityDeniedException();
                Guid principal;
                await using(var q=Command("""
                    SELECT p.PrincipalId FROM security.Principal p
                    WHERE p.Kind='H' AND p.IssuerBytes=@issuer AND DATALENGTH(p.IssuerBytes)=DATALENGTH(@issuer)
                        AND p.SubjectBytes=@subject AND DATALENGTH(p.SubjectBytes)=DATALENGTH(@subject)
                        AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation r WHERE r.PrincipalId=p.PrincipalId)
                    """))
                {
                    Add(q,("@issuer",issuer),("@subject",subject));
                    principal=(Guid?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();
                }
                await using(var q=Command("SELECT COUNT(*) FROM security.HumanSessionIssuance WHERE ProofHash=@proof"))
                {Add(q,("@proof",proof));if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();}
                var until=identity.ExpiresAt<now.AddHours(1)?identity.ExpiresAt:now.AddHours(1);
                var request=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="issue-human-session",proofHash=Convert.ToHexString(proof),principalId=principal});
                // Stored operation receipts never contain the bearer token or original assertion.
                var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=operation,principalId=principal,expiresAt=until,status="issued"});
                await using(var q=Command("""
                    INSERT security.SessionAuthority(TokenHash,PrincipalId,IssuedAt,ExpiresAt,AuthenticationContext,IssuedEpoch)
                        VALUES(@token,@principal,@now,@until,@context,@epoch);
                    INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                        VALUES(@op,@principal,@request,@hash,'I',@result);
                    INSERT security.HumanSessionIssuance(OperationId,ProofHash,TokenHash,AuthenticatedAt,ProofExpiresAt)
                        VALUES(@op,@proof,@token,@authenticated,@proofUntil);
                    INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                        VALUES(@audit,@op,@principal,'issue-human-session',N'Validated external identity proof');
                    """))
                {
                    Add(q,("@token",tokenHash),("@principal",principal),("@now",now),("@until",until),("@context",identity.AuthenticationContext),
                        ("@epoch",epoch),("@op",operation),("@request",request),("@hash",SHA256.HashData(request)),("@result",result),
                        ("@proof",proof),("@authenticated",identity.AuthenticatedAt),("@proofUntil",identity.ExpiresAt),("@audit",Guid.NewGuid()));
                    await q.ExecuteNonQueryAsync(ct);
                }
                await t.CommitAsync(ct);return new(secret,principal,until);
                SqlCommand Command(string sql)=>new(sql,c,t){CommandTimeout=30};
                async Task Lock(string resource,string mode)
                {
                    await using var q=Command("""
                        DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode=@mode,@LockOwner='Transaction',@LockTimeout=10000;
                        IF @r<0 THROW 51020,'Session issuance lock unavailable.',1;
                        """);Add(q,("@resource",resource),("@mode",mode));await q.ExecuteNonQueryAsync(ct);
                }
            }
            catch
            {try{await t.RollbackAsync(CancellationToken.None);}catch(Exception e)when(e is SqlException or InvalidOperationException){}throw;}
        }
        catch{CryptographicOperations.ZeroMemory(secret);throw;}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {
        foreach(var(name,value)in values)
            if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;
            else if(value is DateTimeOffset time)q.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=time;
            else q.Parameters.AddWithValue(name,value);
    }
}
