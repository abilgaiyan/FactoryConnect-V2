using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class RuntimeAssignmentAcceptance
{
    const string Issuer="https://assignment.fixture/tenant";
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        using var a=RSA.Create(3072);using var b=RSA.Create(3072);using var key=RSA.Create(3072);using var otherKey=RSA.Create(3072);using var rotatedKey=RSA.Create(3072);
        var admin=Guid.NewGuid();var manager=Guid.NewGuid();var runtime=Guid.NewGuid();var otherRuntime=Guid.NewGuid();
        var site=Guid.NewGuid();var machine=Guid.NewGuid();var otherMachine=Guid.NewGuid();var wrongSite=Guid.NewGuid();
        BootstrapCustodian Custodian(RSA rsa,string subject)=>new(Guid.NewGuid(),Guid.NewGuid(),Issuer,subject,rsa.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(10));
        var file=new BootstrapTrustFile(target.InstallationId,Issuer,[Custodian(a,"custodian-a"),Custodian(b,"custodian-b")]);var trust=new BootstrapTrustManifest(file);
        var initial=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),admin,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            file.Keys[0].KeyId,file.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),trust.Hash,"assignment acceptance bootstrap");
        var canonical=BootstrapTrustManifest.Canonicalize(initial);
        BootstrapSignature Proof(Guid id,RSA rsa,byte[] bytes)=>new(id,rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        await new SqlBootstrapAuthority(target,trust).ApplyAsync(new(initial,[Proof(initial.ApproverKeyId,a,canonical),Proof(initial.ExecutorKeyId,b,canonical)]));
        var rootToken=RandomNumberGenerator.GetBytes(32);var token=RandomNumberGenerator.GetBytes(32);
        await Exec("""
            INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1);
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Assignment acceptance'),(@wrong,N'Other site');
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'One'),(@other,@site,N'Two');
            """,("@token",rootToken),("@actor",admin),("@site",site),("@wrong",wrongSite),("@machine",machine),("@other",otherMachine));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),manager,Issuer,"assignment-manager",await Epoch(),"enroll independent manager"));
        var assignParent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission='runtime.assign'"))!;
        var credentialParent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission='runtime.credential.manage'"))!;
        var assignmentGrant=Guid.NewGuid();
        await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),assignmentGrant,manager,assignParent,"runtime.assign",AuthorizationScope.Machine(site,machine),true,false,now,now.AddDays(1),await Epoch(),"grant one exact machine"));
        await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),Guid.NewGuid(),manager,credentialParent,"runtime.credential.manage",AuthorizationScope.Installation,true,false,now,now.AddDays(1),await Epoch(),"grant controlled credential ceremony"));
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",token),("@actor",manager));
        await Credential(runtime,"enroll",0,key);await Credential(otherRuntime,"enroll",0,otherKey);
        var request=await Request(0,null);
        await Denied(()=>service.AssignRuntimeAsync(rootToken,request),"security administration alone cannot assign runtime");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{MachineId=otherMachine}),"machine-scoped runtime grant cannot assign another machine");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{SiteId=wrongSite}),"assignment cannot cross authoritative site ownership");
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).AssignRuntimeAsync(token,request),"assignment verifies installation identity");
        await Exec("UPDATE security.InstallationState SET IsSealed=1");
        await Denied(()=>service.AssignRuntimeAsync(token,request),"sealed installation cannot assign runtime");
        await Exec("UPDATE security.InstallationState SET IsSealed=0");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{RuntimeIdentityId=manager}),"human identity cannot be assigned as runtime");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{RuntimeIdentityId=Guid.NewGuid()}),"unprovisioned runtime cannot be assigned");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{ExpectedCredentialGeneration=2}),"assignment validates exact current credential generation");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{EffectiveFrom=now.AddDays(31)}),"credential must cover first intended assignment use");
        await Denied(()=>service.AssignRuntimeAsync(token,request with{EffectiveFrom=now.AddMinutes(-2),EffectiveTo=now.AddMinutes(-1)}),"ended assignment interval rejects");
        await Conflict(()=>service.AssignRuntimeAsync(token,request with{ExpectedSecurityEpoch=99}),"assignment validates expected security epoch");
        await Conflict(()=>service.AssignRuntimeAsync(token,request with{ExpectedAssignmentGeneration=1}),"initial assignment requires generation zero");
        try{await service.AssignRuntimeAsync(token,request with{EffectiveTo=request.EffectiveFrom});throw new InvalidOperationException("empty interval accepted");}catch(ArgumentException){Check(true,"empty assignment interval rejects before SQL");}
        var activities=new List<Activity>();using var listener=new ActivityListener{ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=activity=>{lock(activities)activities.Add(activity);}};ActivitySource.AddActivityListener(listener);
        var results=await Task.WhenAll(service.AssignRuntimeAsync(token,request),service.AssignRuntimeAsync(token,request));
        Check(results[0].SequenceEqual(results[1]),"concurrent exact assignments return one result");
        Check(await AssignmentRows()==1&&await HeadGeneration()==1,"assignment commits one immutable generation and head");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",request.OperationId)))==1,"assignment retry writes one audit");
        Check((await service.AssignRuntimeAsync(token,request with{EffectiveFrom=request.EffectiveFrom.ToOffset(TimeSpan.FromHours(5.5))})).SequenceEqual(results[0]),"assignment retries normalize UTC instants");
        Check((await service.ReadRuntimeAssignmentOperationAsync(token,request.OperationId)).SequenceEqual(results[0]),"assignment manager retrieves own receipt without security administration");
        await Denied(()=>service.ReadRuntimeAssignmentOperationAsync(rootToken,request.OperationId),"another actor cannot retrieve private assignment receipt");
        await Conflict(()=>service.AssignRuntimeAsync(token,request with{Reason="changed"}),"assignment operation rejects conflicting replay");
        var next=await Request(1,request.AssignmentId);var competing=next with{OperationId=Guid.NewGuid(),AssignmentId=Guid.NewGuid(),RuntimeIdentityId=otherRuntime};
        await Conflict(()=>service.AssignRuntimeAsync(token,next with{SupersedesAssignmentId=null}),"replacement requires exact predecessor identity");
        var winners=await Task.WhenAll(Attempt(next),Attempt(competing));Check(winners.Count(x=>x)==1,"competing runtime replacements have one committed winner");
        var head=(Guid)(await Scalar("SELECT AssignmentId FROM configuration.RuntimeAssignmentState WHERE MachineId=@machine",("@machine",machine)))!;
        Check(await HeadGeneration()==2&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=@id",("@id",request.AssignmentId)))==1,"replacement atomically revokes predecessor and advances assignment generation");
        Check((await service.AssignRuntimeAsync(token,request)).SequenceEqual(results[0])&&await HeadGeneration()==2,"old assignment receipt cannot restore superseded head");
        await Denied(async()=>await service.RevokeRuntimeAssignmentAsync(token,Guid.NewGuid(),request.AssignmentId,site,machine,1,await Epoch(),"old assignment already invalidated"),"revoking superseded assignment cannot revoke replacement");
        var revokeOp=Guid.NewGuid();var revokeEpoch=await Epoch();
        await Denied(()=>service.RevokeRuntimeAssignmentAsync(token,Guid.NewGuid(),head,site,machine,1,revokeEpoch,"wrong generation"),"assignment revocation checks exact target generation");
        var revoked=await service.RevokeRuntimeAssignmentAsync(token,revokeOp,head,site,machine,2,revokeEpoch,"remove current assignment");
        var revokedRetry=await service.RevokeRuntimeAssignmentAsync(token,revokeOp,head,site,machine,2,revokeEpoch,"remove current assignment");
        Check(revoked.SequenceEqual(revokedRetry),"assignment revocation exact retry returns one historical receipt");
        Check(await HeadGeneration()==2,"assignment revocation preserves generation tombstone");
        var replacement=await Request(2,head);await service.AssignRuntimeAsync(token,replacement);
        Check(await HeadGeneration()==3,"explicit replacement after revocation advances assignment generation");
        var failed=await Request(3,replacement.AssignmentId);var beforeEpoch=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_AssignmentFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51107,'assignment audit fixture',1; END;");
        try{await service.AssignRuntimeAsync(token,failed);throw new InvalidOperationException("expected audit failure");}catch(SqlException e)when(e.Number==51107){Check(true,"assignment audit failure aborts transaction");}
        await Exec("DROP TRIGGER security.Fixture_AssignmentFailure");
        Check(await HeadGeneration()==3&&await Epoch()==beforeEpoch&&await AssignmentRows()==3&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=@id",("@id",replacement.AssignmentId)))==0,"failed replacement preserves old assignment head revocation and epoch");
        await Credential(runtime,"rotate",1,rotatedKey);
        Check(await HeadGeneration()==3,"credential rotation does not advance assignment generation");
        var rotated=await Request(3,replacement.AssignmentId);
        await Denied(()=>service.AssignRuntimeAsync(token,rotated),"old credential generation cannot authorize a new assignment");
        var current=rotated with{ExpectedCredentialGeneration=2};await service.AssignRuntimeAsync(token,current);
        Check(await HeadGeneration()==4,"explicit assignment may use current rotated credential");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest"))==0,"assignment changes create no activation or acknowledgement");
        Check(Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM security.RuntimeCredentialState WHERE RuntimeIdentityId=@runtime",("@runtime",runtime)))==2,"assignment changes never advance credential generation");
        await Exec("UPDATE [master].Machine SET IsRetired=1 WHERE MachineId=@machine",("@machine",machine));
        await Denied(async()=>await service.AssignRuntimeAsync(token,(await Request(4,current.AssignmentId)) with{ExpectedCredentialGeneration=2}),"retired machine cannot acquire runtime assignment");
        await Exec("UPDATE [master].Machine SET IsRetired=0 WHERE MachineId=@machine",("@machine",machine));
        try{await Exec("DELETE configuration.RuntimeAssignmentIssuance");throw new InvalidOperationException("mutable issuance");}catch(SqlException e)when(e.Number==51040){Check(true,"assignment provenance is immutable");}
        var budgets=activities.Where(x=>x.OperationName=="security.transaction"&&x.GetTagItem("security.assignment.command")?.ToString()=="mutation"&&x.GetTagItem("security.result")?.ToString()=="completed").Select(x=>Convert.ToInt64(x.GetTagItem("security.sql.roundtrips"))).ToArray();
        Check(budgets.Length>0&&budgets.Max()<=24,"assignment commands retain bounded full-path SQL roundtrips");
        Console.WriteLine($"MEASURE: runtime assignment maxRoundtrips={budgets.Max()}; synthetic authority commands, not production capacity");
        // Observe real machine-gate waiting, then cross a session-expiry boundary before releasing it.
        var shortToken=RandomNumberGenerator.GetBytes(32);
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(second,3,SYSUTCDATETIME()),N'fixture-only',1)",("@token",shortToken),("@actor",manager));
        var pendingRequest=(await Request(4,current.AssignmentId)) with{ExpectedCredentialGeneration=2};
        var writer=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2assignment-writer"}.ConnectionString,target.InstallationId);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'fixture barrier failed',1;",barrier,tx))
            {q.Parameters.AddWithValue("@resource",ConfigurationLockResources.Machine(target.InstallationId,machine));await q.ExecuteNonQueryAsync();}
            var pending=writer.AssignRuntimeAsync(shortToken,pendingRequest);
            try
            {
                await WaitForLock("fc2assignment-writer");
                while(Convert.ToInt32(await Scalar("SELECT CASE WHEN ExpiresAt<=SYSUTCDATETIME() THEN 1 ELSE 0 END FROM security.SessionAuthority WHERE TokenHash=@token",("@token",shortToken)))==0)await Task.Delay(20);
                Check(!pending.IsCompleted,"assignment remains queued while SQL clock observes session expiry");
            }
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"assignment rechecks expired session after machine gate wait");
        }
        Check(await HeadGeneration()==4,"expired queued assignment changes no state");
        // Credential revocation first: security-gated command denies subsequent assignment creation.
        await Credential(runtime,"revoke",2,null);
        await Denied(async()=>await service.AssignRuntimeAsync(token,(await Request(4,current.AssignmentId)) with{ExpectedCredentialGeneration=2}),"committed credential revocation denies new runtime assignment");
        // Assignment mutation first above is durable; later grant revocation denies both retry and protected receipt.
        await service.RevokeGrantAsync(rootToken,Guid.NewGuid(),assignmentGrant,await Epoch(),"remove assignment permission");
        await Denied(()=>service.AssignRuntimeAsync(token,request),"completed assignment retry rechecks current machine permission");
        await Denied(()=>service.ReadRuntimeAssignmentOperationAsync(token,request.OperationId),"revoked assignment manager cannot retrieve old receipt");
        return count;

        async Task Credential(Guid id,string action,long expected,RSA? rsa)
        {
            var payload=new RuntimeCredentialPayload(target.InstallationId,Guid.NewGuid(),manager,Issuer,"assignment-manager",id,"https://assignment.fixture/edge",id.ToString("D"),action,expected,
                rsa?.ExportSubjectPublicKeyInfo()??[],rsa is null?DateTimeOffset.UnixEpoch:now.AddSeconds(-1),rsa is null?DateTimeOffset.UnixEpoch:now.AddDays(30),
                file.Keys[0].KeyId,file.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),await Epoch(),RandomNumberGenerator.GetBytes(32),trust.Hash,"assignment fixture controlled credential");
            var bytes=RuntimeCredentialProof.Canonicalize(payload);
            await service.ChangeRuntimeCredentialAsync(token,new(payload,[Proof(payload.ApproverKeyId,a,bytes),Proof(payload.ExecutorKeyId,b,bytes)],rsa?.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)??[]));
        }
        async Task<RuntimeAssignmentRequest> Request(long generation,Guid? prior)=>new(Guid.NewGuid(),Guid.NewGuid(),site,machine,runtime,1,now.AddSeconds(-1),null,generation,prior,await Epoch(),"assign exact machine runtime");
        async Task<bool> Attempt(RuntimeAssignmentRequest r){try{await service.AssignRuntimeAsync(token,r);return true;}catch(SecurityConflictException){return false;}}
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task<long> HeadGeneration()=>Convert.ToInt64(await Scalar("SELECT AssignmentGeneration FROM configuration.RuntimeAssignmentState WHERE MachineId=@machine",("@machine",machine)));
        async Task<int> AssignmentRows()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeAssignment"));
        async Task WaitForLock(string app)
        {for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT'",("@app",app)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("expected assignment SQL lock wait");}
        void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);await q.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand q,params (string Name,object Value)[] args)
    {foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
