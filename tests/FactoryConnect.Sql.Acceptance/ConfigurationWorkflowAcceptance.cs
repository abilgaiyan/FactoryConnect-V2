using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class ConfigurationWorkflowAcceptance
{
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var template=ConfigurationAcceptance.Example();var site=template.SiteId;var machine=template.Machines[0];var policyBytes=Encoding.UTF8.GetBytes("{}");var policyHash=SHA256.HashData(policyBytes);
        var author=Guid.NewGuid();var reviewer=Guid.NewGuid();var admin=Guid.NewGuid();var editor=Guid.NewGuid();var stranger=Guid.NewGuid();var runtime=Guid.NewGuid();var tokens=new Dictionary<Guid,byte[]>();
        await Exec("""
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Workflow fixture');
            INSERT [master].ProductionLine(ProductionLineId,SiteId,DisplayName) VALUES(@line,@site,N'Fixture line');
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Fixture machine');
            INSERT configuration.SiteTimezoneRevision(TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,RulesBytes,RulesSha256,CanonicalBytes,ContentHash)
                VALUES(@timezone,@site,N'Etc/UTC','fixture','fixture','reject','reject',@bytes,@hash,@bytes,@hash);
            INSERT configuration.SharedScheduleRevision VALUES(@schedule,@site,@bytes,@hash);
            INSERT configuration.SharedBreakPolicyRevision VALUES(@break,@site,@bytes,@hash);
            UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1;
            """,("@site",site),("@line",machine.ProductionLineId),("@machine",machine.MachineId),("@timezone",template.Timezone.RevisionId),
            ("@schedule",machine.Schedule.RevisionId),("@break",machine.BreakPolicy.RevisionId),("@bytes",policyBytes),("@hash",policyHash));
        foreach(var actor in new[]{author,reviewer,admin,editor,stranger,runtime})
        {
            var token=RandomNumberGenerator.GetBytes(32);tokens.Add(actor,token);
            await Exec("INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@p,@kind,0x776F726B666C6F77,@subject); INSERT security.SessionAuthority VALUES(@token,@p,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',0)",
                ("@p",actor),("@kind",actor==runtime?"R":"H"),("@subject",actor.ToByteArray()),("@token",token));
        }
        foreach(var permission in new[]{"configuration.edit","configuration.submit","configuration.review"})await Grant(author,permission);
        await Grant(reviewer,"configuration.review");await Grant(editor,"configuration.edit");await Grant(admin,"security.access.manage");
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        var writer=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2configuration-writer"}.ConnectionString,target.InstallationId);
        PackageDefinition Package()=>template with{PackageId=Guid.NewGuid(),RevisionId=Guid.NewGuid(),Machines=[machine with{Signals=[machine.Signals[0] with{MappingId=Guid.NewGuid(),AddressBytes=machine.Signals[0].AddressBytes.ToArray()}]}]};
        async Task<PackageReviewRequest> Review(PackageDefinition p,bool approve=true)=>new(Guid.NewGuid(),Guid.NewGuid(),site,p.RevisionId,PackageCanonicalizer.Hash(p),approve,await Epoch(),"independent fixture review");
        var package=Package();var op=Guid.NewGuid();
        await Denied(()=>service.SubmitConfigurationAsync(tokens[admin],op,package,0,"admin is not editor"),"security management grants no implicit configuration submission");
        await Denied(()=>service.SubmitConfigurationAsync(tokens[editor],op,package,0,"editor lacks submit"),"submission requires edit and submit permissions together");
        await Denied(()=>service.SubmitConfigurationAsync(tokens[runtime],op,package,0,"wrong principal kind"),"runtime cannot submit through human authority");
        await Denied(()=>new SqlSecurityAuthority(target.ConnectionString,Guid.NewGuid()).SubmitConfigurationAsync(tokens[author],op,package,0,"wrong installation"),"workflow validates installation identity");
        await Exec("UPDATE security.InstallationState SET IsSealed=1 WHERE Singleton=1");
        await Denied(()=>service.SubmitConfigurationAsync(tokens[author],op,package,0,"sealed"),"sealed installation blocks authenticated submission");
        await Exec("UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1");
        await Conflict(()=>service.SubmitConfigurationAsync(tokens[author],op,package,99,"stale"),"submission checks expected authority epoch");
        var results=await Task.WhenAll(service.SubmitConfigurationAsync(tokens[author],op,package,0,"submit fixture"),service.SubmitConfigurationAsync(tokens[author],op,package,0,"submit fixture"));
        Check(results[0].SequenceEqual(results[1]),"concurrent exact submissions return one immutable receipt");
        Check(await Epoch()==1,"submission retries advance authority epoch once");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PackageSubmission WHERE PackageRevisionId=@revision AND AuthorPrincipalId=@author AND SubmitterPrincipalId=@author AND ContentHash=@hash",("@revision",package.RevisionId),("@author",author),("@hash",PackageCanonicalizer.Hash(package))))==1,"submission records exact hash and authenticated creator provenance");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",op)))==1,"submission retries append one audit");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PackageReview"))==0,"submission does not approve configuration");
        await Conflict(()=>service.SubmitConfigurationAsync(tokens[author],op,package,0,"changed"),"submission operation fingerprint cannot change");
        Check((await service.ReadConfigurationOperationAsync(tokens[author],op)).SequenceEqual(results[0]),"creator retrieves submission receipt without security management");
        await Denied(()=>service.ReadConfigurationOperationAsync(tokens[reviewer],op),"another actor cannot retrieve private workflow receipt");
        var review=await Review(package);
        await Denied(()=>service.ReviewConfigurationAsync(tokens[author],review),"author cannot self-approve even with review permission");
        await Denied(()=>service.ReviewConfigurationAsync(tokens[admin],review),"administrator cannot review without explicit review permission");
        await Denied(()=>service.ReviewConfigurationAsync(tokens[reviewer],review with{ContentHash=new byte[32]}),"review binds the exact submitted hash");
        await Conflict(()=>service.ReviewConfigurationAsync(tokens[reviewer],review with{ExpectedEpoch=99}),"review rejects stale authority epoch");
        var approved=await Task.WhenAll(service.ReviewConfigurationAsync(tokens[reviewer],review),service.ReviewConfigurationAsync(tokens[reviewer],review));
        Check(approved[0].SequenceEqual(approved[1]),"concurrent exact review retries create one decision");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PackageReview WHERE PackageRevisionId=@revision AND ReviewerPrincipalId=@reviewer AND Decision='approved'",("@revision",package.RevisionId),("@reviewer",reviewer)))==1,"independent review produces exact approved decision");
        Check((await service.ReadConfigurationOperationAsync(tokens[reviewer],review.OperationId)).SequenceEqual(approved[0]),"reviewer retrieves its own receipt under current review permission");
        await Conflict(()=>service.ReviewConfigurationAsync(tokens[reviewer],review with{Approve=false}),"completed approval cannot be changed to rejection");
        await Conflict(async()=>await service.ReviewConfigurationAsync(tokens[reviewer],(await Review(package)) with{Approve=false}),"another review cannot overwrite a terminal decision");
        // Existing storage rows without an authenticated provenance event never qualify for review.
        var unattributed=Package();
        await using(var tx=(SqlTransaction)await c.BeginTransactionAsync())
        {await new SqlPackageRepository().InsertSubmittedAsync(c,tx,unattributed);await tx.CommitAsync();}
        await Denied(async()=>await service.ReviewConfigurationAsync(tokens[reviewer],await Review(unattributed)),"legacy or fixture submissions without provenance cannot be adopted as approval");
        await ForeignKey(()=>Exec("INSERT configuration.PackageSubmission(PackageRevisionId,SiteId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId,OperationId) VALUES(@revision,@site,@hash,@author,@author,@op)",
            ("@revision",unattributed.RevisionId),("@site",site),("@hash",PackageCanonicalizer.Hash(unattributed)),("@author",author),("@op",review.OperationId)),"submission SQL foreign key rejects a mismatched operation actor");
        var rejected=Package();await service.SubmitConfigurationAsync(tokens[author],Guid.NewGuid(),rejected,await Epoch(),"rejection fixture");
        await service.ReviewConfigurationAsync(tokens[reviewer],await Review(rejected,false));
        Check((string?)(await Scalar("SELECT Decision FROM configuration.PackageReview WHERE PackageRevisionId=@revision",("@revision",rejected.RevisionId)))=="rejected","independent rejection is retained as a terminal decision");
        await Immutable("DELETE configuration.PackageReview","review history is immutable");
        await Immutable("UPDATE configuration.PackageSubmission SET AuthorPrincipalId=NEWID()","submission provenance cannot be rewritten");
        var failed=Package();var failedOp=Guid.NewGuid();var epoch=await Epoch();
        await AuditFailure();
        await SqlFailure(()=>service.SubmitConfigurationAsync(tokens[author],failedOp,failed,epoch,"rollback submission"),"submission audit failure aborts content and provenance");
        await Exec("DROP TRIGGER security.Fixture_ConfigurationFailure");
        Check(Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@revision)+(SELECT COUNT(*) FROM configuration.PackageSubmission WHERE PackageRevisionId=@revision)+(SELECT COUNT(*) FROM configuration.PackageSignalMapping WHERE PackageRevisionId=@revision)+(SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op)",("@revision",failed.RevisionId),("@op",failedOp)))==0,"content mappings provenance and operation receipt roll back together");
        Check(await Epoch()==epoch,"failed submission preserves authority epoch");
        await service.SubmitConfigurationAsync(tokens[author],failedOp,failed,epoch,"rollback submission");
        await ForeignKey(()=>Exec("INSERT configuration.PackageReview(ReviewId,PackageRevisionId,SiteId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId,ReviewerPrincipalId,Decision,OperationId) VALUES(@review,@revision,@site,@hash,@author,@author,@author,'approved',@op)",
            ("@review",Guid.NewGuid()),("@revision",failed.RevisionId),("@site",site),("@hash",PackageCanonicalizer.Hash(failed)),("@author",author),("@op",failedOp)),"SQL constraint independently rejects self-review");
        var failedReview=await Review(failed);epoch=await Epoch();await AuditFailure();
        await SqlFailure(()=>service.ReviewConfigurationAsync(tokens[reviewer],failedReview),"review audit failure rolls back decision and result");
        await Exec("DROP TRIGGER security.Fixture_ConfigurationFailure");
        Check(Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM configuration.PackageReview WHERE PackageRevisionId=@revision)+(SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op)",("@revision",failed.RevisionId),("@op",failedReview.OperationId)))==0&&await Epoch()==epoch,"review rollback preserves prior submission and epoch");
        await service.ReviewConfigurationAsync(tokens[reviewer],failedReview);Check(true,"identical failed review can retry after rollback");
        // Hold the last policy key; SQL must not acquire machine locks before all policy locks.
        var snapshot=Package();var initial=PackageCanonicalizer.Serialize(snapshot);var snapshotOp=Guid.NewGuid();epoch=await Epoch();
        await using(var holder=new SqlConnection(target.ConnectionString))
        {
            await holder.OpenAsync();await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture lock failed.',1;",holder,tx))
            {q.Parameters.AddWithValue("@resource",ConfigurationLockResources.Policy(target.InstallationId,"timezone",template.Timezone.RevisionId));await q.ExecuteNonQueryAsync();}
            var pending=writer.SubmitConfigurationAsync(tokens[author],snapshotOp,snapshot,epoch,"snapshot fixture");
            try
            {
                await WaitForLock("fc2configuration-writer","S");
                Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name='fc2configuration-writer' AND l.resource_type='APPLICATION' AND l.request_status='GRANT' AND CHARINDEX('fc2:machine:',l.resource_description)>0"))==0,"all policy locks precede any machine lock");
                snapshot.Machines[0].Signals[0].AddressBytes[0]^=1;
            }
            finally{await tx.CommitAsync();}
            await pending;
        }
        Check(((byte[])(await Scalar("SELECT CanonicalBytes FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@revision",("@revision",snapshot.RevisionId)))!).SequenceEqual(initial),"caller buffer mutation cannot change queued submitted content");
        await Conflict(()=>service.SubmitConfigurationAsync(tokens[author],snapshotOp,snapshot,epoch,"snapshot fixture"),"changed source bytes cannot replay original operation");
        // A valid preliminary session is not an expiry lease while waiting for the remaining gates.
        var expiringToken=RandomNumberGenerator.GetBytes(32);
        await Exec("INSERT security.SessionAuthority VALUES(@token,@p,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(second,3,SYSUTCDATETIME()),N'fixture-expiry',0)",("@token",expiringToken),("@p",author));
        var expiryPackage=Package();epoch=await Epoch();
        await using(var holder=new SqlConnection(target.ConnectionString))
        {
            await holder.OpenAsync();await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture expiry lock failed.',1;",holder,tx))
            {q.Parameters.AddWithValue("@resource",ConfigurationLockResources.Policy(target.InstallationId,"timezone",template.Timezone.RevisionId));await q.ExecuteNonQueryAsync();}
            var pending=writer.SubmitConfigurationAsync(expiringToken,Guid.NewGuid(),expiryPackage,epoch,"expiry after lock wait");
            try
            {
                await WaitForLock("fc2configuration-writer","S");
                var expired=false;
                for(var i=0;i<250;i++)
                {if(Convert.ToInt32(await Scalar("SELECT CASE WHEN ExpiresAt<=SYSUTCDATETIME() THEN 1 ELSE 0 END FROM security.SessionAuthority WHERE TokenHash=@token",("@token",expiringToken)))==1){expired=true;break;}await Task.Delay(20);}
                Check(expired,"SQL clock confirms session expiry while policy lock remains held");
            }
            finally{await tx.CommitAsync();}
            await Denied(()=>pending,"final workflow authorization rejects a session expired during policy wait");
        }
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@revision",("@revision",expiryPackage.RevisionId)))==0,"expiry during lock wait leaves no submitted content");
        // Whole workflow write command count stays constant as machine/signal counts grow.
        var measurements=new ConcurrentQueue<(long Trips,double Elapsed)>();
        using(var listener=new ActivityListener{ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllData,
            ActivityStopped=a=>{if(a.OperationName=="security.transaction")measurements.Enqueue((Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")),Convert.ToDouble(a.GetTagItem("security.elapsed.ms"))));}})
        {
            ActivitySource.AddActivityListener(listener);var small=Package();await service.SubmitConfigurationAsync(tokens[author],Guid.NewGuid(),small,await Epoch(),"small measured submission");
            var smallMeasure=measurements.Single();measurements.Clear();
            await Exec(";WITH n AS (SELECT 1 AS i UNION ALL SELECT i+1 FROM n WHERE i<100) INSERT [master].Machine(MachineId,SiteId,DisplayName) SELECT NEWID(),@site,CONCAT(N'Workflow load ',i) FROM n OPTION(MAXRECURSION 100)",("@site",site));
            var machines=new List<MachineConfiguration>();
            await using(var q=new SqlCommand("SELECT TOP(100) MachineId FROM [master].Machine WHERE SiteId=@site AND MachineId<>@original ORDER BY MachineId",c))
            {
                Add(q,("@site",site),("@original",machine.MachineId));await using var r=await q.ExecuteReaderAsync();
                while(await r.ReadAsync())machines.Add(machine with{MachineId=r.GetGuid(0),Signals=[machine.Signals[0] with{MappingId=Guid.NewGuid()}]});
            }
            var large=Package() with{Machines=machines};await service.SubmitConfigurationAsync(tokens[author],Guid.NewGuid(),large,await Epoch(),"100-machine measured submission");
            var largeMeasure=measurements.Single();
            Check(smallMeasure.Trips>0&&smallMeasure.Trips==largeMeasure.Trips&&largeMeasure.Trips<=28,"batched submission full SQL roundtrips are constant for 1 and 100 machines");
            Check(largeMeasure.Elapsed<5000,"100-machine submission holds authority within the CI five-second ceiling");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PackageMachineAssignment WHERE PackageRevisionId=@revision",("@revision",large.RevisionId)))==100&&
                Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.PackageSignalMapping WHERE PackageRevisionId=@revision",("@revision",large.RevisionId)))==100,"batched write preserves all machine and exact signal rows");
            Console.WriteLine($"MEASURE: configuration submission machines=100 mappings=100 roundtrips={largeMeasure.Trips} elapsedMs={largeMeasure.Elapsed:F2}; synthetic SQL fixture, not a production SLA");
        }
        // Revocation first must invalidate even a registered pending submission.
        var otherToken=RandomNumberGenerator.GetBytes(32);await Exec("INSERT security.SessionAuthority VALUES(@token,@p,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',0)",("@token",otherToken),("@p",author));
        await Exec("CREATE TRIGGER security.Fixture_ConfigurationBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:configuration:test-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'Fixture barrier failed.',1; END;");
        var blocked=Package();
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:configuration:test-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture barrier failed.',1;",barrier,tx))await q.ExecuteNonQueryAsync();
            epoch=await Epoch();var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2configuration-revoker"}.ConnectionString,target.InstallationId);
            var revoking=revoker.RevokeSessionAsync(tokens[admin],Guid.NewGuid(),otherToken,epoch,"revoke pending submit session");Task<byte[]>? pending=null;
            try{await WaitForLock("fc2configuration-revoker","S");pending=writer.SubmitConfigurationAsync(otherToken,Guid.NewGuid(),blocked,epoch+1,"revoked pending submission");await WaitForLock("fc2configuration-writer","X");Check(!pending.IsCompleted,"submission waits behind actual uncommitted session revocation");}
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>pending??throw new InvalidOperationException("Missing pending submission."),"pending submission rechecks committed revocation before writing");
        }
        await Exec("DROP TRIGGER security.Fixture_ConfigurationBarrier");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@revision",("@revision",blocked.RevisionId)))==0,"revoked pending operation publishes no package");
        var reviewerGrant=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE GranteeId=@p AND Permission='configuration.review'",("@p",reviewer)))!;
        await service.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),reviewerGrant,await Epoch(),"remove reviewer authority");
        await Denied(()=>service.ReviewConfigurationAsync(tokens[reviewer],review),"completed review retry requires current review authority");
        await Denied(()=>service.ReadConfigurationOperationAsync(tokens[reviewer],review.OperationId),"revoked reviewer cannot retrieve stored decision result");
        return count;

        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task Grant(Guid principal,string permission)=>await Exec("INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,SiteId,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt) VALUES(NEWID(),@p,@p,'bootstrap',@permission,@scope,@site,1,0,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()))",("@p",principal),("@permission",permission),("@scope",permission=="security.access.manage"?"I":"S"),("@site",permission=="security.access.manage"?DBNull.Value:site));
        async Task AuditFailure()=>await Exec("CREATE TRIGGER security.Fixture_ConfigurationFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51106,'Workflow fixture audit failure.',1; END;");
        async Task WaitForLock(string app,string mode)
        {
            for(var i=0;i<150;i++)
            {if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;await Task.Delay(20);}
            throw new InvalidOperationException("Expected workflow SQL lock wait: "+app+"/"+mode);
        }
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task SqlFailure(Func<Task> action,string name){try{await action();}catch(SqlException e)when(e.Number==51106){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task ForeignKey(Func<Task> action,string name){try{await action();}catch(SqlException e)when(e.Number==547){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Immutable(string sql,string name){try{await Exec(sql);}catch(SqlException e)when(e.Number==51010){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {foreach(var(name,value)in values)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else if(name=="@site")q.Parameters.Add(name,SqlDbType.UniqueIdentifier).Value=value;else q.Parameters.AddWithValue(name,value);}
}
