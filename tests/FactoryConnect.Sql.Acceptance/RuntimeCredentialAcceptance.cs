using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class RuntimeCredentialAcceptance
{
    const string Issuer="https://runtime.fixture/tenant";
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        using var a=RSA.Create(3072);using var b=RSA.Create(3072);using var runtime=RSA.Create(3072);using var next=RSA.Create(3072);
        var actor=Guid.NewGuid();var runtimeId=Guid.NewGuid();
        BootstrapCustodian Key(RSA key,string subject)=>new(Guid.NewGuid(),Guid.NewGuid(),Issuer,subject,key.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(10));
        var file=new BootstrapTrustFile(target.InstallationId,Issuer,[Key(a,"custodian-a"),Key(b,"custodian-b")]);var trust=new BootstrapTrustManifest(file);
        var initial=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),actor,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            file.Keys[0].KeyId,file.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),trust.Hash,"runtime fixture bootstrap");
        var initialBytes=BootstrapTrustManifest.Canonicalize(initial);
        await new SqlBootstrapAuthority(target,trust).ApplyAsync(new(initial,[Proof(file.Keys[0].KeyId,a,initialBytes),Proof(file.Keys[1].KeyId,b,initialBytes)]));
        var token=RandomNumberGenerator.GetBytes(32);
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",token),("@actor",actor));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var p=new RuntimeCredentialPayload(target.InstallationId,Guid.NewGuid(),actor,Issuer,"administrator",runtimeId,"https://runtime.fixture/edge","edge-one",
            "enroll",0,runtime.ExportSubjectPublicKeyInfo(),now.AddSeconds(-1),now.AddDays(30),file.Keys[0].KeyId,file.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),1,
            RandomNumberGenerator.GetBytes(32),trust.Hash,"enroll retained public runtime key");
        var signed=Sign(p,runtime);
        RuntimeCredentialProof.Verify(trust,signed,now);Check(true,"runtime proof verifies independent custody and possession");
        Check(RuntimeCredentialProof.Canonicalize(p).SequenceEqual(RuntimeCredentialProof.Canonicalize(p with{IssuedAt=p.IssuedAt.ToOffset(TimeSpan.FromHours(5.5))})),"runtime canonical proof normalizes UTC");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,signed with{Signatures=[signed.Signatures[0]]}),"single custodian cannot enroll runtime");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,signed with{Signatures=[signed.Signatures[0],signed.Signatures[0]]}),"duplicate custody proof cannot enroll runtime");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,signed with{PossessionSignature=next.SignData(RuntimeCredentialProof.Canonicalize(p),HashAlgorithmName.SHA256,RSASignaturePadding.Pss)}),"runtime must possess exact submitted key");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,signed with{Payload=p with{RuntimeSubject="other"}}),"proof binds exact runtime identity");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,Sign(p with{ActorSubject="custodian-a"},runtime)),"actor cannot be approving custodian");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,Sign(p with{InstallationId=Guid.NewGuid()},runtime)),"runtime command installation is pinned");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,Sign(p with{IssuedAt=now.AddMinutes(-5),ExpiresAt=now,ValidFrom=now.AddMinutes(-6)},runtime)),"expired custody ceremony cannot enroll");
        await Conflict(()=>service.ChangeRuntimeCredentialAsync(token,Sign(p with{ExpectedEpoch=99},runtime)),"runtime enrollment checks authority epoch");
        Check(await Rows()==0,"invalid runtime requests leave no authority");
        await Exec("CREATE TRIGGER security.Fixture_RuntimeFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51106,'runtime audit fixture',1; END;");
        try{await service.ChangeRuntimeCredentialAsync(token,signed);throw new InvalidOperationException("expected audit failure");}catch(SqlException e)when(e.Number==51106){Check(true,"runtime audit failure aborts command");}
        await Exec("DROP TRIGGER security.Fixture_RuntimeFailure");
        Check(await Rows()==0&&Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==1,"runtime identity key and epoch roll back with audit");
        var results=await Task.WhenAll(service.ChangeRuntimeCredentialAsync(token,signed),service.ChangeRuntimeCredentialAsync(token,signed));
        Check(results[0].SequenceEqual(results[1]),"concurrent exact runtime retries commit one receipt");
        Check(await Rows()==1,"runtime enroll persists one public key");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCredentialCeremony"))==1,"runtime custody proofs persist once");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",p.OperationId)))==1,"runtime audit is exactly once");
        Check(((byte[])(await Scalar("SELECT CredentialBindingHash FROM security.RuntimeCredentialGeneration"))!).SequenceEqual(SHA256.HashData(p.PublicKeySpki)),"generation binds retained exact canonical SPKI hash");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeAssignment"))==0&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest"))==0,"runtime enrollment creates no assignment or activation");
        await Conflict(()=>service.ChangeRuntimeCredentialAsync(token,Sign(p with{Reason="changed"},runtime)),"runtime operation rejects changed retry content");
        var rotate=p with{Action="rotate",ExpectedGeneration=1,ExpectedEpoch=2,OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=next.ExportSubjectPublicKeyInfo()};
        await Conflict(()=>service.ChangeRuntimeCredentialAsync(token,Sign(rotate with{ExpectedGeneration=2},next)),"rotation rejects stale current generation");
        await Conflict(()=>service.ChangeRuntimeCredentialAsync(token,Sign(rotate with{Nonce=p.Nonce},next)),"nonce cannot be reused under another operation");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,Sign(rotate with{PublicKeySpki=p.PublicKeySpki},runtime)),"rotation cannot recycle previously enrolled key");
        await service.ChangeRuntimeCredentialAsync(token,Sign(rotate,next));
        Check(Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM security.RuntimeCredentialState"))==2,"runtime rotation advances only credential generation");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCredentialRevocation WHERE CredentialGeneration=1"))==1,"rotation atomically revokes old credential");
        Check((await service.ChangeRuntimeCredentialAsync(token,signed)).SequenceEqual(results[0]),"old enrollment retry returns receipt without restoring old generation");
        Check(Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM security.RuntimeCredentialState"))==2,"old receipt cannot roll back credential state");
        var revoke=rotate with{Action="revoke",OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),ExpectedEpoch=3,ExpectedGeneration=2,PublicKeySpki=[],ValidFrom=DateTimeOffset.UnixEpoch,CredentialExpiresAt=DateTimeOffset.UnixEpoch};
        await service.ChangeRuntimeCredentialAsync(token,Sign(revoke,null));
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCredentialRevocation"))==2,"controlled revocation invalidates current generation");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority"))==1&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant"))==13,"runtime credentials create no human sessions or grants");
        try{await Exec("DELETE security.RuntimeCredentialPublicKey");throw new InvalidOperationException("expected immutable key");}catch(SqlException e)when(e.Number==51040){Check(true,"runtime public key history is immutable");}
        // Current permission remains mandatory for completed receipts; security administrator is no bypass.
        var grant=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission='runtime.credential.manage'"))!;
        var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2runtime-revoker"}.ConnectionString,target.InstallationId);
        var requester=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2runtime-requester"}.ConnectionString,target.InstallationId);
        await Exec("""
            CREATE TRIGGER security.Fixture_RuntimeBarrier ON security.SecurityAudit AFTER INSERT AS
            BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:runtime:test-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51020,'Runtime fixture barrier unavailable.',1; END;
            """);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:runtime:test-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture barrier failed.',1;",barrier,tx))await q.ExecuteNonQueryAsync();
            var revoking=revoker.RevokeGrantAsync(token,Guid.NewGuid(),grant,4,"remove runtime credential management");Task<byte[]>? pending=null;
            try
            {
                await WaitForLock("fc2runtime-revoker","S");pending=requester.ChangeRuntimeCredentialAsync(token,signed);
                await WaitForLock("fc2runtime-requester","X");Check(!pending.IsCompleted,"runtime receipt waits behind uncommitted grant revocation");
            }
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>pending??throw new InvalidOperationException("runtime retry did not start"),"queued runtime retry rechecks committed dedicated grant revocation");
        }
        await Exec("DROP TRIGGER security.Fixture_RuntimeBarrier");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,signed),"security administration cannot bypass revoked runtime credential grant");
        return count;
        SignedRuntimeCredential Sign(RuntimeCredentialPayload payload,RSA? key)
        {var bytes=RuntimeCredentialProof.Canonicalize(payload);return new(payload,[Proof(payload.ApproverKeyId,a,bytes),Proof(payload.ExecutorKeyId,b,bytes)],key?.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)??[]);}
        BootstrapSignature Proof(Guid id,RSA key,byte[] bytes)=>new(id,key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> run,string name){try{await run();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> run,string name){try{await run();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task WaitForLock(string app,string mode)
        {
            for(var i=0;i<150;i++)
            {
                if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("Expected runtime SQL lock wait: "+app+"/"+mode);
        }
        async Task<int> Rows()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCredentialPublicKey"));
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);await q.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand q,params (string Name,object Value)[] args)
    {foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
