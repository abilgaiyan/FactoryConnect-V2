using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class MachineOperationalAcceptance
{
    const string Issuer="https://certificate.fixture/tenant";
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var admin=Guid.NewGuid();var proposer=Guid.NewGuid();var approver=Guid.NewGuid();var holder=Guid.NewGuid();
        var site=Guid.NewGuid();var line=Guid.NewGuid();var machine=Guid.NewGuid();var otherMachine=Guid.NewGuid();
        await using var tls=new RuntimeMtlsFixture(target.InstallationId,proposer,now,0);var f=tls.Credentials;
        var initial=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),admin,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,f.File.Keys[0].KeyId,f.File.Keys[1].KeyId,
            now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),f.Trust.Hash,"operational authority acceptance bootstrap");
        var initialBytes=BootstrapTrustManifest.Canonicalize(initial);
        await new SqlBootstrapAuthority(target,f.Trust).ApplyAsync(new(initial,[RuntimeCertificateAcceptance.Fixture.Proof(initial.ApproverKeyId,f.A,initialBytes),RuntimeCertificateAcceptance.Fixture.Proof(initial.ExecutorKeyId,f.B,initialBytes)]));
        var rootToken=RandomNumberGenerator.GetBytes(32);var token=RandomNumberGenerator.GetBytes(32);var reviewToken=RandomNumberGenerator.GetBytes(32);var holdToken=RandomNumberGenerator.GetBytes(32);
        await Exec("""
            INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1);
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Operational acceptance');
            INSERT [master].ProductionLine(ProductionLineId,SiteId,DisplayName) VALUES(@line,@site,N'Fixture line');
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Target'),(@other,@site,N'Other');
            """,("@token",rootToken),("@actor",admin),("@site",site),("@line",line),("@machine",machine),("@other",otherMachine));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var enabling=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2machine-enabler"}.ConnectionString,target.InstallationId);
        var suspending=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2machine-holder"}.ConnectionString,target.InstallationId);
        var acknowledging=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2machine-ack"}.ConnectionString,target.InstallationId);
        foreach(var(person,subject)in new[]{(proposer,"certificate-manager"),(approver,"independent-enabler"),(holder,"safety-holder")})await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),person,Issuer,subject,await Epoch(),"enroll fixture operator"));
        var grants=new Dictionary<(Guid,string),Guid>();
        foreach(var permission in new[]{"runtime.credential.manage","runtime.assign","configuration.edit","configuration.submit","configuration.activate","machine.enable","commissioning.read"})
            await Grant(proposer,permission,permission=="runtime.credential.manage"?AuthorizationScope.Installation:permission is "configuration.edit" or "configuration.submit"?AuthorizationScope.Site(site):AuthorizationScope.Machine(site,machine));
        await Grant(approver,"machine.enable",AuthorizationScope.Machine(site,machine));await Grant(approver,"configuration.review",AuthorizationScope.Site(site));await Grant(holder,"machine.suspend",AuthorizationScope.Machine(site,machine));
        await Session(token,proposer);await Session(reviewToken,approver);await Session(holdToken,holder);
        var activities=new List<Activity>();using var listener=new ActivityListener{ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=activity=>{lock(activities)activities.Add(activity);}};ActivitySource.AddActivityListener(listener);
        await service.EnrollRuntimeCertificateAsync(token,f.Sign(f.Payload with{ExpectedEpoch=await Epoch()},certificate:tls.Certificate));
        var assignment=Guid.NewGuid();await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),assignment,site,machine,f.Payload.RuntimeIdentityId,1,now.AddMinutes(-5),null,0,null,await Epoch(),"assign operational fixture"));
        await using var pair=await tls.Connect();
        var(package,review)=await Package();
        await Denied(()=>service.ReadRuntimeMachineAsync(pair.Channel,site,machine,(_,_,_)=>Task.FromResult(true)),"unknown operational state blocks otherwise enrolled assigned runtime");
        await Denied(async()=>await Intent(),"unknown operational state blocks activation request creation");
        var unknown=await Current();Check(unknown.Status=="unknown"&&unknown.Generation==0&&unknown.StateId is null,"new and upgraded machines have no implicit enabled authority");
        await Denied(async()=>await service.ProposeMachineEnableAsync(rootToken,await Proposal()),"bootstrap administration alone cannot enable a machine");
        await Denied(async()=>await service.SuspendMachineAsync(rootToken,await Suspension()),"bootstrap administration alone cannot suspend a machine");
        await Denied(async()=>await service.ProposeMachineEnableAsync(holdToken,await Proposal()),"suspension permission does not imply enable permission");
        await Denied(async()=>await service.ProposeMachineEnableAsync(token,(await Proposal()) with{MachineId=otherMachine}),"machine-scoped enable grant cannot operate another machine");
        await Conflict(async()=>await service.ProposeMachineEnableAsync(token,(await Proposal()) with{ExpectedStateId=Guid.NewGuid(),ExpectedGeneration=1}),"enable proposal requires exact current state identity and generation");
        var self=await Proposal();await service.ProposeMachineEnableAsync(token,self);
        var reviewable=await service.ReadMachineEnableProposalAsync(reviewToken,self.ProposalId,site,machine);
        Check(reviewable.ProposerId==proposer&&reviewable.ExpectedGeneration==0&&reviewable.CurrentGeneration==0&&reviewable.Reason==self.Reason,"independent enabler can inspect exact scoped proposal before approving");
        await Denied(()=>service.ReadMachineEnableProposalAsync(holdToken,self.ProposalId,site,machine),"suspension role cannot inspect enable proposal without enable permission");
        await Denied(async()=>await service.ApproveMachineEnableAsync(token,Guid.NewGuid(),self.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"self approval denied"),"enable proposer cannot self approve even with same permission");
        Check((await Current()).Status=="unknown","proposal and rejected self approval do not enable machine");
        await service.RevokeGrantAsync(rootToken,Guid.NewGuid(),grants[(proposer,"machine.enable")],await Epoch(),"revoke proposer enable authority");
        await Denied(async()=>await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),self.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"recheck proposer authority"),"approval rechecks current proposer grant ancestry");
        await Grant(proposer,"machine.enable",AuthorizationScope.Machine(site,machine));
        var temporary=RandomNumberGenerator.GetBytes(32);await Session(temporary,proposer);var revokedSessionProposal=await Proposal();await service.ProposeMachineEnableAsync(temporary,revokedSessionProposal);
        await service.RevokeSessionAsync(rootToken,Guid.NewGuid(),temporary,await Epoch(),"revoke original proposal session");
        await Denied(async()=>await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),revokedSessionProposal.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"session must remain valid"),"approval rechecks the original proposing session revocation");
        var deadline=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,2,SYSUTCDATETIME()),'+00:00')"))!;
        var expiring=(await Proposal()) with{ExpiresAt=deadline};await service.ProposeMachineEnableAsync(token,expiring);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,ConfigurationLockResources.Machine(target.InstallationId,machine));
            var pending=enabling.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),expiring.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"proposal expiry race");
            try{await WaitForLock("fc2machine-enabler","X");while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))! <deadline)await Task.Delay(20);Check(!pending.IsCompleted,"enable approval waits while SQL clock crosses proposal expiry");}
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"expired proposal cannot enable after machine lock wait");
        }
        var rollbackProposal=await Proposal();await service.ProposeMachineEnableAsync(token,rollbackProposal);var rollbackState=Guid.NewGuid();var rollbackOp=Guid.NewGuid();var rollbackEpoch=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_OperationalFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51110,'operational audit fixture',1; END;");
        try{await service.ApproveMachineEnableAsync(reviewToken,rollbackOp,rollbackProposal.ProposalId,rollbackState,site,machine,rollbackEpoch,"atomic enable failure");throw new InvalidOperationException("expected failure");}catch(SqlException error)when(error.Number==51110){Check(true,"post-transition enable audit failure aborts transaction");}
        await Exec("DROP TRIGGER security.Fixture_OperationalFailure");
        Check((await Current()).Status=="unknown"&&await Epoch()==rollbackEpoch&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.MachineOperationalTransition"))==0,"enable transition head receipt audit and epoch roll back together");
        var firstEnable=await Task.WhenAll(service.ApproveMachineEnableAsync(reviewToken,rollbackOp,rollbackProposal.ProposalId,rollbackState,site,machine,rollbackEpoch,"atomic enable failure"),service.ApproveMachineEnableAsync(reviewToken,rollbackOp,rollbackProposal.ProposalId,rollbackState,site,machine,rollbackEpoch,"atomic enable failure"));
        Check(firstEnable[0].SequenceEqual(firstEnable[1])&&(await Current()).Generation==1,"concurrent exact enable approvals commit one state generation");
        await Conflict(async()=>await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),rollbackProposal.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"second approval"),"another approval cannot reuse a completed enable proposal");
        Check((await service.ReadMachineOperationalOperationAsync(reviewToken,rollbackOp)).SequenceEqual(firstEnable[0]),"enable approver retrieves own historical operation receipt");
        await Denied(()=>service.ReadMachineOperationalOperationAsync(token,rollbackOp),"another operator cannot retrieve private enable receipt");
        await Exec("DELETE configuration.MachineOperationalState WHERE MachineId=@machine",("@machine",machine));
        await Denied(()=>Current(),"missing current pointer with retained history is not adopted as a new unknown machine");
        await Exec("INSERT configuration.MachineOperationalState(SiteId,MachineId,StateId,Generation) VALUES(@site,@machine,@state,1)",("@site",site),("@machine",machine),("@state",rollbackState));
        var intent=await Intent();var firstAck=Ack(intent,1);await service.AcknowledgeActivationAsync(pair.Channel,firstAck);
        Check(Convert.ToInt64(await Scalar("SELECT OperationalGeneration FROM configuration.ActivationRequest WHERE ActivationId=@id",("@id",intent.ActivationId)))==1&&Convert.ToInt64(await Scalar("SELECT OperationalGeneration FROM configuration.RuntimeActivationAcknowledgement WHERE AcknowledgementId=@id",("@id",firstAck.AcknowledgementId)))==1,"activation and acknowledgement pin explicit enabled generation");
        var failedSuspend=await Suspension();var failedSuspendEpoch=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_OperationalFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51110,'operational audit fixture',1; END;");
        try{await service.SuspendMachineAsync(holdToken,failedSuspend);throw new InvalidOperationException("expected suspension audit failure");}catch(SqlException error)when(error.Number==51110){Check(true,"post-transition suspension audit failure aborts transaction");}
        await Exec("DROP TRIGGER security.Fixture_OperationalFailure");
        Check((await Current()).Status=="enabled"&&(await Current()).Generation==1&&await Epoch()==failedSuspendEpoch,"failed suspension preserves enabled head and epoch without partial invalidation");
        // Read-first ordering: suspension cannot commit until a protected runtime read releases its shared gate.
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=service.ReadRuntimeMachineAsync(pair.Channel,site,machine,async(_,_,authority)=>{entered.SetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(10));return authority;});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var suspend=await Suspension();var pendingSuspend=suspending.SuspendMachineAsync(holdToken,suspend);
        try{await WaitForLock("fc2machine-holder","X");Check(!pendingSuspend.IsCompleted,"suspension waits behind an authorized runtime read");}
        finally{release.TrySetResult();}
        Check((await reading).OperationalGeneration==1,"read-first ordering completes at its enabled operational cut");await pendingSuspend;
        Check((await Current()).Status=="suspended"&&(await Current()).Generation==2,"suspension commits explicit inactive generation");
        await Denied(()=>service.ReadRuntimeMachineAsync(pair.Channel,site,machine,(_,_,_)=>Task.FromResult(true)),"suspended machine blocks protected runtime reads");
        await Denied(async()=>await Intent(),"suspended machine blocks new activation requests");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,firstAck),"suspension blocks completed runtime acknowledgement retries");
        await service.ApproveMachineEnableAsync(reviewToken,rollbackOp,rollbackProposal.ProposalId,rollbackState,site,machine,rollbackEpoch,"atomic enable failure");
        Check((await Current()).Status=="suspended"&&(await Current()).Generation==2,"historical enable retry cannot restore a suspended machine");
        var staleProposal=await Proposal();await service.ProposeMachineEnableAsync(token,staleProposal);await service.SuspendMachineAsync(holdToken,await Suspension());
        await Conflict(async()=>await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),staleProposal.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"stale hold"),"fresh suspension invalidates a pending enable proposal even when already suspended");
        var resume=await Proposal();await service.ProposeMachineEnableAsync(token,resume);await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),resume.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"independent re-enable");
        Check((await Current()).Status=="enabled"&&(await Current()).Generation==4,"re-enable advances operational generation through independent approval");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,firstAck),"old acknowledgement cannot revive after re-enable");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,firstAck with{OperationId=Guid.NewGuid(),AcknowledgementId=Guid.NewGuid(),OperationalGeneration=4}),"caller cannot rebind old activation to new operational generation");
        await Denied(()=>service.ReadRuntimeAcknowledgementOperationAsync(pair.Channel,site,machine,firstAck.OperationId),"runtime receipt authority is bound to original operational generation");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeActivationAcknowledgement"))==1,"re-enable creates no implicit activation acknowledgement");
        var newIntent=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(newIntent,4));
        Check(Convert.ToInt64(await Scalar("SELECT OperationalGeneration FROM configuration.RuntimeActivationAcknowledgement WHERE ActivationId=@id",("@id",newIntent.ActivationId)))==4,"explicit replacement activation can acknowledge current enabled generation");
        // Suspension-first ordering: a queued runtime acknowledgement observes committed suspended authority.
        var queuedIntent=await Intent();var queuedAck=Ack(queuedIntent,4);
        await Exec("CREATE TRIGGER security.Fixture_OperationalBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:operational:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'fixture barrier failed',1; END;");
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:operational:audit-barrier");
            var holding=suspending.SuspendMachineAsync(holdToken,await Suspension());Task<byte[]>? pending=null;
            try{await WaitForLock("fc2machine-holder","S");pending=acknowledging.AcknowledgeActivationAsync(pair.Channel,queuedAck);await WaitForLock("fc2machine-ack","X");Check(!pending.IsCompleted,"runtime acknowledgement waits behind uncommitted suspension");}
            finally{await tx.CommitAsync();}
            await holding;await Denied(()=>pending??throw new InvalidOperationException("ack not started"),"suspension-first ordering denies queued acknowledgement after commit");
        }
        await Exec("DROP TRIGGER security.Fixture_OperationalBarrier");
        var retiredProposal=await Proposal();await service.ProposeMachineEnableAsync(token,retiredProposal);await Exec("UPDATE [master].Machine SET IsRetired=1 WHERE MachineId=@machine",("@machine",machine));
        await Denied(async()=>await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),retiredProposal.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"retired target"),"retired machine cannot be re-enabled");
        await Exec("UPDATE [master].Machine SET IsRetired=0 WHERE MachineId=@machine",("@machine",machine));
        try{await Exec("DELETE configuration.MachineOperationalTransition");throw new InvalidOperationException("mutable history");}catch(SqlException error)when(error.Number==51040){Check(true,"machine operational history is immutable");}
        try{await Exec("DELETE configuration.MachineEnableProposal");throw new InvalidOperationException("mutable proposal");}catch(SqlException error)when(error.Number==51040){Check(true,"enable proposals and original session binding are immutable");}
        await Grant(holder,"machine.suspend",AuthorizationScope.Machine(site,otherMachine));await Grant(proposer,"commissioning.read",AuthorizationScope.Machine(site,otherMachine));
        await service.SuspendMachineAsync(holdToken,new(Guid.NewGuid(),Guid.NewGuid(),site,otherMachine,null,0,await Epoch(),"hold uncommissioned machine"));
        var heldUnknown=await service.ReadMachineOperationalStateAsync(token,site,otherMachine);
        Check(heldUnknown.Status=="suspended"&&heldUnknown.Generation==1,"safety operator may explicitly suspend an unknown machine without enabling it");
        var sampled=activities.Where(activity=>activity.GetTagItem("security.machine.operation") is not null&&activity.GetTagItem("security.result")?.ToString()=="completed").ToArray();
        var roundtrips=sampled.Max(activity=>Convert.ToInt64(activity.GetTagItem("security.sql.roundtrips")));
        Check(roundtrips<=30,"machine operational commands retain bounded full-path SQL roundtrips");Console.WriteLine($"MEASURE: machine operational authority maxRoundtrips={roundtrips}; explicit single-machine commands, not production capacity");
        return count;

        async Task<MachineOperationalSnapshot> Current()=>await service.ReadMachineOperationalStateAsync(token,site,machine);
        async Task<MachineEnableProposalRequest> Proposal()
        {var current=await Current();var expiry=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(minute,10,SYSUTCDATETIME()),'+00:00')"))!;return new(Guid.NewGuid(),Guid.NewGuid(),site,machine,current.StateId,current.Generation,await Epoch(),expiry,"request explicit machine enable");}
        async Task<MachineSuspensionRequest> Suspension(){var current=await Current();return new(Guid.NewGuid(),Guid.NewGuid(),site,machine,current.StateId,current.Generation,await Epoch(),"safety suspension");}
        async Task Grant(Guid person,string permission,AuthorizationScope scope)
        {var parent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE GranteeId=@admin AND Permission=@permission",("@admin",admin),("@permission",permission)))!;var grant=Guid.NewGuid();await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),grant,person,parent,permission,scope,true,false,now,now.AddDays(1),await Epoch(),"grant fixture authority"));grants[(person,permission)]=grant;}
        async Task Session(byte[] session,Guid principal)=>await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",session),("@actor",principal));
        async Task<(PackageDefinition,Guid)> Package()
        {
            var timezone=Guid.NewGuid();var schedule=Guid.NewGuid();var breaks=Guid.NewGuid();var bytes=Encoding.UTF8.GetBytes("{}");var hash=SHA256.HashData(bytes);
            await Exec("INSERT configuration.SiteTimezoneRevision(TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,RulesBytes,RulesSha256,CanonicalBytes,ContentHash) VALUES(@timezone,@site,N'Etc/UTC','fixture','fixture','reject','reject',@bytes,@hash,@bytes,@hash); INSERT configuration.SharedScheduleRevision VALUES(@schedule,@site,@bytes,@hash); INSERT configuration.SharedBreakPolicyRevision VALUES(@break,@site,@bytes,@hash);",("@timezone",timezone),("@schedule",schedule),("@break",breaks),("@site",site),("@bytes",bytes),("@hash",hash));
            var package=new PackageDefinition(Guid.NewGuid(),Guid.NewGuid(),1,site,new(timezone,hash),[new(machine,line,now.AddMinutes(-5),null,new(schedule,hash),new(breaks,hash),[new(Guid.NewGuid(),"counter","fixture","fixture:machine","opaque",[65],"identity-v1")],["counter-reading"])]);
            await service.SubmitConfigurationAsync(token,Guid.NewGuid(),package,await Epoch(),"operational fixture package");var review=Guid.NewGuid();await service.ReviewConfigurationAsync(reviewToken,new(Guid.NewGuid(),review,site,package.RevisionId,PackageCanonicalizer.Hash(package),true,await Epoch(),"independent package review"));return(package,review);
        }
        async Task<ActivationRequestCommand> Intent()
        {
            Guid? prior=null;long generation=0;await using(var q=new SqlCommand("SELECT ActivationId,ActivationGeneration FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",c)){q.Parameters.AddWithValue("@machine",machine);await using var r=await q.ExecuteReaderAsync();if(await r.ReadAsync()){prior=r.GetGuid(0);generation=r.GetInt64(1);}}
            var request=new ActivationRequestCommand(Guid.NewGuid(),Guid.NewGuid(),site,machine,package.RevisionId,PackageCanonicalizer.Hash(package),review,f.Payload.RuntimeIdentityId,assignment,1,1,now.AddMinutes(-1),null,generation,prior,await Epoch(),"explicit activation request");
            await service.RequestActivationAsync(token,request);return request with{ExpectedActivationGeneration=generation+1};
        }
        ActivationAcknowledgementRequest Ack(ActivationRequestCommand request,long operating)=>new(Guid.NewGuid(),Guid.NewGuid(),site,machine,request.ActivationId,request.ExpectedActivationGeneration,request.PackageRevisionId,request.ContentHash,request.ApprovalReviewId,request.RuntimeIdentityId,request.AssignmentId,1,1,operating,"runtime exact application acknowledgement");
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task Hold(SqlConnection connection,SqlTransaction transaction,string resource){await using var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'fixture lock failed',1;",connection,transaction);q.Parameters.AddWithValue("@resource",resource);await q.ExecuteNonQueryAsync();}
        async Task WaitForLock(string app,string mode){for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("Expected operational SQL wait "+app+"/"+mode);}
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);await q.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand q,params (string Name,object Value)[] args){foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
