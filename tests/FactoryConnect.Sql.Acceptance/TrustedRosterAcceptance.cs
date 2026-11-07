using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class TrustedRosterAcceptance
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
        var enabling=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2roster-writer"}.ConnectionString,target.InstallationId);
        var suspending=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2machine-holder"}.ConnectionString,target.InstallationId);
        var acknowledging=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2roster-ack"}.ConnectionString,target.InstallationId);
        foreach(var(person,subject)in new[]{(proposer,"certificate-manager"),(approver,"independent-enabler"),(holder,"safety-holder")})await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),person,Issuer,subject,await Epoch(),"enroll fixture operator"));
        var grants=new Dictionary<(Guid,string),Guid>();
        foreach(var permission in new[]{"runtime.credential.manage","runtime.assign","configuration.edit","configuration.submit","configuration.activate","machine.enable","commissioning.read"})
            await Grant(proposer,permission,permission=="runtime.credential.manage"?AuthorizationScope.Installation:permission is "configuration.edit" or "configuration.submit"?AuthorizationScope.Site(site):AuthorizationScope.Machine(site,machine));
        await Grant(approver,"machine.enable",AuthorizationScope.Machine(site,machine));await Grant(approver,"configuration.review",AuthorizationScope.Site(site));await Grant(holder,"machine.suspend",AuthorizationScope.Machine(site,machine));
        await Grant(proposer,"configuration.read",AuthorizationScope.Site(site));await Grant(proposer,"policy.revoke",AuthorizationScope.Site(site));await Grant(approver,"policy.revoke",AuthorizationScope.Site(site));
        await Session(token,proposer);await Session(reviewToken,approver);await Session(holdToken,holder);
        var activities=new List<Activity>();using var listener=new ActivityListener{ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=activity=>{lock(activities)activities.Add(activity);}};ActivitySource.AddActivityListener(listener);
        await service.EnrollRuntimeCertificateAsync(token,f.Sign(f.Payload with{ExpectedEpoch=await Epoch()},certificate:tls.Certificate));
        var assignment=Guid.NewGuid();await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),assignment,site,machine,f.Payload.RuntimeIdentityId,1,now.AddMinutes(-5),null,0,null,await Epoch(),"assign operational fixture"));
        await using var pair=await tls.Connect();
        var(package,review)=await Package();
        var enable= new MachineEnableProposalRequest(Guid.NewGuid(),Guid.NewGuid(),site,machine,null,0,await Epoch(),now.AddMinutes(10),"enable safety fixture");
        await service.ProposeMachineEnableAsync(token,enable);await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),enable.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"independent enable");

        var reader=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2roster-reader"}.ConnectionString,target.InstallationId);
        await Denied(()=>reader.ReadTrustedSiteRosterAsync(rootToken,site),"administration does not imply roster read authority");
        await Denied(()=>reader.ReadTrustedSiteRosterAsync(token,Guid.NewGuid()),"roster site permission is exact");
        var empty=await reader.ReadTrustedSiteRosterAsync(token,site);Check(empty.Status=="healthy-empty"&&empty.Machines.Count==2&&empty.Machines.All(x=>x.Status==FactoryConnect.Sql.Inventory.RosterStatus.Excluded),"complete source with no acknowledgement is explicitly healthy empty");
        var first=await Intent();var ack=Ack(first,1);await service.AcknowledgeActivationAsync(pair.Channel,ack);
        var cut=await reader.ReadTrustedSiteRosterAsync(token,site);var active=cut.Machines.Single(x=>x.MachineId==machine);
        Check(cut.Status=="eligible-configurations"&&active.PackageRevisionId==package.RevisionId&&active.AcknowledgementId==ack.AcknowledgementId&&active.ConfiguredCapabilityIntent.SequenceEqual(new[]{"counter-reading"}),"reader assembles exact acknowledged configuration and descriptive capability intent");
        Check(cut.EvaluatedAt.Offset==TimeSpan.Zero&&cut.SecurityEpoch==await Epoch(),"roster evaluation time and ordering epoch are generated by SQL authority");
        var bogusPolicy=Guid.NewGuid();await Exec("INSERT configuration.SharedScheduleRevision VALUES(@revision,@site,0x7B7D,HASHBYTES('SHA2_256',0x7B7D))",("@revision",bogusPolicy),("@site",site));
        await Exec("DISABLE TRIGGER configuration.TR_Assignment_Immutable ON configuration.PackageMachineAssignment");
        try{await Exec("UPDATE configuration.PackageMachineAssignment SET ScheduleRevisionId=@revision WHERE PackageRevisionId=@package",("@revision",bogusPolicy),("@package",package.RevisionId));await Denied(()=>reader.ReadTrustedSiteRosterAsync(token,site),"canonical policy mismatch fails closed despite valid normalized foreign keys");}
        finally{await Exec("UPDATE configuration.PackageMachineAssignment SET ScheduleRevisionId=@revision WHERE PackageRevisionId=@package",("@revision",package.Machines[0].Schedule.RevisionId),("@package",package.RevisionId));await Exec("ENABLE TRIGGER configuration.TR_Assignment_Immutable ON configuration.PackageMachineAssignment");}
        var expiry=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,2,SYSUTCDATETIME()),'+00:00')"))!;
        var bounded=await Intent(until:expiry);await service.AcknowledgeActivationAsync(pair.Channel,Ack(bounded,1));
        while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<expiry)await Task.Delay(20);
        await Excluded("OutsideEffectiveInterval","acknowledgement expires at its half-open SQL effective boundary");
        first=await Intent();ack=Ack(first,1);await service.AcknowledgeActivationAsync(pair.Channel,ack);
        var newer=await Intent();cut=await reader.ReadTrustedSiteRosterAsync(token,site);
        Check(cut.Machines.Single(x=>x.MachineId==machine).AcknowledgementId==ack.AcknowledgementId,"pending replacement retains still eligible last acknowledgement");
        await Exec("DELETE configuration.MachineAcknowledgementHead WHERE MachineId=@machine",("@machine",machine));
        var missing=await reader.ReadTrustedSiteRosterAsync(token,site);Check(missing.Status=="unavailable"&&missing.Machines.Single(x=>x.MachineId==machine).Reason=="MissingAcknowledgementHead","missing head with retained acknowledgement cannot become healthy empty");
        await Exec("INSERT configuration.MachineAcknowledgementHead VALUES(@site,@machine,@ack,@generation)",("@site",site),("@machine",machine),("@ack",ack.AcknowledgementId),("@generation",ack.ActivationGeneration));
        await Exec("UPDATE [master].ProductionLine SET IsRetired=1 WHERE ProductionLineId=@line",("@line",line));await Excluded("Retired","retired production line excludes acknowledged configuration");await Exec("UPDATE [master].ProductionLine SET IsRetired=0 WHERE ProductionLineId=@line",("@line",line));
        await Exec("UPDATE [master].Site SET IsRetired=1 WHERE SiteId=@site",("@site",site));await Excluded("Retired","retired site excludes acknowledged configuration");await Exec("UPDATE [master].Site SET IsRetired=0 WHERE SiteId=@site",("@site",site));
        var policy=await Proposal("schedule",now.AddMinutes(5));await service.ProposePolicyRevocationAsync(token,policy);await Approve(policy);
        Check((await reader.ReadTrustedSiteRosterAsync(token,site)).Machines.Single(x=>x.MachineId==machine).Status==FactoryConnect.Sql.Inventory.RosterStatus.Eligible,"future policy cut does not exclude current acknowledged interval early");
        var immediate=await Proposal("schedule");await service.ProposePolicyRevocationAsync(token,immediate);await Approve(immediate);await Excluded("PolicyRevoked","direct authoritative policy check excludes stale last acknowledgement");
        (package,review)=await Package();var replacement=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(replacement,1));
        await service.WithdrawConfigurationApprovalAsync(reviewToken,await Withdrawal());await Excluded("Revoked","withdrawn review excludes immutable acknowledged configuration");
        (package,review)=await Package();replacement=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(replacement,1));
        // Read-first order: an in-transaction SQL barrier delays materialization while a withdrawal waits on security X.
        await Exec("CREATE TRIGGER security.Fixture_RosterBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:roster:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'fixture barrier failed',1; END;");
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:roster:audit-barrier");
            var writer=enabling.WithdrawConfigurationApprovalAsync(reviewToken,await Withdrawal());Task<TrustedSiteRoster>? reading=null;
            try{await WaitForLock("fc2roster-writer","S");reading=reader.ReadTrustedSiteRosterAsync(token,site);await WaitForLock("fc2roster-reader","S");Check(!reading.IsCompleted,"roster read waits behind uncommitted safety writer");}finally{await tx.CommitAsync();}
            await writer;var after=await reading!;Check(after.Machines.Single(x=>x.MachineId==machine).Reason=="Revoked","writer-first roster cut observes committed withdrawal");
        }
        await Exec("DROP TRIGGER security.Fixture_RosterBarrier");
        (package,review)=await Package();replacement=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(replacement,1));
        // A master-row lock blocks the query after it has acquired security Shared. The writer must wait for the complete read.
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await using(var hold=new SqlCommand("SELECT MachineId FROM [master].Machine WITH(XLOCK,HOLDLOCK) WHERE MachineId=@machine",barrier,tx)){hold.Parameters.AddWithValue("@machine",machine);await hold.ExecuteScalarAsync();}
            var reading=reader.ReadTrustedSiteRosterAsync(token,site);Task<byte[]>? writer=null;
            try{await WaitForDataLock("fc2roster-reader");writer=enabling.WithdrawConfigurationApprovalAsync(reviewToken,await Withdrawal());await WaitForLock("fc2roster-writer","X");Check(!writer.IsCompleted,"withdrawal waits while roster source is being materialized");}finally{await tx.CommitAsync();}
            var before=await reading;await writer!;Check(before.Machines.Single(x=>x.MachineId==machine).Status==FactoryConnect.Sql.Inventory.RosterStatus.Eligible,"read-first cut completes with earlier eligible authority");await Excluded("Revoked","following read observes later withdrawal");
        }
        (package,review)=await Package();replacement=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(replacement,1));
        await service.SuspendMachineAsync(holdToken,new(Guid.NewGuid(),Guid.NewGuid(),site,machine,(Guid)(await Scalar("SELECT StateId FROM configuration.MachineOperationalState WHERE MachineId=@machine",("@machine",machine)))!,1,await Epoch(),"roster suspension"));await Excluded("DisabledOrSuspended","suspension excludes last acknowledgement");
        var suspendedState=(Guid)(await Scalar("SELECT StateId FROM configuration.MachineOperationalState WHERE MachineId=@machine",("@machine",machine)))!;
        var resume=new MachineEnableProposalRequest(Guid.NewGuid(),Guid.NewGuid(),site,machine,suspendedState,2,await Epoch(),now.AddMinutes(10),"resume roster fixture");await service.ProposeMachineEnableAsync(token,resume);await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),resume.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"independent resume");await Excluded("OperatingGenerationChanged","resume cannot revive old acknowledgement");
        await service.RevokeGrantAsync(rootToken,Guid.NewGuid(),grants[(proposer,"configuration.read")],await Epoch(),"revoke roster authority");await Denied(()=>reader.ReadTrustedSiteRosterAsync(token,site),"completed old read gives no access after grant revocation");await Grant(proposer,"configuration.read",AuthorizationScope.Site(site));
        // Complete 100-machine site, one package, and 100 real assignment/activation/acknowledgement producers.
        await Exec("DELETE [master].Machine WHERE MachineId=@other",("@other",otherMachine));
        (package,review)=await Package(100);
        var allMachines=package.Machines.Select(x=>x.MachineId).ToArray();
        await MachineOperationalFixture.Enable(c,site,allMachines.Where(x=>x!=machine).ToArray(),proposer,token,approver,now);
        foreach(var id in allMachines)
        {
            if(id==machine){var original=await Intent();await service.AcknowledgeActivationAsync(pair.Channel,Ack(original,3));continue;}
            await Grant(proposer,"runtime.assign",AuthorizationScope.Machine(site,id));await Grant(proposer,"configuration.activate",AuthorizationScope.Machine(site,id));
            var assigned=Guid.NewGuid();await service.AssignRuntimeAsync(token,new(Guid.NewGuid(),assigned,site,id,f.Payload.RuntimeIdentityId,1,now.AddMinutes(-5),null,0,null,await Epoch(),"batch roster assignment"));
            var intent=new ActivationRequestCommand(Guid.NewGuid(),Guid.NewGuid(),site,id,package.RevisionId,PackageCanonicalizer.Hash(package),review,f.Payload.RuntimeIdentityId,assigned,1,1,now.AddMinutes(-1),null,0,null,await Epoch(),"batch roster activation");await service.RequestActivationAsync(token,intent);
            await service.AcknowledgeActivationAsync(pair.Channel,new(Guid.NewGuid(),Guid.NewGuid(),site,id,intent.ActivationId,1,package.RevisionId,intent.ContentHash,review,f.Payload.RuntimeIdentityId,assigned,1,1,1,"batch roster acknowledgement"));
        }
        var watch=Stopwatch.StartNew();var batchCut=await reader.ReadTrustedSiteRosterAsync(token,site);watch.Stop();Check(batchCut.Machines.Count==100&&batchCut.Machines.All(x=>x.Status==FactoryConnect.Sql.Inventory.RosterStatus.Eligible),"complete 100-machine cut uses exact independently produced acknowledgements");
        var trace=activities.Last(x=>x.GetTagItem("security.roster.read") is true);var trips=Convert.ToInt64(trace.GetTagItem("security.sql.roundtrips"));Check(trips<=12&&watch.Elapsed<TimeSpan.FromSeconds(5),"100-machine complete roster read stays within full-transaction SQL budget");Console.WriteLine($"MEASURE: trusted roster machines=100 elapsedMs={watch.Elapsed.TotalMilliseconds:F2} roundtrips={trips}");
        var samples=await Task.WhenAll(Enumerable.Range(0,8).Select(async _=>{var timer=Stopwatch.StartNew();var value=await reader.ReadTrustedSiteRosterAsync(token,site);return(value,timer.Elapsed.TotalMilliseconds);}));Check(samples.All(x=>x.value.Machines.Count==100&&x.value.Status=="eligible-configurations"),"eight concurrent readers obtain complete eligible site cuts");Console.WriteLine($"MEASURE: trusted roster concurrency=8 maxMs={samples.Max(x=>x.Item2):F2}");
        var perfConnection=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2roster-performance",MinPoolSize=8,MaxPoolSize=8}.ConnectionString;
        var perfReader=new SqlSecurityAuthority(perfConnection,target.InstallationId);var leases=await Task.WhenAll(Enumerable.Range(0,8).Select(async _=>{var lease=new SqlConnection(perfConnection);await lease.OpenAsync();return lease;}));foreach(var lease in leases)await lease.DisposeAsync();
        await perfReader.ReadTrustedSiteRosterAsync(token,site);var scenarios=new List<object>();
        foreach(var concurrency in new[]{1,8})
        {
            var times=new System.Collections.Concurrent.ConcurrentBag<double>();long beforeReads=Convert.ToInt64(await Scalar("SELECT COALESCE(SUM(logical_reads),0) FROM sys.dm_exec_sessions WHERE program_name='fc2roster-performance'"));
            int traceStart;lock(activities)traceStart=activities.Count;
            await Parallel.ForEachAsync(Enumerable.Range(0,128),new ParallelOptions{MaxDegreeOfParallelism=concurrency},async(_,cancellation)=>{var start=Stopwatch.GetTimestamp();var value=await perfReader.ReadTrustedSiteRosterAsync(token,site,cancellation);if(value.Status!="eligible-configurations"||value.Machines.Count!=100||value.Machines.Any(x=>x.Status!=FactoryConnect.Sql.Inventory.RosterStatus.Eligible))throw new InvalidOperationException("Incomplete performance cut");times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);});
            var afterReads=Convert.ToInt64(await Scalar("SELECT COALESCE(SUM(logical_reads),0) FROM sys.dm_exec_sessions WHERE program_name='fc2roster-performance'"));
            Activity[] traces;lock(activities)traces=activities.Skip(traceStart).Where(x=>x.GetTagItem("security.roster.read") is true).ToArray();var sorted=times.Order().ToArray();
            static double P99(double[] values,double quantile)=>values[Math.Clamp((int)Math.Ceiling(values.Length*quantile)-1,0,values.Length-1)];
            var p95=P99(sorted,.95);var p99=P99(sorted,.99);var maxTrips=traces.Max(x=>Convert.ToInt64(x.GetTagItem("security.sql.roundtrips")));
            Check(sorted.Length==128&&traces.Length==128&&maxTrips<=12,"roster full-transaction roundtrip budget holds for 128 samples at concurrency "+concurrency);
            Check(p95<2000&&p99<5000,"roster p95/p99 retain timeout headroom at concurrency "+concurrency);
            var sessions=Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name='fc2roster-performance'"));Check(sessions<=8,"roster connection pool stays bounded at concurrency "+concurrency);
            var logicalDelta=afterReads>=beforeReads?(long?)(afterReads-beforeReads):null;
            Console.WriteLine($"MEASURE: trusted roster concurrency={concurrency} samples=128 p95Ms={p95:F2} p99Ms={p99:F2} maxRoundtrips={maxTrips} pooledSessionLogicalReadsDelta={logicalDelta?.ToString()??"unavailable"}");
            scenarios.Add(new{concurrency,samples=128,machines=100,warm=true,p95Ms=p95,p99Ms=p99,maxRoundtrips=maxTrips,pooledSessions=sessions,pooledSessionLogicalReadsBefore=beforeReads,pooledSessionLogicalReadsAfter=afterReads,pooledSessionLogicalReadsDelta=logicalDelta,
                gateWaitP95Ms=P99(traces.Select(x=>Convert.ToDouble(x.GetTagItem("security.gate.wait.ms"))).Order().ToArray(),.95)});
        }
        Directory.CreateDirectory("artifacts/performance");await File.WriteAllTextAsync("artifacts/performance/trusted-roster.json",JsonSerializer.Serialize(new{schemaVersion=1,complete=true,commit=Environment.GetEnvironmentVariable("GITHUB_SHA"),scope="Warm disposable SQL eligibility reader, not HTTP/production/connectivity capacity",scenarios,
            limitations=new[]{"Logical reads are observed pooled-session counter deltas, include authority and temp-table work, and are not isolated data-query/plan attribution; null on counter decrease.","Sampled client tracing and warm bounded connection pool; no cold-SQL claim.","Canonical packages are deduplicated per cut; no cross-request authorization cache."}},new JsonSerializerOptions{WriteIndented=true}));
        await service.RevokeRuntimeAssignmentAsync(token,Guid.NewGuid(),assignment,site,machine,1,await Epoch(),"remove roster assignment");await Excluded("Revoked","assignment revocation excludes previously acknowledged configuration");
        var revoke=f.Payload with{Action="revoke",ExpectedGeneration=1,ExpectedEpoch=await Epoch(),OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=[],ValidFrom=DateTimeOffset.UnixEpoch,CredentialExpiresAt=DateTimeOffset.UnixEpoch};
        var signed=RuntimeCredentialProof.Canonicalize(revoke);await service.ChangeRuntimeCredentialAsync(token,new(revoke,[RuntimeCertificateAcceptance.Fixture.Proof(revoke.ApproverKeyId,f.A,signed),RuntimeCertificateAcceptance.Fixture.Proof(revoke.ExecutorKeyId,f.B,signed)],[]));
        var revoked=await reader.ReadTrustedSiteRosterAsync(token,site);Check(revoked.Status=="healthy-empty"&&revoked.Machines.All(x=>x.Reason=="Revoked"),"credential revocation excludes every acknowledged machine without discarding history");
        var rotate=f.Payload with{Action="rotate",ExpectedGeneration=1,ExpectedEpoch=await Epoch(),OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=f.Other.ExportSubjectPublicKeyInfo()};
        await service.EnrollRuntimeCertificateAsync(token,f.Sign(rotate,certificate:f.Leaf(f.Other,now,crlUri:tls.CrlUri),key:f.Other));
        Check((await reader.ReadTrustedSiteRosterAsync(token,site)).Machines.All(x=>x.Reason=="RuntimeGenerationChanged"),"new valid credential generation cannot revive old acknowledgements");
        await Exec("INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(NEWID(),@site,N'Overflow')",("@site",site));try{await reader.ReadTrustedSiteRosterAsync(token,site);throw new InvalidOperationException("truncated cut");}catch(SqlException e)when(e.Number==51042){Check(true,"oversized site fails explicitly instead of publishing a truncated roster");}
        return count;

        async Task Excluded(string reason,string label){var value=await reader.ReadTrustedSiteRosterAsync(token,site);var row=value.Machines.Single(x=>x.MachineId==machine);Check(row.Status==FactoryConnect.Sql.Inventory.RosterStatus.Excluded&&row.Reason==reason,label);}
        async Task WaitForDataLock(string app){for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id WHERE s.program_name=@app AND r.wait_type LIKE 'LCK_M_%'",("@app",app)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("Expected source lock wait");}
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
        async Task<ActivationRequestCommand> Intent(DateTimeOffset? from=null,DateTimeOffset? until=null)
        {
            Guid? prior=null;long generation=0;await using(var q=new SqlCommand("SELECT ActivationId,ActivationGeneration FROM configuration.MachineActivationRequestHead WHERE MachineId=@machine",c)){q.Parameters.AddWithValue("@machine",machine);await using var r=await q.ExecuteReaderAsync();if(await r.ReadAsync()){prior=r.GetGuid(0);generation=r.GetInt64(1);}}
            var request=new ActivationRequestCommand(Guid.NewGuid(),Guid.NewGuid(),site,machine,package.RevisionId,PackageCanonicalizer.Hash(package),review,f.Payload.RuntimeIdentityId,assignment,1,1,from??now.AddMinutes(-1),until,generation,prior,await Epoch(),"explicit activation request");
            await service.RequestActivationAsync(token,request);return request with{ExpectedActivationGeneration=generation+1};
        }
        ActivationAcknowledgementRequest Ack(ActivationRequestCommand request,long operating)=>new(Guid.NewGuid(),Guid.NewGuid(),site,machine,request.ActivationId,request.ExpectedActivationGeneration,request.PackageRevisionId,request.ContentHash,request.ApprovalReviewId,request.RuntimeIdentityId,request.AssignmentId,1,1,operating,"runtime exact application acknowledgement");
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"));
        async Task Hold(SqlConnection connection,SqlTransaction transaction,string resource){await using var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'fixture lock failed',1;",connection,transaction);q.Parameters.AddWithValue("@resource",resource);await q.ExecuteNonQueryAsync();}
        async Task WaitForLock(string app,string mode){for(var i=0;i<150;i++){if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode",("@app",app),("@mode",mode)))>0)return;await Task.Delay(20);}throw new InvalidOperationException("Expected operational SQL wait "+app+"/"+mode);}
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);await q.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand q,params (string Name,object Value)[] args){foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
