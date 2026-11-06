using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class ActivationAcknowledgementAcceptance
{
    const string Issuer="https://certificate.fixture/tenant";
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;var packages=new Dictionary<Guid,PackageDefinition>();await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var admin=Guid.NewGuid();var manager=Guid.NewGuid();var reviewer=Guid.NewGuid();var site=Guid.NewGuid();var line=Guid.NewGuid();var machine=Guid.NewGuid();
        await using var tls=new RuntimeMtlsFixture(target.InstallationId,manager,now,0);var f=tls.Credentials;
        var initial=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),admin,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,f.File.Keys[0].KeyId,f.File.Keys[1].KeyId,
            now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),f.Trust.Hash,"acknowledgement acceptance bootstrap");
        var initialBytes=BootstrapTrustManifest.Canonicalize(initial);
        await new SqlBootstrapAuthority(target,f.Trust).ApplyAsync(new(initial,[RuntimeCertificateAcceptance.Fixture.Proof(initial.ApproverKeyId,f.A,initialBytes),RuntimeCertificateAcceptance.Fixture.Proof(initial.ExecutorKeyId,f.B,initialBytes)]));
        var rootToken=RandomNumberGenerator.GetBytes(32);var token=RandomNumberGenerator.GetBytes(32);var reviewToken=RandomNumberGenerator.GetBytes(32);
        await Exec("""
            INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1);
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Acknowledgement acceptance');
            INSERT [master].ProductionLine(ProductionLineId,SiteId,DisplayName) VALUES(@line,@site,N'Fixture line');
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Fixture machine');
            """,("@token",rootToken),("@actor",admin),("@site",site),("@line",line),("@machine",machine));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var writer=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2ack-writer"}.ConnectionString,target.InstallationId);
        var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2ack-revoker"}.ConnectionString,target.InstallationId);
        await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),manager,Issuer,"certificate-manager",await Epoch(),"enroll manager"));
        await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),reviewer,Issuer,"independent-reviewer",await Epoch(),"enroll independent reviewer"));
        foreach(var permission in new[]{"runtime.credential.manage","runtime.assign","configuration.edit","configuration.submit","configuration.activate"})
            await Grant(manager,permission,permission=="runtime.credential.manage"?AuthorizationScope.Installation:AuthorizationScope.Site(site));
        await Grant(reviewer,"configuration.review",AuthorizationScope.Site(site));
        await Session(token,manager);await Session(reviewToken,reviewer);
        await service.EnrollRuntimeCertificateAsync(token,f.Sign(f.Payload with{ExpectedEpoch=await Epoch()},certificate:tls.Certificate));
        var credentialGeneration=1L;var assignmentGeneration=1L;var assignment=Guid.NewGuid();
        await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),assignment,site,machine,f.Payload.RuntimeIdentityId,1,now.AddMinutes(-5),null,0,null,await Epoch(),"assign acknowledgement fixture"));
        await using var pair=await tls.Connect();
        var activities=new List<Activity>();using var listener=new ActivityListener{ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=activity=>{lock(activities)activities.Add(activity);}};ActivitySource.AddActivityListener(listener);
        var first=await Prepare();var ack=await ThroughWire(Ack(first));
        Check(ack.ActivationId==first.ActivationId,"acknowledgement body arrives through the authenticated TLS channel");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{ContentHash=new byte[32]}),"acknowledgement binds exact approved canonical hash");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{ApprovalReviewId=Guid.NewGuid()}),"acknowledgement binds exact approval identity");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{ActivationGeneration=2}),"acknowledgement binds exact activation generation");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{AssignmentGeneration=2}),"acknowledgement cannot substitute assignment generation");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{CredentialGeneration=2}),"acknowledgement cannot substitute credential generation");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{RuntimeIdentityId=manager}),"human identity cannot impersonate authenticated runtime acknowledgement");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{MachineId=Guid.NewGuid()}),"runtime cannot acknowledge another machine scope");
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).AcknowledgeActivationAsync(pair.Channel,ack),"acknowledgement verifies installation identity");
        var concurrent=await Task.WhenAll(service.AcknowledgeActivationAsync(pair.Channel,ack),service.AcknowledgeActivationAsync(pair.Channel,ack));
        Check(concurrent[0].SequenceEqual(concurrent[1])&&await Rows()==1,"concurrent exact acknowledgement retries commit one effect");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",ack.OperationId)))==1,"acknowledgement retry writes one atomic runtime audit");
        Check((Guid)(await Scalar("SELECT ActorPrincipalId FROM security.SecurityOperation WHERE OperationId=@op",("@op",ack.OperationId)))! ==f.Payload.RuntimeIdentityId,"runtime principal owns acknowledgement operation and audit");
        Check((DateTimeOffset)(await Scalar("SELECT AppliedFrom FROM configuration.RuntimeActivationAcknowledgement WHERE AcknowledgementId=@ack",("@ack",ack.AcknowledgementId)))! >first.EffectiveFrom,"acknowledgement uses SQL time instead of backdating requested start");
        Check(Encoding.UTF8.GetString(concurrent[0]).Contains("not-connectivity",StringComparison.Ordinal),"acknowledgement receipt makes no connectivity claim");
        Check((await service.ReadRuntimeAcknowledgementOperationAsync(pair.Channel,site,machine,ack.OperationId)).SequenceEqual(concurrent[0]),"current exact runtime retrieves its stored acknowledgement receipt");
        await Conflict(()=>service.AcknowledgeActivationAsync(pair.Channel,ack with{Reason="changed"}),"acknowledgement operation rejects changed retry content");
        await Conflict(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(first)),"different acknowledgement identity cannot duplicate one activation effect");
        var next=await Prepare();var nextAck=Ack(next);var competing=nextAck with{OperationId=Guid.NewGuid(),AcknowledgementId=Guid.NewGuid()};
        var winners=await Task.WhenAll(Attempt(nextAck,pair.Channel),Attempt(competing,pair.Channel));
        Check(winners.Count(value=>value)==1,"competing acknowledgement identities have one committed winner");
        var newest=await AckHead();await service.AcknowledgeActivationAsync(pair.Channel,ack);
        Check(await AckHead()==newest,"historical exact retry cannot restore a superseded acknowledgement head");
        var superseded=await Prepare();await Prepare();
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(superseded)),"superseded unacknowledged request cannot acquire acknowledgement");
        var future=await Prepare(effectiveFrom:now.AddHours(1));
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(future)),"future activation cannot be acknowledged as already applied");
        Check(await AckHead()==newest,"pending and failed replacements preserve last acknowledgement fact");
        var failed=await Prepare();var failedAck=Ack(failed);var beforeRows=await Rows();var beforeEpoch=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_AckFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51109,'acknowledgement audit fixture',1; END;");
        try{await service.AcknowledgeActivationAsync(pair.Channel,failedAck);throw new InvalidOperationException("expected acknowledgement audit failure");}catch(SqlException error)when(error.Number==51109){Check(true,"post-insert audit failure aborts acknowledgement transaction");}
        await Exec("DROP TRIGGER security.Fixture_AckFailure");
        Check(await Rows()==beforeRows&&await AckHead()==newest&&await Epoch()==beforeEpoch&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op",("@op",failedAck.OperationId)))==0,"acknowledgement row head operation audit and epoch roll back together");
        await service.AcknowledgeActivationAsync(pair.Channel,failedAck);
        Check(await Rows()==beforeRows+1,"identical acknowledgement can retry after rollback");
        var expiryTime=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,2,SYSUTCDATETIME()),'+00:00')"))!;
        var expiring=await Prepare(effectiveTo:expiryTime);var expiryAck=Ack(expiring);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,ConfigurationLockResources.Machine(target.InstallationId,machine));
            var pending=writer.AcknowledgeActivationAsync(pair.Channel,expiryAck);
            try
            {
                await WaitForLock("fc2ack-writer","X");
                while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))! <expiryTime)await Task.Delay(20);
                Check(!pending.IsCompleted,"acknowledgement remains queued while SQL clock crosses activation expiry");
            }
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"acknowledgement rechecks activation expiry after authority locks");
        }
        // A source hash buffer is copied before asynchronous waits, and cannot change the operation being committed.
        var buffered=await Prepare();var bufferedAck=Ack(buffered);var originalHash=bufferedAck.ContentHash.ToArray();
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,ConfigurationLockResources.Machine(target.InstallationId,machine));
            var pending=writer.AcknowledgeActivationAsync(pair.Channel,bufferedAck);
            try{await WaitForLock("fc2ack-writer","X");bufferedAck.ContentHash[0]^=255;}
            finally{await tx.CommitAsync();}
            await pending;
        }
        Check(((byte[])(await Scalar("SELECT ContentHash FROM configuration.RuntimeActivationAcknowledgement WHERE AcknowledgementId=@ack",("@ack",bufferedAck.AcknowledgementId)))!).SequenceEqual(originalHash),"caller hash mutation cannot change a queued acknowledgement");
        var firstRace=await Prepare();var firstRaceAck=Ack(firstRace);
        await AuditBarrier();
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:ack:audit-barrier");
            var acknowledging=writer.AcknowledgeActivationAsync(pair.Channel,firstRaceAck);Task<byte[]>? revoking=null;
            try
            {
                await WaitForLock("fc2ack-writer","S");
                // Revocation's explicit epoch is the next epoch that this observed acknowledgement will commit.
                revoking=revoker.RevokeActivationRequestAsync(token,Guid.NewGuid(),firstRace.ActivationId,site,machine,(await Epoch())+1,"revoke after acknowledgement");
                await WaitForLock("fc2ack-revoker","X");Check(!revoking.IsCompleted,"revocation waits behind an in-flight authenticated acknowledgement");
            }
            finally{await tx.CommitAsync();}
            await acknowledging;await (revoking??throw new InvalidOperationException("revocation not started"));
        }
        await Exec("DROP TRIGGER security.Fixture_AckBarrier");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeActivationAcknowledgement WHERE ActivationId=@activation",("@activation",firstRace.ActivationId)))==1,"acknowledgement-first ordering records historical fact before revocation");
        var secondRace=await Prepare();var secondRaceAck=Ack(secondRace);
        await AuditBarrier();
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:ack:audit-barrier");
            var revoking=revoker.RevokeActivationRequestAsync(token,Guid.NewGuid(),secondRace.ActivationId,site,machine,await Epoch(),"revoke before acknowledgement");Task<byte[]>? pending=null;
            try
            {
                await WaitForLock("fc2ack-revoker","S");pending=writer.AcknowledgeActivationAsync(pair.Channel,secondRaceAck);
                await WaitForLock("fc2ack-writer","X");Check(!pending.IsCompleted,"acknowledgement waits behind uncommitted intent revocation");
            }
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>pending??throw new InvalidOperationException("acknowledgement not started"),"revocation-first ordering denies queued acknowledgement after commit");
        }
        await Exec("DROP TRIGGER security.Fixture_AckBarrier");
        var oldCredential=await Prepare();
        var rotated=f.Payload with{Action="rotate",ExpectedGeneration=1,ExpectedEpoch=await Epoch(),OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=f.Other.ExportSubjectPublicKeyInfo()};
        var rotatedDer=f.Leaf(f.Other,now,crlUri:tls.CrlUri);await service.EnrollRuntimeCertificateAsync(token,f.Sign(rotated,certificate:rotatedDer,key:f.Other));credentialGeneration=2;
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(oldCredential)),"old TLS credential cannot acknowledge after rotation");
        using var rotatedPublic=X509CertificateLoader.LoadCertificate(rotatedDer);using var rotatedCertificate=rotatedPublic.CopyWithPrivateKey(f.Other);
        await using var rotatedPair=await tls.Connect(certificate:rotatedCertificate);
        await Denied(()=>service.AcknowledgeActivationAsync(rotatedPair.Channel,Ack(oldCredential)),"new credential cannot silently rebind old pending request");
        await Denied(()=>service.ReadRuntimeAcknowledgementOperationAsync(rotatedPair.Channel,site,machine,ack.OperationId),"new generation cannot inherit old-generation receipt authority");
        var explicitRotation=await Prepare();await service.AcknowledgeActivationAsync(rotatedPair.Channel,Ack(explicitRotation));
        Check(Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM configuration.RuntimeActivationAcknowledgement WHERE ActivationId=@activation",("@activation",explicitRotation.ActivationId)))==2,"explicit replacement acknowledges new credential without transferring old acknowledgement");
        var oldAssignment=await Prepare();var priorAssignment=assignment;assignment=Guid.NewGuid();
        await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),assignment,site,machine,f.Payload.RuntimeIdentityId,2,now.AddMinutes(-5),null,1,priorAssignment,await Epoch(),"replace independent assignment"));assignmentGeneration=2;
        await Denied(()=>service.AcknowledgeActivationAsync(rotatedPair.Channel,Ack(oldAssignment)),"changed assignment generation requires explicit activation replacement");
        var explicitAssignment=await Prepare();await service.AcknowledgeActivationAsync(rotatedPair.Channel,Ack(explicitAssignment));
        Check(Convert.ToInt64(await Scalar("SELECT AssignmentGeneration FROM configuration.RuntimeActivationAcknowledgement WHERE ActivationId=@activation",("@activation",explicitAssignment.ActivationId)))==2,"explicit acknowledgement retains independently rotated assignment generation");
        foreach(var kind in new[]{"timezone","schedule","break"})
        {
            var blocked=await Prepare();var op=await Upstream("fixture-policy-safety");var root=(await PackageFor(blocked.PackageRevisionId));
            await Exec("""
                INSERT configuration.PolicySafetyRevocation(RevocationId,SiteId,PolicyKind,TimezoneRevisionId,ScheduleRevisionId,BreakPolicyRevisionId,ContentHash,EffectiveFrom,OperationId)
                VALUES(NEWID(),@site,@kind,@timezone,@schedule,@break,@hash,@from,@op);
                """,("@site",site),("@kind",kind),("@timezone",kind=="timezone"?root.Timezone.RevisionId:DBNull.Value),("@schedule",kind=="schedule"?root.Machines[0].Schedule.RevisionId:DBNull.Value),
                ("@break",kind=="break"?root.Machines[0].BreakPolicy.RevisionId:DBNull.Value),("@hash",root.Timezone.ContentHash),("@from",now.AddMinutes(-1)),("@op",op));
            await Denied(()=>service.AcknowledgeActivationAsync(rotatedPair.Channel,Ack(blocked)),"authoritative "+kind+" safety revocation denies acknowledgement");
        }
        var withdrawn=await Prepare();var withdrawalOp=await Upstream("fixture-approval-withdrawal");
        await Exec("INSERT configuration.PackageApprovalRevocation VALUES(@review,@op)",("@review",withdrawn.ApprovalReviewId),("@op",withdrawalOp));
        await Denied(()=>service.AcknowledgeActivationAsync(rotatedPair.Channel,Ack(withdrawn)),"explicit approval withdrawal denies runtime acknowledgement");
        var large=await Prepare(machineCount:100);var largeAck=Ack(large);var watch=Stopwatch.StartNew();await service.AcknowledgeActivationAsync(rotatedPair.Channel,largeAck);watch.Stop();
        var measured=activities.Last(activity=>activity.GetTagItem("security.runtime.acknowledgement") is true&&activity.GetTagItem("security.result")?.ToString()=="completed");
        var roundtrips=Convert.ToInt64(measured.GetTagItem("security.sql.roundtrips"));var locks=Convert.ToInt32(measured.GetTagItem("security.configuration.lock.count"));
        Check(roundtrips<=24&&locks==4,"acknowledgement from a 100-machine package uses bounded SQL and only selected machine locks");
        Check(watch.Elapsed<TimeSpan.FromSeconds(5),"100-machine-package acknowledgement retains CI timeout headroom");
        Console.WriteLine($"MEASURE: runtime acknowledgement packageMachines=100 requestedMachines=1 roundtrips={roundtrips} locks={locks} elapsedMs={watch.Elapsed.TotalMilliseconds:F2}; synthetic warm mTLS fixture, not production capacity");
        try{await Exec("DELETE configuration.RuntimeActivationAcknowledgement");throw new InvalidOperationException("mutable acknowledgement");}catch(SqlException error)when(error.Number==51040){Check(true,"runtime acknowledgement history is immutable");}
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant WHERE Permission='runtime.acknowledge'"))==0,"acknowledgement adds no human impersonation permission");
        return count;

        async Task<ActivationRequestCommand> Prepare(DateTimeOffset? effectiveFrom=null,DateTimeOffset? effectiveTo=null,int machineCount=1)
        {
            var timezone=Guid.NewGuid();var schedule=Guid.NewGuid();var breaks=Guid.NewGuid();var bytes=Encoding.UTF8.GetBytes("{}");var hash=SHA256.HashData(bytes);
            var ids=new[]{machine}.Concat(Enumerable.Range(1,machineCount-1).Select(_=>Guid.NewGuid())).ToArray();
            await Exec("""
                INSERT configuration.SiteTimezoneRevision(TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,RulesBytes,RulesSha256,CanonicalBytes,ContentHash)
                    VALUES(@timezone,@site,N'Etc/UTC','fixture','fixture','reject','reject',@bytes,@hash,@bytes,@hash);
                INSERT configuration.SharedScheduleRevision VALUES(@schedule,@site,@bytes,@hash); INSERT configuration.SharedBreakPolicyRevision VALUES(@break,@site,@bytes,@hash);
                INSERT [master].Machine(MachineId,SiteId,DisplayName) SELECT id,@site,N'Additional fixture machine' FROM OPENJSON(@machines) WITH(id uniqueidentifier '$') WHERE id<>@machine;
                """,("@timezone",timezone),("@schedule",schedule),("@break",breaks),("@site",site),("@bytes",bytes),("@hash",hash),("@machines",JsonSerializer.Serialize(ids)),("@machine",machine));
            var machines=ids.Select(id=>new MachineConfiguration(id,line,now.AddMinutes(-5),null,new(schedule,hash),new(breaks,hash),[new(Guid.NewGuid(),"counter","fixture","fixture:ack","opaque",[65],"identity-v1")],["counter-reading"])).ToArray();
            var package=new PackageDefinition(Guid.NewGuid(),Guid.NewGuid(),1,site,new(timezone,hash),machines);packages[package.RevisionId]=package;
            await service.SubmitConfigurationAsync(token,Guid.NewGuid(),package,await Epoch(),"submit acknowledgement fixture");var review=Guid.NewGuid();
            await service.ReviewConfigurationAsync(reviewToken,new(Guid.NewGuid(),review,site,package.RevisionId,PackageCanonicalizer.Hash(package),true,await Epoch(),"independent acknowledgement fixture review"));
            Guid? prior=null;long generation=0;
            await using(var q=new SqlCommand("SELECT ActivationId,ActivationGeneration FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",c))
            {q.Parameters.AddWithValue("@machine",machine);await using var r=await q.ExecuteReaderAsync();if(await r.ReadAsync()){prior=r.GetGuid(0);generation=r.GetInt64(1);}}
            var request=new ActivationRequestCommand(Guid.NewGuid(),Guid.NewGuid(),site,machine,package.RevisionId,PackageCanonicalizer.Hash(package),review,f.Payload.RuntimeIdentityId,assignment,assignmentGeneration,credentialGeneration,effectiveFrom??now.AddMinutes(-1),effectiveTo,generation,prior,await Epoch(),"request fixture runtime application");
            await service.RequestActivationAsync(token,request);return request with{ExpectedActivationGeneration=generation+1};
        }
        ActivationAcknowledgementRequest Ack(ActivationRequestCommand request)=>new(Guid.NewGuid(),Guid.NewGuid(),site,machine,request.ActivationId,request.ExpectedActivationGeneration,request.PackageRevisionId,request.ContentHash.ToArray(),request.ApprovalReviewId,request.RuntimeIdentityId,request.AssignmentId,request.AssignmentGeneration,request.CredentialGeneration,"runtime confirms exact package application");
        async Task<bool> Attempt(ActivationAcknowledgementRequest value,RuntimeMtlsChannel channel){try{await service.AcknowledgeActivationAsync(channel,value);return true;}catch(SecurityConflictException){return false;}}
        async Task<ActivationAcknowledgementRequest> ThroughWire(ActivationAcknowledgementRequest value)
        {
            var body=JsonSerializer.SerializeToUtf8Bytes(value);var frame=new byte[4+body.Length];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame,body.Length);body.CopyTo(frame,4);
            await pair.Client.WriteAsync(frame);var header=new byte[4];await Receive(header);var length=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
            if(length is <1 or >16384)throw new InvalidOperationException("fixture frame size");var received=new byte[length];await Receive(received);
            return JsonSerializer.Deserialize<ActivationAcknowledgementRequest>(received)??throw new InvalidOperationException("fixture body");
            async Task Receive(Memory<byte> buffer){while(buffer.Length>0){var read=await pair.Channel.ReadAsync(buffer);if(read==0)throw new EndOfStreamException();buffer=buffer[read..];}}
        }
        Task<PackageDefinition> PackageFor(Guid revision)=>Task.FromResult(packages[revision]);
        async Task Grant(Guid grantee,string permission,AuthorizationScope scope)
        {var parent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission=@permission AND GranteeId=@admin",("@permission",permission),("@admin",admin)))!;await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),Guid.NewGuid(),grantee,parent,permission,scope,true,false,now,now.AddDays(1),await Epoch(),"grant fixture scoped authority"));}
        async Task Session(byte[] session,Guid principal)=>await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",session),("@actor",principal));
        async Task<Guid> Upstream(string action)
        {var op=Guid.NewGuid();await Exec("INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes) VALUES(@op,@actor,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D); INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(NEWID(),@op,@actor,@action,N'Synthetic committed upstream acceptance only');",("@op",op),("@actor",admin),("@action",action));return op;}
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task<int> Rows()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeActivationAcknowledgement"));
        async Task<Guid> AckHead()=>(Guid)(await Scalar("SELECT AcknowledgementId FROM configuration.MachineAcknowledgementHead WHERE MachineId=@machine",("@machine",machine)))!;
        async Task AuditBarrier()=>await Exec("CREATE TRIGGER security.Fixture_AckBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:ack:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'ack fixture barrier failed',1; END;");
        async Task Hold(SqlConnection connection,SqlTransaction transaction,string resource)
        {await using var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'fixture lock failed',1;",connection,transaction);q.Parameters.AddWithValue("@resource",resource);await q.ExecuteNonQueryAsync();}
        async Task WaitForLock(string app,string mode)
        {for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("Expected acknowledgement application lock wait: "+app+"/"+mode);}
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);await q.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand q,params (string Name,object Value)[] args)
    {foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else if(name is "@timezone" or "@schedule" or "@break")q.Parameters.Add(name,SqlDbType.UniqueIdentifier).Value=value;else q.Parameters.AddWithValue(name,value);}
}
