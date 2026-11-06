using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class ActivationRequestAcceptance
{
    private sealed record Fixture(PackageDefinition Package,Guid Review,Guid Runtime,Guid Assignment,Guid SeedOperation);
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var site=Guid.NewGuid();var line=Guid.NewGuid();var author=Guid.NewGuid();var reviewer=Guid.NewGuid();var controller=Guid.NewGuid();var admin=Guid.NewGuid();var stranger=Guid.NewGuid();var scopedController=Guid.NewGuid();
        var tokens=new Dictionary<Guid,byte[]>();var policyBytes=Encoding.UTF8.GetBytes("{}");var policyHash=SHA256.HashData(policyBytes);
        await Exec("INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Activation fixture'); INSERT [master].ProductionLine(ProductionLineId,SiteId,DisplayName) VALUES(@line,@site,N'Fixture line'); UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1;",("@site",site),("@line",line));
        foreach(var actor in new[]{author,reviewer,controller,admin,stranger,scopedController})
        {
            var token=RandomNumberGenerator.GetBytes(32);tokens.Add(actor,token);
            await Exec("INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@actor,'H',0x61637469766174696F6E,@subject); INSERT security.SessionAuthority VALUES(@token,@actor,@from,@until,N'fixture-only',0)",("@actor",actor),("@subject",actor.ToByteArray()),("@token",token),("@from",now.AddMinutes(-1)),("@until",now.AddHours(1)));
        }
        foreach(var permission in new[]{"configuration.edit","configuration.submit"})await Grant(author,permission);
        await Grant(reviewer,"configuration.review");await Grant(controller,"configuration.activate");await Grant(admin,"security.access.manage");
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var writer=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2activation-writer"}.ConnectionString,target.InstallationId);
        var fixture=await Prepare();var request=await Request(fixture);
        await Denied(()=>service.RequestActivationAsync(tokens[admin],request),"security administrator cannot implicitly request activation");
        await Denied(()=>service.RequestActivationAsync(tokens[author],request),"package authorship does not grant activation authority");
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).RequestActivationAsync(tokens[controller],request),"activation checks installation identity");
        await Exec("UPDATE security.InstallationState SET IsSealed=1 WHERE Singleton=1");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request),"sealed installation blocks activation intent");
        await Exec("UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{ContentHash=new byte[32]}),"activation binds exact approved package hash");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{ApprovalReviewId=Guid.NewGuid()}),"activation cannot substitute another approval identity");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{MachineId=Guid.NewGuid()}),"activation cannot expand approved machine scope");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{AssignmentGeneration=2}),"assignment generation is checked independently");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{CredentialGeneration=2}),"credential generation is checked independently");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{RuntimeIdentityId=Guid.NewGuid()}),"caller runtime identity cannot establish assignment authority");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{EffectiveFrom=now.AddHours(-2)}),"activation interval cannot start before package or assignment interval");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{EffectiveFrom=now.AddMinutes(-20),EffectiveTo=now.AddMinutes(-10)}),"already expired activation interval rejects");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request with{EffectiveFrom=now.AddDays(2)}),"credential must cover the first intended use");
        await Conflict(()=>service.RequestActivationAsync(tokens[controller],request with{ExpectedSecurityEpoch=long.MaxValue}),"activation checks expected security epoch");
        await Conflict(()=>service.RequestActivationAsync(tokens[controller],request with{ExpectedActivationGeneration=1}),"initial request checks expected activation generation");
        var results=await Task.WhenAll(service.RequestActivationAsync(tokens[controller],request),service.RequestActivationAsync(tokens[controller],request));
        Check(results[0].SequenceEqual(results[1]),"concurrent exact activation retries return one pending result");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest WHERE ActivationId=@id",("@id",request.ActivationId)))==1,"exact retries append one durable activation intent");
        Check(Convert.ToInt64(await Scalar("SELECT ActivationGeneration FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",("@machine",request.MachineId)))==1,"initial pending request advances its own generation to one");
        Check(Encoding.UTF8.GetString(results[0]).Contains("pending-acknowledgement",StringComparison.Ordinal),"request result explicitly requires future acknowledgement");
        Check((await service.ReadActivationOperationAsync(tokens[controller],request.OperationId)).SequenceEqual(results[0]),"requester retrieves its result under current activation authority");
        await Denied(()=>service.ReadActivationOperationAsync(tokens[author],request.OperationId),"another actor cannot retrieve a private activation result");
        await Conflict(()=>service.RequestActivationAsync(tokens[controller],request with{Reason="changed"}),"activation operation fingerprint is immutable");
        var next=await Request(fixture,1,request.ActivationId);
        await Conflict(()=>service.RequestActivationAsync(tokens[controller],next with{SupersedesActivationId=null}),"replacement requires exact prior pending request");
        var competing=next with{OperationId=Guid.NewGuid(),ActivationId=Guid.NewGuid()};
        var winners=await Task.WhenAll(Attempt(next),Attempt(competing));Check(winners.Count(x=>x)==1,"competing updates to one head have exactly one committed winner");
        var head=(Guid)(await Scalar("SELECT ActivationId FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",("@machine",request.MachineId)))!;
        Check((await service.RequestActivationAsync(tokens[controller],request)).SequenceEqual(results[0])&&(Guid)(await Scalar("SELECT ActivationId FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",("@machine",request.MachineId)))! ==head,"historical retry never restores a superseded pending head");
        await service.RevokeActivationRequestAsync(tokens[controller],Guid.NewGuid(),request.ActivationId,site,request.MachineId,await Epoch(),"revoke old intent");
        Check((Guid)(await Scalar("SELECT ActivationId FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",("@machine",request.MachineId)))! ==head,"revoking an old request cannot revoke a replacement head");
        await Immutable("DELETE configuration.ActivationRequest","activation intent history is immutable");
        var rejected=await Prepare(approved:false);
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(rejected)),"a rejected package cannot produce activation intent");
        await ForeignKey(()=>Exec("""
            INSERT configuration.ActivationRequest(ActivationId,SiteId,MachineId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,ApprovalDecision,RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration,EffectiveFrom,ActorPrincipalId,OperationId)
            VALUES(@id,@site,@machine,1,@revision,@hash,@review,'approved',@runtime,@assignment,1,1,@from,@actor,@op)
            """,("@id",Guid.NewGuid()),("@site",site),("@machine",rejected.Package.Machines[0].MachineId),("@revision",rejected.Package.RevisionId),
            ("@hash",PackageCanonicalizer.Hash(rejected.Package)),("@review",rejected.Review),("@runtime",rejected.Runtime),("@assignment",rejected.Assignment),("@from",now),("@actor",admin),("@op",rejected.SeedOperation)),"SQL approval pin independently rejects a rejected review");
        var scopedFixture=await Prepare();await Grant(scopedController,"configuration.activate",scopedFixture.Package.Machines[0].MachineId);
        await service.RequestActivationAsync(tokens[scopedController],await Request(scopedFixture));Check(true,"machine-only activation grant authorizes its exact assigned scope");
        await Denied(async()=>await service.RequestActivationAsync(tokens[scopedController],await Request(rejected)),"machine-only controller cannot use another machine scope");
        var missing=await Prepare(seedRuntime:false);
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(missing)),"missing committed runtime authority fails closed");
        var rollback=await Prepare();var rollbackRequest=await Request(rollback);var epoch=await Epoch();await AuditFailure();
        await SqlFailure(()=>service.RequestActivationAsync(tokens[controller],rollbackRequest),"activation audit failure aborts request and head together");
        await Exec("DROP TRIGGER security.Fixture_ActivationFailure");
        Check(Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM configuration.ActivationRequest WHERE ActivationId=@id)+(SELECT COUNT(*) FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine)+(SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op)",("@id",rollbackRequest.ActivationId),("@machine",rollbackRequest.MachineId),("@op",rollbackRequest.OperationId)))==0&&await Epoch()==epoch,"request head operation and epoch roll back atomically");
        await service.RequestActivationAsync(tokens[controller],rollbackRequest);
        var revokeOp=Guid.NewGuid();epoch=await Epoch();await AuditFailure();
        await SqlFailure(()=>service.RevokeActivationRequestAsync(tokens[controller],revokeOp,rollbackRequest.ActivationId,site,rollbackRequest.MachineId,epoch,"revoke rollback fixture"),"intent revocation audit failure aborts whole operation");
        await Exec("DROP TRIGGER security.Fixture_ActivationFailure");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequestRevocation WHERE ActivationId=@id",("@id",rollbackRequest.ActivationId)))==0&&await Epoch()==epoch,"failed intent revocation preserves prior pending request");
        var revocations=await Task.WhenAll(service.RevokeActivationRequestAsync(tokens[controller],revokeOp,rollbackRequest.ActivationId,site,rollbackRequest.MachineId,epoch,"revoke rollback fixture"),service.RevokeActivationRequestAsync(tokens[controller],revokeOp,rollbackRequest.ActivationId,site,rollbackRequest.MachineId,epoch,"revoke rollback fixture"));
        Check(revocations[0].SequenceEqual(revocations[1])&&Encoding.UTF8.GetString(revocations[0]).Contains("not-runtime-stop",StringComparison.Ordinal),"exact intent revocation retries do not claim runtime stop");
        var rotated=await Prepare();var old=await Request(rotated);await service.RequestActivationAsync(tokens[controller],old);
        await Exec("INSERT security.RuntimeCredentialGeneration VALUES(@runtime,2,@hash,@from,@until,@op); UPDATE security.RuntimeCredentialState SET CredentialGeneration=2 WHERE RuntimeIdentityId=@runtime",("@runtime",rotated.Runtime),("@hash",SHA256.HashData([2])),("@from",now.AddHours(-1)),("@until",now.AddDays(1)),("@op",rotated.SeedOperation));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(rotated,1,old.ActivationId)),"rotation rejects an old credential generation even with unchanged package bytes");
        var rebound=(await Request(rotated,1,old.ActivationId)) with{CredentialGeneration=2};await service.RequestActivationAsync(tokens[controller],rebound);
        Check(Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM configuration.ActivationRequest WHERE ActivationId=@id",("@id",rebound.ActivationId)))==2,"explicit replacement pins the new credential without transferring acknowledgement");
        var assignment2=Guid.NewGuid();
        await Exec("INSERT configuration.RuntimeAssignment VALUES(@assignment,@site,@machine,@runtime,2,@from,NULL,@op); UPDATE configuration.RuntimeAssignmentState SET AssignmentId=@assignment,AssignmentGeneration=2 WHERE MachineId=@machine",("@assignment",assignment2),("@site",site),("@machine",old.MachineId),("@runtime",rotated.Runtime),("@from",now.AddHours(-1)),("@op",rotated.SeedOperation));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],(await Request(rotated,2,rebound.ActivationId)) with{CredentialGeneration=2}),"assignment rotation rejects its old independent generation");
        var reassigned=(await Request(rotated,2,rebound.ActivationId)) with{CredentialGeneration=2,AssignmentId=assignment2,AssignmentGeneration=2};await service.RequestActivationAsync(tokens[controller],reassigned);
        Check(Convert.ToInt64(await Scalar("SELECT AssignmentGeneration FROM configuration.ActivationRequest WHERE ActivationId=@id",("@id",reassigned.ActivationId)))==2,"new assignment identity and generation are durably pinned");
        var revokedCredential=await Prepare();await Exec("INSERT security.RuntimeCredentialRevocation VALUES(@runtime,1,@op)",("@runtime",revokedCredential.Runtime),("@op",revokedCredential.SeedOperation));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(revokedCredential)),"committed credential revocation blocks request creation");
        var revokedAssignment=await Prepare();await Exec("INSERT configuration.RuntimeAssignmentRevocation VALUES(@assignment,@op)",("@assignment",revokedAssignment.Assignment),("@op",revokedAssignment.SeedOperation));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(revokedAssignment)),"committed assignment revocation blocks request creation");
        var revokedApproval=await Prepare();await Exec("INSERT configuration.PackageApprovalRevocation VALUES(@review,@op)",("@review",revokedApproval.Review),("@op",revokedApproval.SeedOperation));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(revokedApproval)),"explicit approval withdrawal blocks request creation");
        var revokedRuntime=await Prepare();await Exec("INSERT security.PrincipalRevocation VALUES(@runtime,@op,DEFAULT)",("@runtime",revokedRuntime.Runtime),("@op",revokedRuntime.SeedOperation));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(revokedRuntime)),"revoked runtime principal blocks request creation");
        var retired=await Prepare();await Exec("UPDATE [master].Machine SET IsRetired=1 WHERE MachineId=@machine",("@machine",retired.Package.Machines[0].MachineId));
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(retired)),"retired machine cannot acquire activation intent");
        var bounded=await Prepare();
        var invalidBounds=await Request(bounded);
        await Invalid(()=>service.RequestActivationAsync(tokens[controller],invalidBounds with{EffectiveTo=invalidBounds.EffectiveFrom}),"empty effective interval rejects before SQL");
        var finite=await Prepare(packageUntil:now.AddMinutes(30),assignmentUntil:now.AddMinutes(20));
        var finiteRequest=await Request(finite);
        await Denied(()=>service.RequestActivationAsync(tokens[controller],finiteRequest),"open-ended request cannot exceed finite package interval");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],finiteRequest with{EffectiveTo=now.AddMinutes(30)}),"request interval cannot exceed finite assignment interval");
        await service.RequestActivationAsync(tokens[controller],finiteRequest with{EffectiveTo=now.AddMinutes(20)});Check(true,"half-open request may end exactly at the assignment boundary");
        var expiredCredential=await Prepare(credentialUntil:now);
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(expiredCredential)),"expired credential generation cannot receive pending intent");
        // Selected-machine locking must not grow to every machine in a shared package.
        var measurements=new ConcurrentQueue<(long Trips,int Locks,double Elapsed)>();
        var single=await Prepare();var large=await Prepare(machineCount:100);
        using(var listener=new ActivityListener{ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllData,
            ActivityStopped=a=>{if(a.OperationName=="security.transaction")measurements.Enqueue((Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")),Convert.ToInt32(a.GetTagItem("security.configuration.lock.count")),Convert.ToDouble(a.GetTagItem("security.elapsed.ms"))));}})
        {
            ActivitySource.AddActivityListener(listener);await service.RequestActivationAsync(tokens[controller],await Request(single));var small=measurements.Single();measurements.Clear();
            await service.RequestActivationAsync(tokens[controller],await Request(large));var big=measurements.Single();
            Check(small.Trips>0&&small.Trips==big.Trips&&big.Trips<=24,"activation SQL roundtrip count is constant for 1- and 100-machine approved packages");
            Check(small.Locks==4&&big.Locks==4,"per-machine intent locks only its three pins and target machine");
            Check(big.Elapsed<5000,"selected-machine activation request stays within CI five-second ceiling");
            Console.WriteLine($"MEASURE: activation request packageMachines=100 requestedMachines=1 roundtrips={big.Trips} locks={big.Locks} elapsedMs={big.Elapsed:F2}; synthetic SQL, not runtime activation capacity");
        }
        foreach(var kind in new[]{"timezone","schedule","break"})
        {
            var policyFixture=await Prepare();await RevokePolicy(policyFixture,kind,now.AddMinutes(-1));
            await Denied(async()=>await service.RequestActivationAsync(tokens[controller],await Request(policyFixture)),"authoritative "+kind+" safety revocation blocks a pending request");
        }
        var scheduled=await Prepare();await RevokePolicy(scheduled,"break",now.AddHours(1));
        await service.RequestActivationAsync(tokens[controller],(await Request(scheduled)) with{EffectiveFrom=now.AddMinutes(20)});
        await Denied(async()=>await service.RequestActivationAsync(tokens[controller],(await Request(scheduled,1,(Guid)(await Scalar("SELECT ActivationId FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",("@machine",scheduled.Package.Machines[0].MachineId)))!)) with{EffectiveFrom=now.AddHours(2)}),"known scheduled safety revocation blocks later first use");
        var expiring=await Prepare();var expiringToken=RandomNumberGenerator.GetBytes(32);await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,@from,DATEADD(second,3,SYSUTCDATETIME()),N'fixture-expiry',0)",("@token",expiringToken),("@actor",controller),("@from",now.AddMinutes(-1)));
        var expiredRequest=await Request(expiring);
        await using(var holder=new SqlConnection(target.ConnectionString))
        {
            await holder.OpenAsync();await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();await HoldMachine(holder,tx,expiredRequest.MachineId);
            var pending=writer.RequestActivationAsync(expiringToken,expiredRequest);
            try{await WaitForLock("fc2activation-writer","X");var expired=false;for(var i=0;i<250;i++){if(Convert.ToInt32(await Scalar("SELECT CASE WHEN ExpiresAt<=SYSUTCDATETIME() THEN 1 ELSE 0 END FROM security.SessionAuthority WHERE TokenHash=@token",("@token",expiringToken)))==1){expired=true;break;}await Task.Delay(20);}Check(expired,"SQL clock observes expiry during activation machine-lock wait");}
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"activation rechecks session expiry after all authority locks");
        }
        // Maintenance fixture models a trusted upstream credential update, not public enrollment.
        var racing=await Prepare();var raceRequest=await Request(racing);
        await using(var holder=new SqlConnection(target.ConnectionString))
        {
            await holder.OpenAsync();await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();await HoldMachine(holder,tx,raceRequest.MachineId);
            var pending=writer.RequestActivationAsync(tokens[controller],raceRequest);
            try
            {
                await WaitForLock("fc2activation-writer","X");
                await using var q=new SqlCommand("INSERT security.RuntimeCredentialRevocation VALUES(@runtime,1,@op)",holder,tx);Add(q,("@runtime",racing.Runtime),("@op",racing.SeedOperation));await q.ExecuteNonQueryAsync();
            }
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"queued intent reads fresh committed prerequisite revocation after machine gate wait");
        }
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest WHERE ActivationId=@id",("@id",raceRequest.ActivationId)))==0,"revocation-first race leaves no durable activation intent");
        var grantRace=await Prepare();var grantRaceRequest=await Request(grantRace);
        var activateGrant=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE GranteeId=@actor AND Permission='configuration.activate'",("@actor",controller)))!;
        await Exec("CREATE TRIGGER security.Fixture_ActivationBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:activation:test-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'Fixture revocation barrier failed.',1; END;");
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:activation:test-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture barrier failed.',1;",barrier,tx))await q.ExecuteNonQueryAsync();
            var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2activation-revoker"}.ConnectionString,target.InstallationId);
            var revoking=revoker.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),activateGrant,grantRaceRequest.ExpectedSecurityEpoch,"revoke activation controller");Task<byte[]>? pending=null;
            try
            {
                await WaitForLock("fc2activation-revoker","S");pending=writer.RequestActivationAsync(tokens[controller],grantRaceRequest with{ExpectedSecurityEpoch=grantRaceRequest.ExpectedSecurityEpoch+1});
                await WaitForLock("fc2activation-writer","X");Check(!pending.IsCompleted,"activation request waits behind actual G2 grant revocation");
            }
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>pending??throw new InvalidOperationException("Missing activation grant race."),"G2 revocation committed first denies queued activation request");
        }
        await Exec("DROP TRIGGER security.Fixture_ActivationBarrier");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest WHERE ActivationId=@id",("@id",grantRaceRequest.ActivationId)))==0,"G2-revoked queued intent has no durable request");
        await Denied(()=>service.RequestActivationAsync(tokens[controller],request),"completed request replay requires current activation permission");
        await Denied(()=>service.ReadActivationOperationAsync(tokens[controller],request.OperationId),"revoked controller cannot retrieve historical activation result");
        return count;

        async Task<Fixture> Prepare(bool approved=true,bool seedRuntime=true,int machineCount=1,DateTimeOffset? packageUntil=null,DateTimeOffset? assignmentUntil=null,DateTimeOffset? credentialUntil=null)
        {
            var timezone=Guid.NewGuid();var schedule=Guid.NewGuid();var breaks=Guid.NewGuid();var runtime=Guid.NewGuid();var seedOp=Guid.NewGuid();
            var pairs=Enumerable.Range(0,machineCount).Select(_=>new{machineId=Guid.NewGuid(),assignmentId=Guid.NewGuid()}).ToArray();
            await Exec("""
                INSERT configuration.SiteTimezoneRevision(TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,RulesBytes,RulesSha256,CanonicalBytes,ContentHash)
                    VALUES(@timezone,@site,N'Etc/UTC','fixture','fixture','reject','reject',@bytes,@hash,@bytes,@hash);
                INSERT configuration.SharedScheduleRevision VALUES(@schedule,@site,@bytes,@hash);
                INSERT configuration.SharedBreakPolicyRevision VALUES(@break,@site,@bytes,@hash);
                INSERT [master].Machine(MachineId,SiteId,DisplayName) SELECT machineId,@site,N'Synthetic machine' FROM OPENJSON(@pairs) WITH(machineId uniqueidentifier '$.machineId');
                """,("@timezone",timezone),("@schedule",schedule),("@break",breaks),("@site",site),("@bytes",policyBytes),("@hash",policyHash),("@pairs",JsonSerializer.Serialize(pairs)));
            var machines=pairs.Select(pair=>new MachineConfiguration(pair.machineId,line,now.AddHours(-1),packageUntil,new(schedule,policyHash),new(breaks,policyHash),
                [new(Guid.NewGuid(),"counter","fixture","fixture:runtime","opaque",[0,65,255,32],"identity-v1")],["counter-reading"])).ToArray();
            var package=new PackageDefinition(Guid.NewGuid(),Guid.NewGuid(),1,site,new(timezone,policyHash),machines);
            await service.SubmitConfigurationAsync(tokens[author],Guid.NewGuid(),package,await Epoch(),"activation fixture submission");
            var review=Guid.NewGuid();await service.ReviewConfigurationAsync(tokens[reviewer],new(Guid.NewGuid(),review,site,package.RevisionId,PackageCanonicalizer.Hash(package),approved,await Epoch(),"activation fixture review"));
            if(seedRuntime)
                await Exec("""
                    INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@runtime,'R',0x72756E74696D65,@subject);
                    INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                        VALUES(@op,@actor,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
                    INSERT security.SecurityAudit VALUES(NEWID(),@op,@actor,'fixture-runtime-seed',N'Synthetic trusted upstream fixture',DEFAULT);
                    INSERT security.RuntimeIdentity VALUES(@runtime,'R',@op);
                    INSERT security.RuntimeCredentialGeneration VALUES(@runtime,1,@hash,@from,@until,@op);
                    INSERT security.RuntimeCredentialState VALUES(@runtime,1);
                    INSERT configuration.RuntimeAssignment(AssignmentId,SiteId,MachineId,RuntimeIdentityId,AssignmentGeneration,EffectiveFrom,EffectiveTo,OperationId)
                        SELECT assignmentId,@site,machineId,@runtime,1,@from,@assignmentUntil,@op FROM OPENJSON(@pairs) WITH(machineId uniqueidentifier '$.machineId',assignmentId uniqueidentifier '$.assignmentId');
                    INSERT configuration.RuntimeAssignmentState(MachineId,SiteId,AssignmentId,AssignmentGeneration,RuntimeIdentityId)
                        SELECT machineId,@site,assignmentId,1,@runtime FROM OPENJSON(@pairs) WITH(machineId uniqueidentifier '$.machineId',assignmentId uniqueidentifier '$.assignmentId');
                    """,("@runtime",runtime),("@subject",runtime.ToByteArray()),("@op",seedOp),("@actor",admin),("@hash",SHA256.HashData([1])),("@from",now.AddHours(-1)),("@until",credentialUntil??now.AddDays(1)),("@assignmentUntil",(object?)assignmentUntil??DBNull.Value),("@site",site),("@pairs",JsonSerializer.Serialize(pairs)));
            return new(package,review,runtime,pairs[0].assignmentId,seedOp);
        }
        async Task<ActivationRequestCommand> Request(Fixture f,long generation=0,Guid? prior=null)=>new(Guid.NewGuid(),Guid.NewGuid(),site,f.Package.Machines[0].MachineId,f.Package.RevisionId,
            PackageCanonicalizer.Hash(f.Package),f.Review,f.Runtime,f.Assignment,1,1,now,null,generation,prior,await Epoch(),"request fixture activation");
        async Task<bool> Attempt(ActivationRequestCommand value){try{await service.RequestActivationAsync(tokens[controller],value);return true;}catch(SecurityConflictException){return false;}}
        async Task RevokePolicy(Fixture f,string kind,DateTimeOffset effective)
        {
            var machine=f.Package.Machines[0];await Exec("""
                INSERT configuration.PolicySafetyRevocation(RevocationId,SiteId,PolicyKind,TimezoneRevisionId,ScheduleRevisionId,BreakPolicyRevisionId,ContentHash,EffectiveFrom,OperationId)
                VALUES(NEWID(),@site,@kind,@timezone,@schedule,@break,@hash,@effective,@op);
                """,("@site",site),("@kind",kind),("@timezone",kind=="timezone"?f.Package.Timezone.RevisionId:DBNull.Value),
                ("@schedule",kind=="schedule"?machine.Schedule.RevisionId:DBNull.Value),("@break",kind=="break"?machine.BreakPolicy.RevisionId:DBNull.Value),("@hash",policyHash),("@effective",effective),("@op",f.SeedOperation));
        }
        async Task HoldMachine(SqlConnection connection,SqlTransaction transaction,Guid machine)
        {await using var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@machine,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture machine gate failed.',1;",connection,transaction);q.Parameters.AddWithValue("@machine",ConfigurationLockResources.Machine(target.InstallationId,machine));await q.ExecuteNonQueryAsync();}
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task Grant(Guid actor,string permission,Guid? machineId=null)=>await Exec("INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,SiteId,MachineId,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt) VALUES(NEWID(),@actor,@actor,'bootstrap',@permission,@kind,@site,@machine,1,0,@from,@until)",("@actor",actor),("@permission",permission),("@kind",permission=="security.access.manage"?"I":machineId.HasValue?"M":"S"),("@machine",(object?)machineId??DBNull.Value),("@site",permission=="security.access.manage"?DBNull.Value:site),("@from",now.AddMinutes(-1)),("@until",now.AddHours(1)));
        async Task AuditFailure()=>await Exec("CREATE TRIGGER security.Fixture_ActivationFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51107,'Activation fixture audit failure.',1; END;");
        async Task WaitForLock(string app,string mode)
        {for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("Expected activation gate wait: "+app+"/"+mode);}
        void Check(bool pass,string name){if(!pass)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Invalid(Func<Task> action,string name){try{await action();}catch(ArgumentException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task ForeignKey(Func<Task> action,string name){try{await action();}catch(SqlException e)when(e.Number==547){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Immutable(string sql,string name){try{await Exec(sql);}catch(SqlException e)when(e.Number==51040){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task SqlFailure(Func<Task> action,string name){try{await action();}catch(SqlException e)when(e.Number==51107){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {foreach(var(name,value)in values)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else if(value is DateTimeOffset instant)q.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=instant;else if(name=="@assignmentUntil")q.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=value;else if(name is "@site" or "@machine" or "@timezone" or "@schedule" or "@break")q.Parameters.Add(name,SqlDbType.UniqueIdentifier).Value=value;else q.Parameters.AddWithValue(name,value);}
}
