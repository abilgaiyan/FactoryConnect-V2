using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class SafetyRevocationAcceptance
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
        var enabling=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2safety-writer"}.ConnectionString,target.InstallationId);
        var suspending=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2machine-holder"}.ConnectionString,target.InstallationId);
        var acknowledging=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2safety-ack"}.ConnectionString,target.InstallationId);
        foreach(var(person,subject)in new[]{(proposer,"certificate-manager"),(approver,"independent-enabler"),(holder,"safety-holder")})await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),person,Issuer,subject,await Epoch(),"enroll fixture operator"));
        var grants=new Dictionary<(Guid,string),Guid>();
        foreach(var permission in new[]{"runtime.credential.manage","runtime.assign","configuration.edit","configuration.submit","configuration.activate","machine.enable","commissioning.read"})
            await Grant(proposer,permission,permission=="runtime.credential.manage"?AuthorizationScope.Installation:permission is "configuration.edit" or "configuration.submit"?AuthorizationScope.Site(site):AuthorizationScope.Machine(site,machine));
        await Grant(approver,"machine.enable",AuthorizationScope.Machine(site,machine));await Grant(approver,"configuration.review",AuthorizationScope.Site(site));await Grant(holder,"machine.suspend",AuthorizationScope.Machine(site,machine));
        await Grant(proposer,"policy.revoke",AuthorizationScope.Site(site));await Grant(approver,"policy.revoke",AuthorizationScope.Site(site));
        await Session(token,proposer);await Session(reviewToken,approver);await Session(holdToken,holder);
        var activities=new List<Activity>();using var listener=new ActivityListener{ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=activity=>{lock(activities)activities.Add(activity);}};ActivitySource.AddActivityListener(listener);
        await service.EnrollRuntimeCertificateAsync(token,f.Sign(f.Payload with{ExpectedEpoch=await Epoch()},certificate:tls.Certificate));
        var assignment=Guid.NewGuid();await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),assignment,site,machine,f.Payload.RuntimeIdentityId,1,now.AddMinutes(-5),null,0,null,await Epoch(),"assign operational fixture"));
        await using var pair=await tls.Connect();
        var(package,review)=await Package();
        var enable= new MachineEnableProposalRequest(Guid.NewGuid(),Guid.NewGuid(),site,machine,null,0,await Epoch(),now.AddMinutes(10),"enable safety fixture");
        await service.ProposeMachineEnableAsync(token,enable);await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),enable.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"independent enable");

        var baseProposal=await Proposal("schedule");
        await Denied(()=>service.ProposePolicyRevocationAsync(rootToken,baseProposal),"administrator cannot bypass dedicated policy permission");
        await Denied(()=>service.ProposePolicyRevocationAsync(holdToken,baseProposal),"machine suspension permission cannot revoke shared policy");
        await Denied(()=>service.ProposePolicyRevocationAsync(token,baseProposal with{ContentHash=RandomNumberGenerator.GetBytes(32)}),"policy revocation requires exact retained content hash");
        await Denied(()=>service.ProposePolicyRevocationAsync(token,baseProposal with{SiteId=Guid.NewGuid()}),"site grant cannot revoke another site's policy");
        try{await service.ProposePolicyRevocationAsync(token,baseProposal with{Classification="ordinary"});throw new InvalidOperationException("classification bypass");}catch(ArgumentException){Check(true,"safety classification cannot bypass independent approval");}
        await service.ProposePolicyRevocationAsync(token,baseProposal);
        var inspect=await service.ReadPolicyRevocationProposalAsync(reviewToken,baseProposal.ProposalId,site);
        Check(inspect.PolicyRevisionId==baseProposal.PolicyRevisionId&&inspect.ProposerId==proposer&&inspect.Reason==baseProposal.Reason,"independent reviewer inspects exact policy proposal");
        await Denied(async()=>await service.ApprovePolicyRevocationAsync(token,Guid.NewGuid(),baseProposal.ProposalId,Guid.NewGuid(),site,await Epoch(),"self approval"),"policy proposer cannot self approve");
        await service.RevokeGrantAsync(rootToken,Guid.NewGuid(),grants[(proposer,"policy.revoke")],await Epoch(),"revoke proposing permission");
        await Denied(async()=>await Approve(baseProposal),"approval checks current proposer grant ancestry");
        await Grant(proposer,"policy.revoke",AuthorizationScope.Site(site));
        var temporary=RandomNumberGenerator.GetBytes(32);await Session(temporary,proposer);var invalidSession=await Proposal("break");await service.ProposePolicyRevocationAsync(temporary,invalidSession);
        await service.RevokeSessionAsync(rootToken,Guid.NewGuid(),temporary,await Epoch(),"revoke proposing session");
        await Denied(async()=>await Approve(invalidSession),"approval checks original proposing session revocation");
        var schedule=await Proposal("schedule",now.AddMinutes(5));await service.ProposePolicyRevocationAsync(token,schedule);
        var revokeOp=Guid.NewGuid();var revokeId=Guid.NewGuid();var revokeEpoch=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_SafetyFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51111,'safety audit fixture',1; END;");
        try{await service.ApprovePolicyRevocationAsync(reviewToken,revokeOp,schedule.ProposalId,revokeId,site,revokeEpoch,"scheduled safety hold");throw new InvalidOperationException("audit expected");}catch(SqlException e)when(e.Number==51111){Check(true,"policy audit failure aborts mutation");}
        await Exec("DROP TRIGGER security.Fixture_SafetyFailure");
        Check(await Epoch()==revokeEpoch&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PolicySafetyRevocation"))==0&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op",("@op",revokeOp)))==0,"policy fact authority receipt and epoch roll back together");
        var retries=await Task.WhenAll(service.ApprovePolicyRevocationAsync(reviewToken,revokeOp,schedule.ProposalId,revokeId,site,revokeEpoch,"scheduled safety hold"),service.ApprovePolicyRevocationAsync(reviewToken,revokeOp,schedule.ProposalId,revokeId,site,revokeEpoch,"scheduled safety hold"));
        Check(retries[0].SequenceEqual(retries[1])&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PolicyRevocationAuthority"))==1,"concurrent exact safety approval retries commit one fact");
        Check((await service.ReadSafetyRevocationOperationAsync(reviewToken,revokeOp)).SequenceEqual(retries[0]),"current authorized actor retrieves historical safety receipt");
        await Denied(()=>service.ReadSafetyRevocationOperationAsync(token,revokeOp),"another actor cannot retrieve private safety receipt");
        await Conflict(()=>service.ApprovePolicyRevocationAsync(reviewToken,revokeOp,schedule.ProposalId,revokeId,site,revokeEpoch,"different body"),"safety operation identity binds exact request bytes");
        var live=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(live,1));Check(true,"future policy revocation permits currently effective intent and acknowledgement");
        await Denied(async()=>await Intent(now.AddMinutes(6)),"known scheduled policy revocation blocks later first use");
        var acceleration=await Proposal("schedule");await service.ProposePolicyRevocationAsync(token,acceleration);await Approve(acceleration);
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PolicySafetyRevocation"))==2,"immediate acceleration appends history without postponing scheduled revocation");
        await Denied(async()=>await Intent(),"effective schedule revocation blocks new activation");
        await service.ApprovePolicyRevocationAsync(reviewToken,revokeOp,schedule.ProposalId,revokeId,site,revokeEpoch,"scheduled safety hold");
        await Denied(async()=>await Intent(),"historical scheduled retry cannot restore a revoked policy");
        var later=await Proposal("schedule",now.AddMinutes(8));await service.ProposePolicyRevocationAsync(token,later);await Conflict(async()=>await Approve(later),"later proposal cannot postpone an existing safety cut");
        foreach(var kind in new[]{"timezone","break"})
        {
            (package,review)=await Package();var pending=await Intent();var proposal=await Proposal(kind);await service.ProposePolicyRevocationAsync(token,proposal);await Approve(proposal);
            await Denied(async()=>await Intent(),"effective "+kind+" revocation blocks activation");
            await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(pending,1)),"effective "+kind+" revocation blocks pending runtime acknowledgement");
        }
        // Let SQL time cross a scheduled cut while the policy lock is held. Approval must not backdate.
        (package,review)=await Package();var scheduledAt=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,2,SYSUTCDATETIME()),'+00:00')"))!;
        var delayed=await Proposal("break",scheduledAt);await service.ProposePolicyRevocationAsync(token,delayed);var delayedId=Guid.NewGuid();
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,ConfigurationLockResources.Policy(target.InstallationId,"break",delayed.PolicyRevisionId));
            var pending=enabling.ApprovePolicyRevocationAsync(reviewToken,Guid.NewGuid(),delayed.ProposalId,delayedId,site,await Epoch(),"late scheduling approval");
            try{await WaitForLock("fc2safety-writer","X");while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<scheduledAt)await Task.Delay(20);}
            finally{await tx.CommitAsync();}
            await pending;
        }
        Check((DateTimeOffset)(await Scalar("SELECT EffectiveFrom FROM configuration.PolicySafetyRevocation WHERE RevocationId=@id",("@id",delayedId)))!>scheduledAt,"approval after lock wait clamps effective cut to fresh SQL time");
        (package,review)=await Package();var expiry=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,2,SYSUTCDATETIME()),'+00:00')"))!;
        var expired=(await Proposal("timezone")) with{ExpiresAt=expiry};await service.ProposePolicyRevocationAsync(token,expired);
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,ConfigurationLockResources.Policy(target.InstallationId,"timezone",expired.PolicyRevisionId));
            var pending=enabling.ApprovePolicyRevocationAsync(reviewToken,Guid.NewGuid(),expired.ProposalId,Guid.NewGuid(),site,await Epoch(),"expiry after wait");
            try{await WaitForLock("fc2safety-writer","X");while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<expiry)await Task.Delay(20);}
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"expired policy proposal cannot approve after lock wait");
        }
        (package,review)=await Package();var withdrawal=await Withdrawal();
        await Denied(()=>service.WithdrawConfigurationApprovalAsync(rootToken,withdrawal),"administrator cannot implicitly withdraw approval");
        await Grant(proposer,"configuration.review",AuthorizationScope.Site(site));withdrawal=await Withdrawal();
        await Denied(()=>service.WithdrawConfigurationApprovalAsync(token,withdrawal),"author cannot withdraw own package even with review permission");
        await Denied(()=>service.WithdrawConfigurationApprovalAsync(reviewToken,withdrawal with{ContentHash=RandomNumberGenerator.GetBytes(32)}),"withdrawal binds exact approved revision hash and review");
        var waitingIntent=await Intent();withdrawal=await Withdrawal();var before=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_SafetyFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51111,'safety audit fixture',1; END;");
        try{await service.WithdrawConfigurationApprovalAsync(reviewToken,withdrawal);throw new InvalidOperationException("audit expected");}catch(SqlException e)when(e.Number==51111){Check(true,"withdrawal audit failure aborts mutation");}
        await Exec("DROP TRIGGER security.Fixture_SafetyFailure");
        Check(await Epoch()==before&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PackageApprovalRevocation"))==0,"withdrawal fact provenance receipt and epoch roll back atomically");
        var withdrawResults=await Task.WhenAll(service.WithdrawConfigurationApprovalAsync(reviewToken,withdrawal),service.WithdrawConfigurationApprovalAsync(reviewToken,withdrawal));
        Check(withdrawResults[0].SequenceEqual(withdrawResults[1]),"concurrent exact approval withdrawal retries commit once");
        await Denied(async()=>await Intent(),"withdrawn approval blocks fresh activation");
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(waitingIntent,1)),"withdrawn approval blocks pending runtime acknowledgement");
        await Conflict(async()=>await service.WithdrawConfigurationApprovalAsync(reviewToken,await Withdrawal()),"another operation cannot withdraw an already withdrawn review");
        // Both transaction orders use observed SQL application-lock waits, never task timing as commit proof.
        foreach(var ackFirst in new[]{false,true})
        {
            (package,review)=await Package();var pendingIntent=await Intent();var ack=Ack(pendingIntent,1);var race=await Withdrawal();
            await Exec("CREATE TRIGGER security.Fixture_SafetyBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:safety:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'fixture barrier failed',1; END;");
            await using(var barrier=new SqlConnection(target.ConnectionString))
            {
                await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:safety:audit-barrier");
                var first=ackFirst?acknowledging.AcknowledgeActivationAsync(pair.Channel,ack):enabling.WithdrawConfigurationApprovalAsync(reviewToken,race);Task<byte[]>? second=null;
                try{await WaitForLock(ackFirst?"fc2safety-ack":"fc2safety-writer","S");second=ackFirst?enabling.WithdrawConfigurationApprovalAsync(reviewToken,race with{ExpectedSecurityEpoch=race.ExpectedSecurityEpoch+1}):acknowledging.AcknowledgeActivationAsync(pair.Channel,ack);await WaitForLock(ackFirst?"fc2safety-writer":"fc2safety-ack","X");Check(!second.IsCompleted,"second authority command waits behind first uncommitted transaction");}
                finally{await tx.CommitAsync();}
                await first;if(ackFirst){await second!;Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeActivationAcknowledgement WHERE AcknowledgementId=@id",("@id",ack.AcknowledgementId)))==1,"acknowledgement committed before withdrawal remains immutable history");}
                else await Denied(()=>second!,"withdrawal-first ordering denies queued runtime acknowledgement");
            }
            await Exec("DROP TRIGGER security.Fixture_SafetyBarrier");
        }
        foreach(var ackFirst in new[]{false,true})
        {
            (package,review)=await Package();var pendingIntent=await Intent();var ack=Ack(pendingIntent,1);var race=await Proposal("schedule");await service.ProposePolicyRevocationAsync(token,race);var epoch=await Epoch();
            await Exec("CREATE TRIGGER security.Fixture_SafetyBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:safety:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'fixture barrier failed',1; END;");
            await using(var barrier=new SqlConnection(target.ConnectionString))
            {
                await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:safety:audit-barrier");
                var first=ackFirst?acknowledging.AcknowledgeActivationAsync(pair.Channel,ack):enabling.ApprovePolicyRevocationAsync(reviewToken,Guid.NewGuid(),race.ProposalId,Guid.NewGuid(),site,epoch,"policy race");Task<byte[]>? second=null;
                try{await WaitForLock(ackFirst?"fc2safety-ack":"fc2safety-writer","S");second=ackFirst?enabling.ApprovePolicyRevocationAsync(reviewToken,Guid.NewGuid(),race.ProposalId,Guid.NewGuid(),site,epoch+1,"policy race"):acknowledging.AcknowledgeActivationAsync(pair.Channel,ack);await WaitForLock(ackFirst?"fc2safety-writer":"fc2safety-ack","X");Check(!second.IsCompleted,"policy and acknowledgement share deterministic commit ordering");}
                finally{await tx.CommitAsync();}
                await first;if(ackFirst){await second!;Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeActivationAcknowledgement WHERE AcknowledgementId=@id",("@id",ack.AcknowledgementId)))==1,"acknowledgement-first policy revocation preserves historical acknowledgement");}
                else await Denied(()=>second!,"policy-first ordering denies queued runtime acknowledgement");
            }
            await Exec("DROP TRIGGER security.Fixture_SafetyBarrier");
        }
        // Scheduled cut becomes effective without a timer job or fact rewrite.
        (package,review)=await Package();var cut=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,3,SYSUTCDATETIME()),'+00:00')"))!;var timed=await Proposal("timezone",cut);
        await service.ProposePolicyRevocationAsync(token,timed);await Approve(timed);var timedIntent=await Intent();
        while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<cut)await Task.Delay(20);
        await Denied(()=>service.AcknowledgeActivationAsync(pair.Channel,Ack(timedIntent,1)),"scheduled cut blocks pending acknowledgement when SQL time reaches effective instant");
        await Denied(async()=>await Intent(),"scheduled cut blocks fresh activation after effective instant");
        foreach(var table in new[]{"PolicyRevocationProposal","PolicyRevocationAuthority","ApprovalWithdrawalAuthority"})
        {try{await Exec("DELETE configuration."+table);throw new InvalidOperationException("mutable safety history");}catch(SqlException e)when(e.Number==51040){Check(true,table+" is immutable");}}
        (package,review)=await Package(100);var batch=await Withdrawal();var watch=Stopwatch.StartNew();await service.WithdrawConfigurationApprovalAsync(reviewToken,batch);watch.Stop();
        var sample=activities.Last(a=>a.GetTagItem("security.safety.command")?.ToString()=="withdraw-approval"&&a.GetTagItem("security.result")?.ToString()=="completed");
        var trips=Convert.ToInt64(sample.GetTagItem("security.sql.roundtrips"));
        Check(trips<=30&&watch.Elapsed<TimeSpan.FromSeconds(5),"100-machine approval withdrawal retains bounded complete transaction cost");
        Console.WriteLine($"MEASURE: 100-machine approval withdrawal elapsedMs={watch.Elapsed.TotalMilliseconds:F2} roundtrips={trips}; disposable fixture, not production capacity");
        var successful=activities.Where(a=>a.GetTagItem("security.safety.command") is not null&&a.GetTagItem("security.result")?.ToString()=="completed").ToArray();
        var max=successful.Max(a=>Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")));Check(max<=30,"safety commands use bounded policy probes and batched resource locking");Console.WriteLine($"MEASURE: safety authority maxRoundtrips={max}");
        return count;

        async Task<PolicyRevocationProposalRequest> Proposal(string kind,DateTimeOffset? scheduled=null)
        {using var document=JsonDocument.Parse(PackageCanonicalizer.Serialize(package));var root=document.RootElement;var policy=kind=="timezone"?root.GetProperty("timezone"):root.GetProperty("machines")[0].GetProperty(kind=="schedule"?"schedule":"breakPolicy");return new(Guid.NewGuid(),Guid.NewGuid(),site,kind,policy.GetProperty("revisionId").GetGuid(),Convert.FromHexString(policy.GetProperty("contentHash").GetString()!),"safety-critical",scheduled,now.AddMinutes(10),await Epoch(),"explicit shared policy safety hold");}
        async Task<byte[]> Approve(PolicyRevocationProposalRequest proposal)=>await service.ApprovePolicyRevocationAsync(reviewToken,Guid.NewGuid(),proposal.ProposalId,Guid.NewGuid(),site,await Epoch(),"independent policy safety approval");
        async Task<ApprovalWithdrawalRequest> Withdrawal()=>new(Guid.NewGuid(),site,package.RevisionId,PackageCanonicalizer.Hash(package),review,await Epoch(),"withdraw exact approved package");
        async Task Grant(Guid person,string permission,AuthorizationScope scope)
        {var parent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE GranteeId=@admin AND Permission=@permission",("@admin",admin),("@permission",permission)))!;var grant=Guid.NewGuid();await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),grant,person,parent,permission,scope,true,false,now,now.AddDays(1),await Epoch(),"grant fixture authority"));grants[(person,permission)]=grant;}
        async Task Session(byte[] session,Guid principal)=>await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",session),("@actor",principal));
        async Task<(PackageDefinition,Guid)> Package(int size=1)
        {
            var timezone=Guid.NewGuid();var schedule=Guid.NewGuid();var breaks=Guid.NewGuid();var bytes=Encoding.UTF8.GetBytes("{}");var hash=SHA256.HashData(bytes);
            await Exec("INSERT configuration.SiteTimezoneRevision(TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,RulesBytes,RulesSha256,CanonicalBytes,ContentHash) VALUES(@timezone,@site,N'Etc/UTC','fixture','fixture','reject','reject',@bytes,@hash,@bytes,@hash); INSERT configuration.SharedScheduleRevision VALUES(@schedule,@site,@bytes,@hash); INSERT configuration.SharedBreakPolicyRevision VALUES(@break,@site,@bytes,@hash);",("@timezone",timezone),("@schedule",schedule),("@break",breaks),("@site",site),("@bytes",bytes),("@hash",hash));
            var machines=new List<MachineConfiguration>();
            for(var i=0;i<size;i++){var id=i==0?machine:Guid.NewGuid();if(i>0)await Exec("INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Batch fixture')",("@machine",id),("@site",site));machines.Add(new(id,line,now.AddMinutes(-5),null,new(schedule,hash),new(breaks,hash),[new(Guid.NewGuid(),"counter","fixture","fixture:machine","opaque",[65],"identity-v1")],["counter-reading"]));}
            var package=new PackageDefinition(Guid.NewGuid(),Guid.NewGuid(),1,site,new(timezone,hash),machines);
            await service.SubmitConfigurationAsync(token,Guid.NewGuid(),package,await Epoch(),"operational fixture package");var review=Guid.NewGuid();await service.ReviewConfigurationAsync(reviewToken,new(Guid.NewGuid(),review,site,package.RevisionId,PackageCanonicalizer.Hash(package),true,await Epoch(),"independent package review"));return(package,review);
        }
        async Task<ActivationRequestCommand> Intent(DateTimeOffset? from=null)
        {
            Guid? prior=null;long generation=0;await using(var q=new SqlCommand("SELECT ActivationId,ActivationGeneration FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",c)){q.Parameters.AddWithValue("@machine",machine);await using var r=await q.ExecuteReaderAsync();if(await r.ReadAsync()){prior=r.GetGuid(0);generation=r.GetInt64(1);}}
            var request=new ActivationRequestCommand(Guid.NewGuid(),Guid.NewGuid(),site,machine,package.RevisionId,PackageCanonicalizer.Hash(package),review,f.Payload.RuntimeIdentityId,assignment,1,1,from??now.AddMinutes(-1),null,generation,prior,await Epoch(),"explicit activation request");
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
