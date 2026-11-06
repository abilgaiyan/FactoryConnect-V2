using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class HumanIdentityAcceptance
{
    private const string Issuer="https://identity.fixture/tenant";
    private sealed class Verifier(VerifiedHumanIdentity identity):IHumanIdentityVerifier
    {
        public Task<VerifiedHumanIdentity> VerifyAsync(string assertion,CancellationToken ct)
            =>assertion=="fixture"?Task.FromResult(identity):throw new SecurityDeniedException();
    }
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var admin=Guid.NewGuid();using var a=RSA.Create(3072);using var b=RSA.Create(3072);
        var keys=new[]{new BootstrapCustodian(Guid.NewGuid(),Guid.NewGuid(),Issuer,"custodian-a",a.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(1)),
            new BootstrapCustodian(Guid.NewGuid(),Guid.NewGuid(),Issuer,"custodian-b",b.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(1))};
        var trust=new BootstrapTrustManifest(new(target.InstallationId,Issuer,keys));
        var payload=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),admin,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            keys[0].KeyId,keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),trust.Hash,"identity fixture bootstrap");
        var canonical=BootstrapTrustManifest.Canonicalize(payload);
        await new SqlBootstrapAuthority(target,trust).ApplyAsync(new(payload,[
            new(keys[0].KeyId,a.SignData(canonical,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),
            new(keys[1].KeyId,b.SignData(canonical,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))]));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var writer=new SqlSecurityAuthority(Named("fc2identity-writer"),target.InstallationId);
        var tokens=new Dictionary<Guid,byte[]>{{admin,await Login("administrator")}};
        var site=Guid.NewGuid();var machine=Guid.NewGuid();var scope=AuthorizationScope.Machine(site,machine);
        await Exec("INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Identity fixture'); INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Fixture machine');",("@site",site),("@machine",machine));
        async Task<HumanEnrollmentRequest> Request(string subject)=>new(Guid.NewGuid(),Guid.NewGuid(),Issuer,subject,await Epoch(),"enroll fixture human");
        var memberRequest=await Request("member");
        await Exec("UPDATE security.InstallationState SET IsSealed=1 WHERE Singleton=1");
        await Denied(()=>service.EnrollHumanAsync(tokens[admin],memberRequest),"sealed installation blocks human enrollment");
        await Exec("UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1");
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).EnrollHumanAsync(tokens[admin],memberRequest),"enrollment validates installation identity");
        await Denied(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{Issuer="https://untrusted.fixture"}),"enrollment cannot introduce an unpinned issuer");
        await Denied(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{Issuer="https://identity.fixture/Tenant"}),"pinned issuer comparison preserves exact case");
        await Invalid(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{Subject="\ud800"}),"enrollment rejects lossy identity encoding");
        await Invalid(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{Subject=new string('x',2049)}),"enrollment enforces identity byte bounds");
        await Invalid(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{Subject=" "}),"enrollment requires an explicit subject");
        await Conflict(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{ExpectedEpoch=99}),"enrollment rejects stale authority epoch");
        var epoch=await Epoch();var results=await Task.WhenAll(service.EnrollHumanAsync(tokens[admin],memberRequest),service.EnrollHumanAsync(tokens[admin],memberRequest));
        Check(results[0].SequenceEqual(results[1]),"concurrent exact enrollment retries return one receipt");
        Check(await Epoch()==epoch+1,"enrollment retry advances authority epoch once");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",memberRequest.OperationId)))==1,"enrollment retry writes one audit");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.HumanIdentityEnrollment WHERE PrincipalId=@p AND OperationId=@op AND ActorPrincipalId=@actor",("@p",memberRequest.PrincipalId),("@op",memberRequest.OperationId),("@actor",admin)))==1,"human enrollment records target and authorized actor");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant WHERE GranteeId=@p",("@p",memberRequest.PrincipalId)))==0,"enrollment creates no permissions");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority WHERE PrincipalId=@p",("@p",memberRequest.PrincipalId)))==0,"enrollment creates no authentication session");
        await Conflict(()=>service.EnrollHumanAsync(tokens[admin],memberRequest with{Subject="different"}),"enrollment operation rejects altered fingerprint");
        await Conflict(async()=>await service.EnrollHumanAsync(tokens[admin],memberRequest with{OperationId=Guid.NewGuid(),ExpectedEpoch=await Epoch()}),"principal ID cannot be enrolled twice");
        await Conflict(async()=>await service.EnrollHumanAsync(tokens[admin],(await Request("member"))),"external identity cannot bind to a second principal");
        var competing=await Request("competing-human");
        var competitors=await Task.WhenAll(Attempt(competing),Attempt(competing with{OperationId=Guid.NewGuid(),PrincipalId=Guid.NewGuid()}));
        Check(competitors.Count(x=>x)==1,"different enrollment operations cannot bind one external identity twice");
        foreach(var subject in new[]{"Member","member ","member\0","\u00e9","e\u0301"})
        {var r=await Request(subject);await service.EnrollHumanAsync(tokens[admin],r);Check(await Login(subject) is {Length:32},"exact external subject preserved: "+Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(subject)));}
        var member=memberRequest.PrincipalId;tokens[member]=await Login("member");
        await Denied(()=>service.ReadAsync(tokens[member],"configuration.read",scope,(_,_,_)=>Task.FromResult(true)),"enrolled and authenticated human still needs explicit permission");
        await Grant(admin,member,"configuration.read",scope,true);
        await Grant(admin,member,"security.access.manage",scope,true);
        Check(await Read(member),"enrolled human can use explicitly issued machine permission");
        await Denied(async()=>await service.EnrollHumanAsync(tokens[member],(await Request("out-of-scope"))),"machine-scoped access administrator cannot enroll global identities");
        await Denied(async()=>await service.RevokeHumanAsync(tokens[member],Guid.NewGuid(),admin,await Epoch(),"out-of-scope revoke"),"machine-scoped administrator cannot revoke global identities");
        // Trusted identity registration is atomic; failed audit never leaves a usable orphan principal.
        var rollback=await Request("rollback-human");epoch=await Epoch();
        await AuditFailure();
        await SqlFailure(()=>service.EnrollHumanAsync(tokens[admin],rollback),"enrollment audit failure aborts whole operation");
        await Exec("DROP TRIGGER security.Fixture_IdentityFailure");
        Check(Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM security.Principal WHERE PrincipalId=@p)+(SELECT COUNT(*) FROM security.HumanIdentityEnrollment WHERE PrincipalId=@p)+(SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op)",("@p",rollback.PrincipalId),("@op",rollback.OperationId)))==0,"principal enrollment mapping and receipt roll back together");
        Check(await Epoch()==epoch,"failed enrollment preserves authority epoch");
        await service.EnrollHumanAsync(tokens[admin],rollback);Check(await Login("rollback-human") is {Length:32},"failed enrollment can retry unchanged after rollback");
        // Principal revocation must not consume the caller's only independent administration authority.
        var installManager=await Request("installation-manager");await service.EnrollHumanAsync(tokens[admin],installManager);
        var manager=installManager.PrincipalId;tokens[manager]=await Login("installation-manager");
        await Grant(admin,manager,"security.access.manage",AuthorizationScope.Installation,true);
        await Denied(async()=>await service.RevokeHumanAsync(tokens[manager],Guid.NewGuid(),admin,await Epoch(),"dependent root revoke"),"dependent administrator cannot revoke its only management ancestor");
        await Denied(async()=>await service.RevokeHumanAsync(tokens[admin],Guid.NewGuid(),admin,await Epoch(),"self revoke"),"ordinary principal revocation cannot revoke the caller");
        var runtime=Guid.NewGuid();await Exec("INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@p,'R',0x72756E74696D65,0x6964656E74697479)",("@p",runtime));
        await Denied(async()=>await service.RevokeHumanAsync(tokens[admin],Guid.NewGuid(),runtime,await Epoch(),"wrong principal kind"),"human revocation cannot operate on runtime identities");
        await Denied(async()=>await service.RevokeHumanAsync(tokens[admin],Guid.NewGuid(),Guid.NewGuid(),await Epoch(),"unknown identity"),"human revocation rejects unknown principal");
        // Offboarding a delegated grantor invalidates all descendants, including descendants' own delegations.
        var leafRequest=await Request("leaf");await service.EnrollHumanAsync(tokens[admin],leafRequest);var leaf=leafRequest.PrincipalId;tokens[leaf]=await Login("leaf");
        await Grant(member,leaf,"configuration.read",scope,false);
        Check(await Read(leaf),"live delegated human chain authorizes descendant");
        var revokeOp=Guid.NewGuid();var revokeEpoch=await Epoch();
        await Conflict(()=>service.RevokeHumanAsync(tokens[admin],revokeOp,member,0,"stale revoke"),"principal revoke rejects stale authority epoch");
        await AuditFailure();
        await SqlFailure(()=>service.RevokeHumanAsync(tokens[admin],revokeOp,member,revokeEpoch,"offboard member"),"principal revoke audit failure rolls back revocation");
        await Exec("DROP TRIGGER security.Fixture_IdentityFailure");
        Check(await Read(member)&&await Read(leaf),"failed principal revocation preserves direct and dependent authority");
        Check(await Epoch()==revokeEpoch,"failed principal revoke preserves epoch");
        // Read-first ordering: actual shared read gate holds revocation until its materialized result is complete.
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=service.ReadAsync(tokens[leaf],"configuration.read",scope,async(_,_,ct)=>{entered.SetResult();await release.Task.WaitAsync(ct);return true;});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revoking=writer.RevokeHumanAsync(tokens[admin],revokeOp,member,revokeEpoch,"offboard member");
        try{await WaitForLock("fc2identity-writer","X");Check(!revoking.IsCompleted,"human revocation waits for dependent protected read");}
        finally{release.TrySetResult();}
        Check(await reading,"authorized dependent read finishes before principal revoke commits");
        var revoked=await revoking;
        await Denied(()=>Read(member),"principal revocation invalidates existing direct session authority");
        await Denied(()=>Read(leaf),"principal revocation invalidates descendant delegation authority");
        await Denied(()=>Login("member"),"principal revocation blocks fresh sign-in proofs");
        Check((await service.RevokeHumanAsync(tokens[admin],revokeOp,member,revokeEpoch,"offboard member")).SequenceEqual(revoked),"exact principal revoke retry returns original receipt");
        Check((await service.EnrollHumanAsync(tokens[admin],memberRequest)).SequenceEqual(results[0]),"enrollment receipt retrieval never resurrects revoked identity");
        await Denied(()=>Login("member"),"replayed enrollment leaves principal revoked");
        await Conflict(async()=>await service.EnrollHumanAsync(tokens[admin],(await Request("member"))),"revoked external binding cannot be re-enrolled to bypass revocation");
        await Conflict(()=>service.RevokeHumanAsync(tokens[admin],revokeOp,member,revokeEpoch,"altered reason"),"principal revoke operation fingerprint is immutable");
        // Revoke-first ordering: hold actual revocation at its audit while fresh sign-in waits for the gate.
        await Exec("""
            CREATE TRIGGER security.Fixture_IdentityBarrier ON security.SecurityAudit AFTER INSERT AS
            BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:identity:test-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51020,'Identity fixture barrier unavailable.',1; END;
            """);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:identity:test-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture barrier failed.',1;",barrier,tx))await q.ExecuteNonQueryAsync();
            var task=writer.RevokeHumanAsync(tokens[admin],Guid.NewGuid(),manager,await Epoch(),"offboard manager");Task<byte[]>? signingIn=null;
            try
            {
                await WaitForLock("fc2identity-writer","S");signingIn=Login("installation-manager","fc2identity-login");
                await WaitForLock("fc2identity-login","S");Check(!signingIn.IsCompleted,"fresh sign-in waits behind uncommitted principal revocation");
            }
            finally{await tx.CommitAsync();}
            await task;await Denied(()=>signingIn??throw new InvalidOperationException("Sign-in did not start."),"sign-in rechecks committed human revocation after waiting");
        }
        await Exec("DROP TRIGGER security.Fixture_IdentityBarrier");
        await Immutable("DELETE security.HumanIdentityEnrollment","human enrollment history cannot be erased");
        await Immutable("DELETE security.PrincipalRevocation","human revocation history cannot be erased");
        var receipt=(await service.ReadOperationResultAsync(tokens[admin],memberRequest.OperationId));
        Check(receipt.SequenceEqual(results[0]),"authorized administrator can read enrollment receipt");
        // An independent valid management path remains usable even if another path depends on the target.
        // Privileged fixture simulates a root established through future controlled recovery, not ordinary enrollment.
        var alternateRequest=await Request("alternate-root");await service.EnrollHumanAsync(tokens[admin],alternateRequest);
        var alternate=alternateRequest.PrincipalId;tokens[alternate]=await Login("alternate-root");
        await Grant(admin,alternate,"security.access.manage",AuthorizationScope.Installation,true);
        var independent=Guid.NewGuid();
        await Exec("""
            INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
            VALUES(@grant,@p,@p,'bootstrap','security.access.manage','I',1,0,@from,@until);
            """,("@grant",independent),("@p",alternate),("@from",now.AddMinutes(-1)),("@until",now.AddHours(1)));
        // Revoke the root at the end, after testing the existing administrator's result retrieval denial.
        var rootManage=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE GranteeId=@p AND Permission='security.access.manage' AND ParentGrantId IS NULL",("@p",admin)))!;
        await service.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),rootManage,await Epoch(),"remove current administrator authority");
        await Denied(()=>service.ReadOperationResultAsync(tokens[admin],memberRequest.OperationId),"revoked management authority denies stored enrollment results");
        await Denied(()=>service.EnrollHumanAsync(tokens[admin],memberRequest),"completed enrollment cannot bypass revoked management authority");
        await service.RevokeHumanAsync(tokens[alternate],Guid.NewGuid(),admin,await Epoch(),"independent root offboarding");
        Check(await service.ReadAsync(tokens[alternate],"security.access.manage",AuthorizationScope.Installation,(_,_,_)=>Task.FromResult(true)),"independent management path survives principal offboarding");
        await Denied(()=>Login("administrator"),"independent administrator can revoke former root principal");
        return count;

        async Task<bool> Attempt(HumanEnrollmentRequest r){try{await service.EnrollHumanAsync(tokens[admin],r);return true;}catch(SecurityConflictException){return false;}}

        string Named(string app)=>new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=app}.ConnectionString;
        async Task<byte[]> Login(string subject,string? app=null)
        {
            // Separate fixture clock connection: sign-in may run concurrently with lock monitoring.
            await using var clock=new SqlConnection(target.ConnectionString);await clock.OpenAsync();
            await using var q=new SqlCommand("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')",clock);
            var time=(DateTimeOffset)(await q.ExecuteScalarAsync())!;
            var identity=new VerifiedHumanIdentity(Issuer,subject,Guid.NewGuid().ToString("N"),"fixture-mfa",time.AddSeconds(-1),time.AddMinutes(30));
            var issued=await new SqlHumanSessionAuthority(app is null?target.ConnectionString:Named(app),target.InstallationId,new Verifier(identity)).IssueAsync("fixture");
            return SHA256.HashData(issued.Token);
        }
        async Task Grant(Guid actor,Guid recipient,string permission,AuthorizationScope resource,bool canDelegate)
        {
            var parent=(Guid)(await Scalar("SELECT TOP(1) GrantId FROM security.PermissionGrant WHERE GranteeId=@p AND Permission=@permission AND CanDelegate=1",("@p",actor),("@permission",permission)))!;
            var from=(DateTimeOffset)(await Scalar("SELECT EffectiveFrom FROM security.PermissionGrant WHERE GrantId=@p",("@p",parent)))!;
            var until=(DateTimeOffset)(await Scalar("SELECT ExpiresAt FROM security.PermissionGrant WHERE GrantId=@p",("@p",parent)))!;
            await service.GrantPermissionAsync(tokens[actor],new(Guid.NewGuid(),Guid.NewGuid(),recipient,parent,permission,resource,true,canDelegate,from,until,await Epoch(),"identity fixture grant"));
        }
        Task<bool> Read(Guid principal)=>service.ReadAsync(tokens[principal],"configuration.read",scope,(_,_,_)=>Task.FromResult(true));
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState WHERE Singleton=1"));
        async Task AuditFailure()=>await Exec("CREATE TRIGGER security.Fixture_IdentityFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51104,'Identity fixture audit failure.',1; END;");
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
            throw new InvalidOperationException("Expected SQL application-lock wait: "+app+"/"+mode);
        }
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Invalid(Func<Task> action,string name){try{await action();}catch(ArgumentException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task SqlFailure(Func<Task> action,string name){try{await action();}catch(SqlException e)when(e.Number==51104){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Immutable(string sql,string name){try{await Exec(sql);}catch(SqlException e)when(e.Number==51030){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {
        foreach(var(name,value)in values)
            if(value is byte[] b)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=b;
            else q.Parameters.AddWithValue(name,value);
    }
}
