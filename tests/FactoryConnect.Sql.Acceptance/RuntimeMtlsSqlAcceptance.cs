using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class RuntimeMtlsSqlAcceptance
{
    const string Issuer="https://certificate.fixture/tenant";
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var admin=Guid.NewGuid();var manager=Guid.NewGuid();var site=Guid.NewGuid();var machine=Guid.NewGuid();var otherMachine=Guid.NewGuid();
        await using var fixture=new RuntimeMtlsFixture(target.InstallationId,manager,now,4);var f=fixture.Credentials;
        var initial=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),admin,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            f.File.Keys[0].KeyId,f.File.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),f.Trust.Hash,"mTLS SQL acceptance bootstrap");
        var canonical=BootstrapTrustManifest.Canonicalize(initial);
        await new SqlBootstrapAuthority(target,f.Trust).ApplyAsync(new(initial,[RuntimeCertificateAcceptance.Fixture.Proof(initial.ApproverKeyId,f.A,canonical),RuntimeCertificateAcceptance.Fixture.Proof(initial.ExecutorKeyId,f.B,canonical)]));
        var rootToken=RandomNumberGenerator.GetBytes(32);var token=RandomNumberGenerator.GetBytes(32);
        await Exec("""
            INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1);
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'mTLS acceptance');
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Assigned'),(@other,@site,N'Unassigned');
            """,("@token",rootToken),("@actor",admin),("@site",site),("@machine",machine),("@other",otherMachine));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var reader=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2mtls-reader"}.ConnectionString,target.InstallationId);
        var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2mtls-revoker"}.ConnectionString,target.InstallationId);
        await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),manager,Issuer,"certificate-manager",1,"enroll mTLS fixture manager"));
        foreach(var permission in new[]{"runtime.credential.manage","runtime.assign"})
        {
            var parent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission=@permission",("@permission",permission)))!;
            await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),Guid.NewGuid(),manager,parent,permission,permission=="runtime.assign"?AuthorizationScope.Machine(site,machine):AuthorizationScope.Installation,true,false,now,now.AddDays(1),await Epoch(),"grant explicit fixture authority"));
        }
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',4)",("@token",token),("@actor",manager));
        await MachineOperationalFixture.Enable(c,site,[machine,otherMachine],admin,rootToken,manager,now);
        await using var pair=await fixture.Connect();
        Check(typeof(RuntimeMtlsChannel).IsSealed&&typeof(RuntimeMtlsChannel).GetConstructors().Length==0,"authenticated channel cannot be caller-constructed or subclassed");
        await Denied(()=>Read(),"valid CA and handshake do not replace SQL certificate enrollment");
        await service.EnrollRuntimeCertificateAsync(token,f.Sign(f.Payload,certificate:fixture.Certificate));
        await Denied(()=>Read(),"enrolled mTLS runtime without machine assignment is denied");
        var assignment=await Assign(0,null);
        var activities=new List<Activity>();var caWithinSql=false;using var listener=new ActivityListener
        {
            ShouldListenTo=source=>source.Name is "FactoryConnect.V2.Security" or "FactoryConnect.V2.RuntimeTransport",
            Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted=activity=>{if(activity.OperationName=="runtime.ca.verify"&&activity.Parent?.OperationName=="security.transaction")caWithinSql=true;},
            ActivityStopped=activity=>{lock(activities)activities.Add(activity);}
        };ActivitySource.AddActivityListener(listener);
        var authority=await Read();
        Check(authority.RuntimeIdentityId==f.Payload.RuntimeIdentityId&&authority.AssignmentId==assignment&&authority.AssignmentGeneration==1&&authority.CredentialGeneration==1,"mTLS certificate resolves exact current runtime and assignment generations from SQL");
        await Denied(()=>service.ReadRuntimeMachineAsync(pair.Channel,site,otherMachine,(_,_,_)=>Task.FromResult(true)),"authenticated runtime cannot read an unassigned machine");
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).ReadRuntimeMachineAsync(pair.Channel,site,machine,(_,_,_)=>Task.FromResult(true)),"runtime read verifies explicit installation identity");
        using(var changedPublic=X509CertificateLoader.LoadCertificate(f.Leaf(f.Runtime,now,crlUri:fixture.CrlUri)))
        using(var changedCertificate=changedPublic.CopyWithPrivateKey(f.Runtime))
        await using(var changedPair=await fixture.Connect(certificate:changedCertificate))
            await Denied(()=>service.ReadRuntimeMachineAsync(changedPair.Channel,site,machine,(_,_,_)=>Task.FromResult(true)),"same runtime key in a different certificate cannot replace exact SQL DER pin");
        // Privileged fixture injects inconsistent upstream facts: a valid certificate attached to another retained key.
        // The real producer rejects this; the runtime reader must independently reject it too.
        using(var mismatchKey=RSA.Create(3072))
        using(var mismatchPublic=X509CertificateLoader.LoadCertificate(f.Leaf(f.Runtime,now,crlUri:fixture.CrlUri)))
        using(var mismatchCertificate=mismatchPublic.CopyWithPrivateKey(f.Runtime))
        {
            var fakeRuntime=Guid.NewGuid();var fakeOp=Guid.NewGuid();var fakeAssignment=Guid.NewGuid();
            await Exec("""
                INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@runtime,'R',0x66697874757265,@subject);
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes) VALUES(@op,@actor,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(NEWID(),@op,@actor,'fixture-inconsistent-certificate',N'Synthetic integrity acceptance only');
                INSERT security.RuntimeIdentity VALUES(@runtime,'R',@op);
                INSERT security.RuntimeCredentialGeneration VALUES(@runtime,1,@keyHash,@from,@until,@op);
                INSERT security.RuntimeCredentialPublicKey VALUES(@runtime,1,@spki,@keyHash,@op);
                INSERT security.RuntimeCredentialState VALUES(@runtime,1);
                INSERT security.RuntimeCredentialCeremony VALUES(@op,@runtime,1,@nonce,@first,@second,0x01,0x02,0x03);
                INSERT security.RuntimeCertificateBinding VALUES(@runtime,1,@certificate,@certificateHash,@anchor,@anchorHash,@anchorKeyHash,@from,@until,@op);
                INSERT configuration.RuntimeAssignment VALUES(@assignment,@site,@machine,@runtime,1,@from,NULL,@op);
                INSERT configuration.RuntimeAssignmentState VALUES(@machine,@site,@assignment,1,@runtime);
                """,("@runtime",fakeRuntime),("@subject",fakeRuntime.ToByteArray()),("@op",fakeOp),("@actor",admin),("@keyHash",SHA256.HashData(mismatchKey.ExportSubjectPublicKeyInfo())),
                ("@from",now.AddMinutes(-1)),("@until",now.AddDays(30)),("@spki",mismatchKey.ExportSubjectPublicKeyInfo()),("@nonce",RandomNumberGenerator.GetBytes(32)),("@first",initial.ApproverKeyId),("@second",initial.ExecutorKeyId),
                ("@certificate",mismatchCertificate.RawData),("@certificateHash",SHA256.HashData(mismatchCertificate.RawData)),("@anchor",f.Root.RawData),("@anchorHash",SHA256.HashData(f.Root.RawData)),("@anchorKeyHash",SHA256.HashData(f.Ca.ExportSubjectPublicKeyInfo())),
                ("@assignment",fakeAssignment),("@site",site),("@machine",otherMachine));
            await using var mismatchPair=await fixture.Connect(certificate:mismatchCertificate);
            await Denied(()=>service.ReadRuntimeMachineAsync(mismatchPair.Channel,site,otherMachine,(_,_,_)=>Task.FromResult(true)),"runtime read independently rejects certificate and retained-key integrity mismatch");
        }
        await Exec("UPDATE [master].Machine SET IsRetired=1 WHERE MachineId=@machine",("@machine",machine));
        await Denied(()=>Read(),"retired machine denies authenticated runtime reads");
        await Exec("UPDATE [master].Machine SET IsRetired=0 WHERE MachineId=@machine",("@machine",machine));
        var next=await Assign(1,assignment);Check((await Read()).AssignmentId==next,"existing channel reads fresh assignment replacement instead of retaining old authority");
        var elapsed=new List<double>();
        for(var i=0;i<16;i++){var start=Stopwatch.GetTimestamp();await Read();elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
        var snapshots=activities.Where(activity=>activity.GetTagItem("security.runtime.read") is true&&activity.GetTagItem("security.result")?.ToString()=="completed").ToArray();
        var maxRoundtrips=snapshots.Max(activity=>Convert.ToInt64(activity.GetTagItem("security.sql.roundtrips")));
        Check(maxRoundtrips<=12&&elapsed.Max()<2000,"runtime read retains bounded SQL and whole-request warm latency budgets");
        Check(!caWithinSql&&activities.Any(activity=>activity.OperationName=="runtime.ca.verify"),"CA verification is measured outside SQL authority transactions");
        Console.WriteLine($"MEASURE: mTLS runtime read samples={elapsed.Count} maxMs={elapsed.Max():F2} maxRoundtrips={maxRoundtrips}; warm CRL cache, not production capacity");
        // A long machine wait cannot extend the CA proof's five-second freshness window.
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,ConfigurationLockResources.Machine(target.InstallationId,machine));
            var pending=Read();
            try
            {
                await WaitForLock("fc2mtls-reader","S");var start=Stopwatch.GetTimestamp();
                while(Stopwatch.GetElapsedTime(start)<TimeSpan.FromMilliseconds(5200))await Task.Delay(20);
                Check(!pending.IsCompleted,"runtime read waits behind machine gate while certificate verdict ages");
            }
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"aged CA verdict cannot authorize after machine gate wait");
        }
        // Read-first: held shared gates allow the bounded read to finish before assignment invalidation commits.
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=reader.ReadRuntimeMachineAsync(pair.Channel,site,machine,async(_,_,value)=>{entered.SetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(10));return value;});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var removing=revoker.RevokeRuntimeAssignmentAsync(token,Guid.NewGuid(),next,site,machine,2,await Epoch(),"remove assigned runtime");
        try{await WaitForLock("fc2mtls-revoker","X");Check(!removing.IsCompleted,"assignment revocation waits behind an authorized runtime read");}
        finally{release.TrySetResult();}
        Check((await reading).AssignmentId==next,"read-first ordering finishes with its protected assignment cut");await removing;
        await Denied(()=>Read(),"committed assignment revocation denies further reads on same TLS channel");
        var restored=await Assign(2,next);Check((await Read()).AssignmentId==restored,"explicit assignment replacement restores current scoped access");
        // Revocation-first: gate is held through an observed audit barrier, then queued read sees the committed revocation.
        await Exec("""
            CREATE TRIGGER security.Fixture_MtlsBarrier ON security.SecurityAudit AFTER INSERT AS
            BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:mtls:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51020,'mTLS fixture barrier failed',1; END;
            """);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:mtls:audit-barrier");
            var revoke=f.Payload with{Action="revoke",ExpectedGeneration=1,ExpectedEpoch=await Epoch(),OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=[],ValidFrom=DateTimeOffset.UnixEpoch,CredentialExpiresAt=DateTimeOffset.UnixEpoch};
            var signedBytes=RuntimeCredentialProof.Canonicalize(revoke);
            var revoking=revoker.ChangeRuntimeCredentialAsync(token,new(revoke,[RuntimeCertificateAcceptance.Fixture.Proof(revoke.ApproverKeyId,f.A,signedBytes),RuntimeCertificateAcceptance.Fixture.Proof(revoke.ExecutorKeyId,f.B,signedBytes)],[]));
            Task<RuntimeMachineAuthority>? pending=null;
            try
            {
                await WaitForLock("fc2mtls-revoker","S");pending=Read();await WaitForLock("fc2mtls-reader","S");
                Check(!pending.IsCompleted,"runtime read waits behind uncommitted credential revocation");
            }
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>pending??throw new InvalidOperationException("read not started"),"revocation-first ordering denies queued runtime read after commit");
        }
        await Exec("DROP TRIGGER security.Fixture_MtlsBarrier");
        var rotated=f.Payload with{Action="rotate",ExpectedGeneration=1,ExpectedEpoch=await Epoch(),OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=f.Other.ExportSubjectPublicKeyInfo()};
        var rotatedDer=f.Leaf(f.Other,now,crlUri:fixture.CrlUri);await service.EnrollRuntimeCertificateAsync(token,f.Sign(rotated,certificate:rotatedDer,key:f.Other));
        await Denied(()=>Read(),"old TLS channel cannot retain authority across credential rotation");
        using(var publicCertificate=X509CertificateLoader.LoadCertificate(rotatedDer))
        using(var rotatedCertificate=publicCertificate.CopyWithPrivateKey(f.Other))
        await using(var rotatedPair=await fixture.Connect(certificate:rotatedCertificate))
        {
            var value=await service.ReadRuntimeMachineAsync(rotatedPair.Channel,site,machine,(_,_,authority)=>Task.FromResult(authority));
            Check(value.CredentialGeneration==2&&value.AssignmentGeneration==3,"new authenticated certificate preserves independent assignment generation");
            await Exec("UPDATE security.InstallationState SET IsSealed=1");
            await Denied(()=>service.ReadRuntimeMachineAsync(rotatedPair.Channel,site,machine,(_,_,_)=>Task.FromResult(true)),"sealed installation denies authenticated runtime reads");
            await Exec("UPDATE security.InstallationState SET IsSealed=0");
            await rotatedPair.Channel.DisposeAsync();
            await Denied(()=>service.ReadRuntimeMachineAsync(rotatedPair.Channel,site,machine,(_,_,_)=>Task.FromResult(true)),"disposed authenticated channel is not reusable authority");
        }
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest"))==0,"runtime authentication does not create activation or acknowledgement");
        return count;
        Task<RuntimeMachineAuthority> Read()=>reader.ReadRuntimeMachineAsync(pair.Channel,site,machine,(_,_,value)=>Task.FromResult(value));
        async Task<Guid> Assign(long expected,Guid? prior)
        {var id=Guid.NewGuid();await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),id,site,machine,f.Payload.RuntimeIdentityId,1,now.AddSeconds(-30),null,expected,prior,await Epoch(),"assign mTLS fixture runtime"));return id;}
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task Hold(SqlConnection connection,SqlTransaction transaction,string resource)
        {await using var command=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'fixture lock failed',1;",connection,transaction);command.Parameters.AddWithValue("@resource",resource);await command.ExecuteNonQueryAsync();}
        async Task WaitForLock(string app,string mode)
        {for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("Expected actual mTLS SQL lock wait: "+app+"/"+mode);}
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var command=new SqlCommand(sql,c);Add(command,args);return await command.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var command=new SqlCommand(sql,c);Add(command,args);await command.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand command,params (string Name,object Value)[] args)
    {foreach(var(name,value)in args)if(value is byte[] bytes)command.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else command.Parameters.AddWithValue(name,value);}
}
