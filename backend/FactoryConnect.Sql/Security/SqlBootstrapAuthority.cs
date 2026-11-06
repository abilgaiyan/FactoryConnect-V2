using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

/// <summary>Explicit offline initial-bootstrap authority. Never used as a sign-in or recovery endpoint.</summary>
public sealed class SqlBootstrapAuthority
{
    private readonly InstallationTarget target;
    private readonly BootstrapTrustManifest trust;
    private static readonly string[] Permissions=["security.access.manage","configuration.read","configuration.edit","configuration.submit",
        "configuration.review","configuration.activate","commissioning.read","audit.read","machine.suspend","machine.enable","policy.revoke","runtime.assign","runtime.credential.manage"];

    public SqlBootstrapAuthority(InstallationTarget target,BootstrapTrustManifest trust)
    {
        var connection=new SqlConnectionStringBuilder(target.ConnectionString);
        if(target.InitializeEmpty||target.InstallationId==Guid.Empty||target.InstallationId!=trust.InstallationId||
            string.IsNullOrWhiteSpace(target.Database)||string.IsNullOrWhiteSpace(target.ExpectedServer)||
            connection.InitialCatalog!=target.Database||!string.IsNullOrEmpty(connection.AttachDBFilename)||
            new[]{"master","model","tempdb","msdb","FactoryConnect"}.Contains(target.Database,StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Explicit existing V2 target and installation trust are required.");
        this.target=target;this.trust=trust;
    }

    public async Task<byte[]> ApplyAsync(SignedBootstrapRequest request,CancellationToken ct=default)
    {
        var p=request.Payload with{Nonce=request.Payload.Nonce.ToArray(),TrustManifestHash=request.Payload.TrustManifestHash.ToArray()};
        var proofs=request.Signatures.Select(s=>s with{Signature=s.Signature.ToArray()}).ToArray();
        var canonical=BootstrapTrustManifest.Canonicalize(p);
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync(ct);
        await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try
        {
            await Lock($"fc2:operation:{target.InstallationId:N}:{p.OperationId:N}");
            await Lock(SqlSecurityAuthority.GateResource(target.InstallationId));
            await using(var identity=Command("SELECT InstallationId,Product,CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) FROM platform.InstallationIdentity WHERE Singleton=1"))
            await using(var r=await identity.ExecuteReaderAsync(ct))
                if(!await r.ReadAsync(ct)||r.GetGuid(0)!=target.InstallationId||r.GetString(1)!="FactoryConnect-V2"||r.GetString(2)!=target.ExpectedServer)
                    throw new SecurityDeniedException();
            bool sealedState;long epoch;DateTimeOffset now;
            await using(var q=Command("SELECT IsSealed,AuthorityEpoch,TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00') FROM security.InstallationState WHERE Singleton=1"))
            await using(var r=await q.ExecuteReaderAsync(ct))
            {
                if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
                sealedState=r.GetBoolean(0);epoch=r.GetInt64(1);now=r.GetDateTimeOffset(2);
            }
            trust.Verify(p,proofs,now);
            var storedTrust=await Scalar("SELECT ManifestHash FROM security.BootstrapTrust WHERE Singleton=1");
            if(storedTrust is byte[] hash)
            {if(!CryptographicOperations.FixedTimeEquals(hash,trust.Hash))throw new SecurityDeniedException();}
            else if(!sealedState||epoch!=0||Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.BootstrapCustodianKey"))!=0)
                throw new SecurityDeniedException();
            if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.BootstrapCustodianRevocation WHERE KeyId IN (@first,@second)",("@first",p.ApproverKeyId),("@second",p.ExecutorKeyId)))!=0)
                throw new SecurityDeniedException();

            if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PrincipalRevocation WHERE PrincipalId=@principal",("@principal",p.PrincipalId)))!=0)
                throw new SecurityDeniedException();
            // Fresh custody authorization is required even for a completed retry.
            await using(var q=Command("""
                SELECT o.ActorPrincipalId,o.RequestBytes,o.ResultBytes FROM security.SecurityOperation o
                JOIN security.InitialBootstrap b ON b.OperationId=o.OperationId
                WHERE o.OperationId=@op AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation r WHERE r.PrincipalId=o.ActorPrincipalId)
                """))
            {
                Add(q,("@op",p.OperationId));await using var r=await q.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    if(r.GetGuid(0)!=p.PrincipalId||!((byte[])r[1]).AsSpan().SequenceEqual(canonical))throw new SecurityConflictException();
                    var receipt=(byte[])r[2];await r.DisposeAsync();await t.CommitAsync(ct);return receipt;
                }
            }
            if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op",("@op",p.OperationId)))!=0)
                throw new SecurityConflictException();
            if(!sealedState||epoch!=0||Convert.ToInt32(await Scalar("""
                SELECT (SELECT COUNT(*) FROM security.InitialBootstrap)+(SELECT COUNT(*) FROM security.Principal)+
                    (SELECT COUNT(*) FROM security.PermissionGrant)+(SELECT COUNT(*) FROM security.SessionAuthority)+
                    (SELECT COUNT(*) FROM security.SecurityOperation)
                """))!=0)throw new SecurityDeniedException();
            if(p.ExpectedEpoch!=epoch)throw new SecurityConflictException();

            if(storedTrust is null)
            {
                await Execute("INSERT security.BootstrapTrust VALUES(1,@installation,@bytes,@hash)",("@installation",target.InstallationId),("@bytes",trust.CanonicalBytes),("@hash",trust.Hash));
                foreach(var key in trust.Keys)
                    await Execute("""
                        INSERT security.BootstrapCustodianKey(KeyId,CustodianId,IdentityHash,PublicKeySpki,PublicKeyHash,ValidFrom,ExpiresAt)
                        VALUES(@key,@custodian,@identity,@spki,@hash,@from,@until)
                        """,("@key",key.KeyId),("@custodian",key.CustodianId),("@identity",SHA256.HashData(BootstrapTrustManifest.IdentityBytes(key.Issuer,key.Subject))),
                        ("@spki",key.PublicKeySpki),("@hash",SHA256.HashData(key.PublicKeySpki)),("@from",key.ValidFrom),("@until",key.ExpiresAt));
            }
            var utf8=new UTF8Encoding(false,true);
            await Execute("INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@principal,'H',@issuer,@subject)",
                ("@principal",p.PrincipalId),("@issuer",utf8.GetBytes(p.AdministratorIssuer)),("@subject",utf8.GetBytes(p.AdministratorSubject)));
            var managementGrant=Guid.NewGuid();
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=p.OperationId,principalId=p.PrincipalId,managementGrantId=managementGrant,profile=p.Profile,status="initialized"});
            await Execute("""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                VALUES(@op,@principal,@bytes,@hash,'I',@result)
                """,("@op",p.OperationId),("@principal",p.PrincipalId),("@bytes",canonical),("@hash",SHA256.HashData(canonical)),("@result",result));
            foreach(var permission in Permissions)
                await Execute("""
                    INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                    VALUES(@id,@principal,@principal,'bootstrap',@permission,'I',@use,1,@from,@until)
                    """,("@id",permission=="security.access.manage"?managementGrant:Guid.NewGuid()),("@principal",p.PrincipalId),("@permission",permission),
                    ("@use",permission is "security.access.manage" or "audit.read"),("@from",p.IssuedAt.ToUniversalTime()),("@until",p.AdministratorExpiresAt.ToUniversalTime()));
            var approver=trust.Keys.Single(k=>k.KeyId==p.ApproverKeyId);var executor=trust.Keys.Single(k=>k.KeyId==p.ExecutorKeyId);
            await Execute("""
                INSERT security.InitialBootstrap(Singleton,OperationId,PrincipalId,NonceHash,ApproverKeyId,ApproverCustodianId,ApproverIdentityHash,
                    ExecutorKeyId,ExecutorCustodianId,ExecutorIdentityHash,ApproverSignature,ExecutorSignature)
                VALUES(1,@op,@principal,@nonce,@approver,@firstCustodian,@firstIdentity,@executor,@secondCustodian,@secondIdentity,@firstSignature,@secondSignature);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@audit,@op,@principal,'initial-bootstrap',@reason);
                UPDATE security.InstallationState SET IsSealed=0,AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,("@op",p.OperationId),("@principal",p.PrincipalId),("@nonce",SHA256.HashData(p.Nonce)),
                ("@approver",approver.KeyId),("@firstCustodian",approver.CustodianId),("@firstIdentity",SHA256.HashData(BootstrapTrustManifest.IdentityBytes(approver.Issuer,approver.Subject))),
                ("@executor",executor.KeyId),("@secondCustodian",executor.CustodianId),("@secondIdentity",SHA256.HashData(BootstrapTrustManifest.IdentityBytes(executor.Issuer,executor.Subject))),
                ("@firstSignature",proofs.Single(s=>s.KeyId==approver.KeyId).Signature),("@secondSignature",proofs.Single(s=>s.KeyId==executor.KeyId).Signature),
                ("@audit",Guid.NewGuid()),("@reason",p.Reason));
            await t.CommitAsync(ct);return result;
        }
        catch
        {try{await t.RollbackAsync(CancellationToken.None);}catch(Exception e)when(e is SqlException or InvalidOperationException){}throw;}

        SqlCommand Command(string sql)=>new(sql,c,t){CommandTimeout=30};
        async Task Lock(string resource)=>await Execute("""
            DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51020,'Bootstrap lock unavailable.',1;
            """,("@resource",resource));
        async Task Execute(string sql,params (string Name,object Value)[] values)
        {await using var q=Command(sql);Add(q,values);await q.ExecuteNonQueryAsync(ct);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values)
        {await using var q=Command(sql);Add(q,values);return await q.ExecuteScalarAsync(ct);}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {
        foreach(var(name,value)in values)
            if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;
            else if(value is DateTimeOffset time)q.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=time;
            else q.Parameters.AddWithValue(name,value);
    }
}
