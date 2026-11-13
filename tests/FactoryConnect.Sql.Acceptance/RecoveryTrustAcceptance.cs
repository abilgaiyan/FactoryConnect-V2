using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class RecoveryTrustAcceptance
{
    private const string Issuer="https://recovery.fixture/tenant";
    private sealed class Fixture:IDisposable
    {
        internal readonly RSA A=RSA.Create(3072),B=RSA.Create(3072),C=RSA.Create(3072),D=RSA.Create(3072);
        internal readonly BootstrapTrustFile BootstrapFile;
        internal readonly BootstrapTrustManifest Bootstrap;
        internal readonly RecoveryTrustFile RecoveryFile;
        internal readonly RecoveryTrustManifest Recovery;
        internal readonly RecoveryTrustProvisionPayload Payload;
        internal Fixture(Guid installation,Guid actor,DateTimeOffset now,long epoch)
        {
            BootstrapFile=new(installation,Issuer,[Key(A,"bootstrap-a"),Key(B,"bootstrap-b")]);Bootstrap=new(BootstrapFile);
            RecoveryFile=new(installation,Issuer,[Key(C,"recovery-a"),Key(D,"recovery-b")]);Recovery=new(RecoveryFile);
            Payload=new(installation,Guid.NewGuid(),Guid.NewGuid(),actor,Issuer,"administrator",BootstrapFile.Keys[0].KeyId,
                BootstrapFile.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),epoch,RandomNumberGenerator.GetBytes(32),Bootstrap.Hash,Recovery.Hash,"provision separate recovery custody");
            BootstrapCustodian Key(RSA rsa,string subject)=>new(Guid.NewGuid(),Guid.NewGuid(),Issuer,subject,rsa.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(2));
        }
        internal SignedRecoveryTrustProvision Sign(RecoveryTrustProvisionPayload p)
        {var bytes=RecoveryTrustManifest.Canonicalize(p);return new(p,[new(p.ApproverKeyId,A.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),new(p.ExecutorKeyId,B.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))],[new(RecoveryFile.Keys[0].KeyId,C.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),new(RecoveryFile.Keys[1].KeyId,D.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))]);}
        public void Dispose(){A.Dispose();B.Dispose();C.Dispose();D.Dispose();}
    }
    internal static int PureChecks()
    {
        var count=0;var now=new DateTimeOffset(2026,11,6,0,0,0,TimeSpan.Zero);using var f=new Fixture(Guid.NewGuid(),Guid.NewGuid(),now,1);
        var p=f.Payload;f.Recovery.VerifyProvision(f.Bootstrap,f.Sign(p),now);Check(true,"separate recovery trust verifies two bootstrap custodian signatures");
        var golden=new RecoveryTrustProvisionPayload(Guid.Parse("11111111-1111-1111-1111-111111111111"),Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),Guid.Parse("44444444-4444-4444-4444-444444444444"),Issuer,"administrator",
            Guid.Parse("55555555-5555-5555-5555-555555555555"),Guid.Parse("66666666-6666-6666-6666-666666666666"),now,now.AddMinutes(10),1,
            Enumerable.Range(0,32).Select(i=>(byte)i).ToArray(),Enumerable.Repeat((byte)1,32).ToArray(),Enumerable.Repeat((byte)2,32).ToArray(),"provision");
        Check(Convert.ToHexString(SHA256.HashData(RecoveryTrustManifest.Canonicalize(golden))).ToLowerInvariant()=="b6496b76ca92e0e83bd9b88750770275826d86b78b20b5998c7d1e4526ae7a26","recovery provisioning canonical v1 golden digest");
        Check(RecoveryTrustManifest.Canonicalize(p).SequenceEqual(RecoveryTrustManifest.Canonicalize(p with{IssuedAt=p.IssuedAt.ToOffset(TimeSpan.FromHours(5.5))})),"recovery provisioning canonicalizes UTC instants");
        Check(f.Recovery.Hash.SequenceEqual(new RecoveryTrustManifest(f.RecoveryFile with{Keys=f.RecoveryFile.Keys.Reverse().ToArray()}).Hash),"recovery manifest order is canonical");
        Check(!f.Recovery.Hash.SequenceEqual(new BootstrapTrustManifest(new(f.RecoveryFile.InstallationId,Issuer,f.RecoveryFile.Keys)).Hash),"recovery trust has a distinct hash domain from bootstrap");
        var signed=f.Sign(p);
        Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,new(p,[signed.Signatures[0]],signed.RecoverySignatures),now),"one bootstrap custodian cannot provision recovery trust");
        Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,new(p,[signed.Signatures[0],signed.Signatures[0]],signed.RecoverySignatures),now),"duplicate proof cannot satisfy recovery provisioning threshold");
        Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,new(p with{Reason="changed"},signed.Signatures,signed.RecoverySignatures),now),"custody signatures bind exact provisioning content");
        Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,signed with{RecoverySignatures=[]},now),"recovery trust requires new private-key possession proofs");
        Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,signed with{RecoverySignatures=[signed.RecoverySignatures[0],signed.RecoverySignatures[0]]},now),"one recovery owner cannot prove both keys");
        var expired=p with{IssuedAt=now.AddMinutes(-5),ExpiresAt=now};Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,f.Sign(expired),now),"expired provisioning capability rejects");
        var future=p with{IssuedAt=now.AddMinutes(1)};Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,f.Sign(future),now),"future provisioning capability rejects");
        var self=p with{ActorSubject="bootstrap-a"};Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,f.Sign(self),now),"provisioning administrator cannot be an approving custodian");
        var wrongHash=p with{RecoveryTrustHash=new byte[32]};Denied(()=>f.Recovery.VerifyProvision(f.Bootstrap,f.Sign(wrongHash),now),"signatures cannot override recovery manifest hash binding");
        var reused=new RecoveryTrustManifest(f.RecoveryFile with{Keys=[f.RecoveryFile.Keys[0] with{PublicKeySpki=f.A.ExportSubjectPublicKeyInfo()},f.RecoveryFile.Keys[1]]});
        Denied(()=>reused.VerifyProvision(f.Bootstrap,f.Sign(p with{RecoveryTrustHash=reused.Hash}),now),"recovery keys cannot reuse bootstrap public material under new IDs");
        var reusedId=new RecoveryTrustManifest(f.RecoveryFile with{Keys=[f.RecoveryFile.Keys[0] with{KeyId=f.BootstrapFile.Keys[0].KeyId},f.RecoveryFile.Keys[1]]});
        Denied(()=>reusedId.VerifyProvision(f.Bootstrap,f.Sign(p with{RecoveryTrustHash=reusedId.Hash}),now),"recovery keys cannot reuse bootstrap key IDs");
        Invalid(()=>new RecoveryTrustManifest(f.RecoveryFile with{Keys=[f.RecoveryFile.Keys[0],f.RecoveryFile.Keys[1] with{PublicKeySpki=f.C.ExportSubjectPublicKeyInfo()}]}),"recovery custodians require distinct canonical public keys");
        Invalid(()=>RecoveryTrustManifest.Canonicalize(p with{Nonce=new byte[31]}),"provisioning nonce is fixed at 32 bytes");
        Invalid(()=>RecoveryTrustManifest.Canonicalize(p with{ExpiresAt=p.IssuedAt.AddMinutes(16)}),"recovery provisioning challenge has bounded lifetime");
        Invalid(()=>RecoveryTrustManifest.Canonicalize(p with{ActorSubject="\ud800"}),"recovery identity encoding cannot be lossy");
        return count;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        void Denied(Action action,string name){try{action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        void Invalid(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new InvalidOperationException(name);}
    }
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;var actor=Guid.NewGuid();using var f=new Fixture(target.InstallationId,actor,now,1);
        var bootstrapPayload=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),actor,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            f.BootstrapFile.Keys[0].KeyId,f.BootstrapFile.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),f.Bootstrap.Hash,"recovery fixture initialization");
        var initialBytes=BootstrapTrustManifest.Canonicalize(bootstrapPayload);
        await new SqlBootstrapAuthority(target,f.Bootstrap).ApplyAsync(new(bootstrapPayload,[new(bootstrapPayload.ApproverKeyId,f.A.SignData(initialBytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),new(bootstrapPayload.ExecutorKeyId,f.B.SignData(initialBytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))]));
        // Privileged fixture session represents independently validated sign-in; production provisions none automatically.
        var token=RandomNumberGenerator.GetBytes(32);await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",token),("@actor",actor));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);var p=f.Payload;var signed=f.Sign(p);
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).ProvisionRecoveryTrustAsync(token,f.RecoveryFile,signed),"recovery provisioning checks installation identity");
        await Exec("UPDATE security.InstallationState SET IsSealed=1 WHERE Singleton=1");
        await Denied(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,signed),"sealed installation cannot provision recovery keys");
        await Exec("UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1");
        await Denied(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,new(p,[signed.Signatures[0]],signed.RecoverySignatures)),"invalid custody threshold leaves no recovery authority");
        await Denied(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,f.Sign(p with{ActorSubject="wrong-subject"})),"signed request cannot impersonate session actor binding");
        var expired=p with{IssuedAt=now.AddMinutes(-5),ExpiresAt=now};await Denied(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,f.Sign(expired)),"expired request cannot provision recovery authority");
        await Conflict(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,f.Sign(p with{ExpectedEpoch=99})),"recovery provisioning expected epoch is checked");
        Check(await RecoveryRows()==0,"rejected provisioning creates no trust keys state or ceremony");
        await Exec("CREATE TRIGGER security.Fixture_RecoveryFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51105,'Recovery fixture audit failure.',1; END;");
        try{await service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,signed);throw new InvalidOperationException("Expected failure.");}
        catch(SqlException e)when(e.Number==51105){Check(true,"recovery provisioning audit failure aborts transaction");}
        await Exec("DROP TRIGGER security.Fixture_RecoveryFailure");
        Check(await RecoveryRows()==0,"trust keys state and ceremony roll back atomically");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op",("@op",p.OperationId)))==0,"failed recovery provision leaves no completed receipt");
        Check(Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==1,"failed recovery provisioning preserves authority epoch");
        var results=await Task.WhenAll(service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,signed),service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,signed));
        Check(results[0].SequenceEqual(results[1]),"concurrent exact recovery provision retries return one receipt");
        Check(await RecoveryRows()==5,"one trust revision current pointer ceremony and two public keys persist");
        Check(Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==2,"recovery trust provisioning advances epoch once");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",p.OperationId)))==1,"recovery trust retry writes one audit");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.Principal"))==1,"provisioning does not create a recovery administrator");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant"))==14,"provisioning does not grant additional permissions");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority"))==1,"provisioning does not fabricate a login session");
        Check(((byte[])(await Scalar("SELECT ManifestHash FROM security.RecoveryTrustRevision WHERE TrustId=@trust",("@trust",p.TrustId)))!).SequenceEqual(f.Recovery.Hash),"committed recovery trust hash matches exact public manifest");
        await Conflict(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,f.Sign(p with{Reason="changed"})),"recovery provisioning operation rejects conflicting content");
        var another=p with{OperationId=Guid.NewGuid(),TrustId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),ExpectedEpoch=2};
        await Denied(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,f.Sign(another)),"another ceremony cannot replace initialized recovery trust");
        Check((await service.ReadOperationResultAsync(token,p.OperationId)).SequenceEqual(results[0]),"authorized actor retrieves provisioning receipt");
        await Immutable("DELETE security.RecoveryTrustRevision","recovery trust revision cannot be erased");
        await Immutable("UPDATE security.RecoveryCustodianKey SET PublicKeySpki=0x01","recovery public keys cannot be overwritten");
        await Immutable("DELETE security.RecoveryTrustProvisioning","recovery provisioning ceremony is immutable");
        // A completed receipt cannot retain session authority while queued behind actual revocation.
        var otherToken=RandomNumberGenerator.GetBytes(32);
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',2)",("@token",otherToken),("@actor",actor));
        var provisioner=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2recovery-provisioner"}.ConnectionString,target.InstallationId);
        var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2recovery-revoker"}.ConnectionString,target.InstallationId);
        await Exec("""
            CREATE TRIGGER security.Fixture_RecoveryBarrier ON security.SecurityAudit AFTER INSERT AS
            BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:recovery:test-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51020,'Recovery fixture barrier unavailable.',1; END;
            """);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:recovery:test-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture barrier failed.',1;",barrier,tx))await q.ExecuteNonQueryAsync();
            var revoking=revoker.RevokeSessionAsync(token,Guid.NewGuid(),otherToken,2,"revoke recovery fixture session");Task<byte[]>? pending=null;
            try
            {
                await WaitForLock("fc2recovery-revoker","S");pending=provisioner.ProvisionRecoveryTrustAsync(otherToken,f.RecoveryFile,signed);
                await WaitForLock("fc2recovery-provisioner","X");Check(!pending.IsCompleted,"recovery provisioning retry waits behind uncommitted session revocation");
            }
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>pending??throw new InvalidOperationException("Provisioning retry did not start."),"queued provisioning receipt rechecks committed session revocation");
        }
        await Exec("DROP TRIGGER security.Fixture_RecoveryBarrier");
        // Simulate a separately authorized bootstrap custodian revocation. A completed replay still rechecks custody.
        var op=Guid.NewGuid();await Exec("""
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
            VALUES(@op,@actor,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
            INSERT security.BootstrapCustodianRevocation(KeyId,OperationId) VALUES(@key,@op);
            """,("@op",op),("@actor",actor),("@key",p.ApproverKeyId));
        await Denied(()=>service.ProvisionRecoveryTrustAsync(token,f.RecoveryFile,signed),"completed recovery provision replay rechecks custodian revocation");
        var management=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission='security.access.manage'"))!;
        await service.RevokeGrantAsync(token,Guid.NewGuid(),management,3,"revoke fixture management");
        await Denied(()=>service.ReadOperationResultAsync(token,p.OperationId),"revoked administrator cannot retrieve recovery provisioning receipt");
        return count;
        async Task WaitForLock(string app,string mode)
        {
            for(var i=0;i<150;i++)
            {
                if(Convert.ToInt32(await Scalar("""
                    SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id
                    WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode
                    """,("@app",app),("@mode",mode)))>0)return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("Expected recovery SQL application-lock wait: "+app+"/"+mode);
        }
        async Task<int> RecoveryRows()=>Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM security.RecoveryTrustRevision)+(SELECT COUNT(*) FROM security.RecoveryTrustState)+(SELECT COUNT(*) FROM security.RecoveryCustodianKey)+(SELECT COUNT(*) FROM security.RecoveryTrustProvisioning)"));
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Immutable(string sql,string name){try{await Exec(sql);}catch(SqlException e)when(e.Number==51030){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {foreach(var(name,value)in values)if(value is byte[] b)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=b;else q.Parameters.AddWithValue(name,value);}
}
