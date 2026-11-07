using System.Data;
using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using FactoryConnect.Protocols.MTConnect;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.ActivityProcessing;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class MtConnectIngestionAcceptance
{
    const string Issuer="https://certificate.fixture/tenant";
    const string Device="fixture-device";
    const string Stream="mtconnect:uuid:"+Device;
    private sealed class EvidenceLoginVerifier(VerifiedHumanIdentity identity):IHumanIdentityVerifier
    {
        public Task<VerifiedHumanIdentity> VerifyAsync(string assertion,CancellationToken ct)
            => assertion=="sql-evidence-proof" ? Task.FromResult(identity) : throw new SecurityDeniedException();
    }
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
        var enabling=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2ingest-revoker"}.ConnectionString,target.InstallationId);
        var suspending=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2machine-holder"}.ConnectionString,target.InstallationId);
        var acknowledging=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2ingest-writer"}.ConnectionString,target.InstallationId);
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
        await using var initialPair=await tls.Connect();var pair=initialPair;
        var(package,review)=await Package();
        var writer=new SqlSecurityAuthority(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2ingest-writer"}.ConnectionString,target.InstallationId);
        await Denied(()=>writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream),"unenabled runtime cannot acquire ingestion context");
        var enable=new MachineEnableProposalRequest(Guid.NewGuid(),Guid.NewGuid(),site,machine,null,0,await Epoch(),now.AddMinutes(10),"enable ingestion fixture");await service.ProposeMachineEnableAsync(token,enable);await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),enable.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"independent enable");
        var activation=await Intent();await Denied(()=>writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream),"pending activation without acknowledgement cannot ingest");
        var ack=Ack(activation,1);await service.AcknowledgeActivationAsync(pair.Channel,ack);
        var approvedActivityMapping=package.Machines[0].Signals.Single(
            x=>x.SemanticKey=="machine-activity");
        var mappingResolver=new SqlApprovedActivityMappingResolver(
            target.ConnectionString,target.InstallationId);
        var resolved=await mappingResolver.ResolveAsync(
            site,machine,approvedActivityMapping.MappingId,Stream);
        Check(resolved.PackageRevisionId==package.RevisionId &&
            resolved.AcknowledgementId==ack.AcknowledgementId &&
            resolved.PackageSha256==Convert.ToHexString(PackageCanonicalizer.Hash(package)) &&
            resolved.Signal.SourceAddress=="execution" &&
            resolved.Signal.ExactValues.Count==2 &&
            resolved.Signal.ExactValues["ACTIVE"]==ActivityState.Running &&
            resolved.Signal.ExactValues["READY"]==ActivityState.Idle,
            "approved activity state mapping resolves exact acknowledged package values");
        try
        {
            await mappingResolver.ResolveAsync(site,machine,
                package.Machines[0].Signals.Single(x=>x.SemanticKey=="counter-upper")
                    .MappingId,Stream);
            throw new InvalidOperationException("Counter mapping was interpreted as activity.");
        }
        catch(InvalidDataException)
        {
            Check(true,"approved counter normalization cannot be interpreted as machine activity");
        }
        try
        {
            await mappingResolver.ResolveAsync(site,machine,
                approvedActivityMapping.MappingId,Stream.ToUpperInvariant());
            throw new InvalidOperationException("Folded mapping stream accepted.");
        }
        catch(InvalidDataException)
        {
            Check(true,"mapping resolution rejects case-folded source stream");
        }
        await using var agent=new MtConnectAgentFixture();using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};var client=new MtConnectSampleClient(http);
        var stamp=now.ToString("O",CultureInfo.InvariantCulture);agent.Reply=(from,count)=>(200,Frame(from,Math.Min(count,2)),null);
        var context=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);Check(context.Cursor is null&&context.AcknowledgementId==ack.AcknowledgementId,"fresh acknowledged stream has explicit retained-window bootstrap context");
        var fetched=await client.AcquireAsync(agent.Endpoint,Device,100,2);Check(fetched.Observations.Count==2&&fetched.Observations[0].RawValue==" 00042 ","real HTTP sample acquisition preserves source whitespace and leading zeros");
        var request=await Request(fetched);var epoch=await Epoch();await Transport(request);Check(await Epoch()==epoch,"ordinary ingestion commits without advancing security epoch");
        var first=await writer.ReadMtConnectEvidenceAsync(token,site,machine,Stream);Check(first.Count==2&&first.Single(x=>x.Address=="Counter").RawValue==" 00042 "&&first.Select(x=>x.MappingId).Distinct().Count()==2,"protected read returns actual saved values with case-distinct mapping identities");
        Check(first.All(x=>x.BatchId==request.OperationId&&x.AcknowledgementId==ack.AcknowledgementId&&x.DocumentHash==Convert.ToHexString(SHA256.HashData(fetched.RawDocument)).ToLowerInvariant()),"saved values retain exact batch acknowledgement and raw-document provenance");
        // The source reader must reject a projection mutated independently of
        // immutable canonical/source document bytes. This intentional corruption
        // is confined to the disposable SQL acceptance database and restored
        // before the remaining ingestion authority tests.
        var counterEvidence=first.Single(x=>x.Address=="Counter");
        var counterSignal=new QualifiedActivitySignal(machine,Stream,
            counterEvidence.MappingId,"Counter",
            new Dictionary<string,ActivityState>(StringComparer.Ordinal)
            {[" 00042 "]=ActivityState.Running});
        var counterIdentity=ActivityQualificationIdentity.From(
            counterSignal,"accepted-reader-test-only");
        var acceptedReader=new SqlAcceptedActivityEvidenceReader(target.ConnectionString);
        var verifiedCounter=await acceptedReader.ReadBoundedAsync(
            site,counterIdentity,7,99,100);
        Check(verifiedCounter.Count==1 &&
            verifiedCounter[0].RawValue==counterEvidence.RawValue &&
            verifiedCounter[0].BatchId==request.OperationId &&
            verifiedCounter[0].AcknowledgementId==ack.AcknowledgementId,
            "reader binds exact accepted projection to verified canonical source and owning batch");

        async Task RejectTampered(string label,
            ActivityQualificationIdentity? readIdentity=null)
        {
            try
            {
                await acceptedReader.ReadBoundedAsync(site,
                    readIdentity??counterIdentity,7,99,100);
            }
            catch(InvalidDataException)
            {
                Check(true,label);
                return;
            }
            throw new InvalidOperationException("Projection inconsistency was accepted: "+label);
        }
        const string sourceWhere="""
            WHERE MachineId=@machine AND InstanceId=7 AND Sequence=100
            """;
        await Exec("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        try
        {
            await Exec("UPDATE acquisition.MtConnectObservation SET RawValue=N'FORGED' "+
                sourceWhere,("@machine",machine));
            await RejectTampered("modified raw value rejected against verified canonical source");
            await Exec("UPDATE acquisition.MtConnectObservation SET RawValue=@value,Quality='unavailable' "+
                sourceWhere,("@machine",machine),("@value",counterEvidence.RawValue));
            await RejectTampered("modified quality rejected against verified source observation");
            await Exec("UPDATE acquisition.MtConnectObservation SET Quality='source-reported',"+
                "SourceTimestamp=N'2020-01-01T00:00:00Z' "+sourceWhere,("@machine",machine));
            await RejectTampered("modified source timestamp rejected against verified source observation");
            await Exec("UPDATE acquisition.MtConnectObservation SET SourceTimestamp=@stamp,"+
                "ObservedAt=DATEADD(second,1,ObservedAt) "+sourceWhere,
                ("@machine",machine),("@stamp",counterEvidence.SourceTimestamp));
            await RejectTampered("modified observed instant rejected against verified source observation");
            await Exec("UPDATE acquisition.MtConnectObservation SET ObservedAt=@observed,"+
                "AddressBytes=0x636F756E746572 "+sourceWhere,
                ("@machine",machine),("@observed",counterEvidence.ObservedAt));
            await RejectTampered("changed projected address rejected against canonical identity",
                counterIdentity with{SourceAddress="counter"});
            // SQL must reject an oversized aggregate *before* fetching the
            // tampered LOB over the network. This is disposable SQL only.
            await Exec("UPDATE acquisition.MtConnectObservation SET "+
                "AddressBytes=0x436F756E746572,"+
                "RawValue=REPLICATE(CONVERT(nvarchar(max),N'X'),5000000) "+
                sourceWhere,("@machine",machine));
            try
            {
                await acceptedReader.ReadBoundedAsync(site,counterIdentity,7,99,100);
                throw new InvalidOperationException("Oversized accepted source projection transferred.");
            }
            catch(SqlException error) when(error.Number==51074)
            {
                Check(true,"SQL aggregate byte budget rejects oversized LOB before transfer");
            }
        }
        finally
        {
            await Exec("UPDATE acquisition.MtConnectObservation SET RawValue=@value,"+
                "Quality=@quality,SourceTimestamp=@stamp,ObservedAt=@observed,"+
                "AddressBytes=0x436F756E746572 "+sourceWhere,
                ("@machine",machine),("@value",counterEvidence.RawValue),
                ("@quality",counterEvidence.Quality),
                ("@stamp",counterEvidence.SourceTimestamp),
                ("@observed",counterEvidence.ObservedAt));
            await Exec("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        }
        Check((await acceptedReader.ReadBoundedAsync(site,counterIdentity,7,99,100))
            .Single().RawValue==counterEvidence.RawValue,
            "disposable projection corruption fixture fully restored accepted evidence");
        // End-to-end SQL authority: a freshly verified human proof issues a one-use
        // bearer, and that session reads only the machine explicitly granted to it.
        var loginIdentity=new VerifiedHumanIdentity(Issuer,"certificate-manager",Guid.NewGuid().ToString("N"),
            "fixture-mfa",now,now.AddMinutes(10));
        var login=new SqlHumanSessionAuthority(target.ConnectionString,target.InstallationId,
            new EvidenceLoginVerifier(loginIdentity));
        var issued=await login.IssueAsync("sql-evidence-proof");
        var issuedHash=SHA256.HashData(issued.Token);
        try
        {
            var accepted=await writer.ReadMtConnectEvidenceAsync(issuedHash,site,machine,Stream);
            Check(accepted.Count==first.Count&&accepted.Select(x=>(x.Sequence,x.InstanceId,x.RawValue,x.DocumentHash))
                .SequenceEqual(first.Select(x=>(x.Sequence,x.InstanceId,x.RawValue,x.DocumentHash))),
                "verified human sign-in reads identical retained SQL evidence through its own scoped session");
            var permittedMachines=await writer.ReadOperatorEvidenceMachinesAsync(issuedHash,site);
            Check(permittedMachines.Count==1&&permittedMachines[0].SiteId==site
                &&permittedMachines[0].MachineId==machine,
                "verified human sees only currently authorized machine in bounded operator roster");
            // Sparse-access performance regression: one machine grant among 1,200
            // unrelated same-site machines must cost one inventory command, not
            // proportional discovery pages. This runs on disposable SQL only.
            await Exec("""
                INSERT [master].Machine(MachineId,SiteId,DisplayName)
                SELECT TOP (1200) NEWID(),@site,N'Sparse discovery fixture'
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                """,("@site",site));
            Check(Convert.ToInt32(await Scalar(
                "SELECT COUNT(*) FROM [master].Machine WHERE SiteId=@site AND DisplayName=N'Sparse discovery fixture'",
                ("@site",site)))==1200,"sparse fixture retains 1200 unauthorized same-site machines");
            var sparseActivities=new List<Activity>();
            using(var sparseListener=new ActivityListener
            {
                ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",
                Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped=a=>{lock(sparseActivities)sparseActivities.Add(a);}
            })
            {
                ActivitySource.AddActivityListener(sparseListener);
                var sparseClock=Stopwatch.StartNew();
                var sparse=await writer.ReadOperatorEvidenceMachinesAsync(issuedHash,site);
                sparseClock.Stop();
                Check(sparse.Count==1&&sparse[0].MachineId==machine,
                    "one-machine operator does not discover 1200 unauthorized same-site machines");
                Activity[] observed;
                lock(sparseActivities)observed=sparseActivities
                    .Where(a=>a.OperationName=="security.transaction").ToArray();
                Check(observed.Length==1&&observed[0].GetTagItem("security.sql.roundtrips") is not null
                    &&Convert.ToInt64(observed[0].GetTagItem("security.sql.roundtrips"))<=12,
                    "sparse one-machine discovery SQL roundtrips are independent of site size");
                Console.WriteLine($"MEASURE: operator sparse siteMachines=1202 visible=1 elapsedMs={sparseClock.Elapsed.TotalMilliseconds:F2} sqlRoundtrips={Convert.ToInt64(observed.Single().GetTagItem("security.sql.roundtrips"))}");
            }
            await Exec("DELETE [master].Machine WHERE SiteId=@site AND DisplayName=N'Sparse discovery fixture'",
                ("@site",site));
            // A full 100-machine visible roster must stay query-bounded. Historical
            // revoked grants must not consume the visible-machine limit.
            var rosterMachines=Enumerable.Range(0,98).Select(_=>Guid.NewGuid()).ToArray();
            await using(var rosterInsert=new SqlCommand("",c))
            {
                var rosterValues=string.Join(",",rosterMachines.Select((_,i)=>$"(@id{i},@site,N'Roster fixture')"));
                rosterInsert.CommandText="INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES "+rosterValues;
                rosterInsert.Parameters.AddWithValue("@site",site);
                for(var i=0;i<rosterMachines.Length;i++)rosterInsert.Parameters.AddWithValue($"@id{i}",rosterMachines[i]);
                await rosterInsert.ExecuteNonQueryAsync();
            }
            var rootGrant=(Guid)(await Scalar(
                "SELECT GrantId FROM security.PermissionGrant WHERE GranteeId=@admin AND Permission=N'commissioning.read'",
                ("@admin",admin)))!;
            // Set up a broad delegated grant so one SQL-authority verification
            // covers the entire visible 100-machine inventory.
            var rosterGrant=Guid.NewGuid();
            await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),rosterGrant,proposer,
                rootGrant,"commissioning.read",AuthorizationScope.Site(site),
                true,false,now,now.AddHours(1),await Epoch(),"site roster performance acceptance"));
            var rosterReader=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
            var rosterActivities=new List<Activity>();
            using(var rosterListener=new ActivityListener
            {
                ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",
                Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped=a=>{lock(rosterActivities)rosterActivities.Add(a);}
            })
            {
                ActivitySource.AddActivityListener(rosterListener);
                var clock=Stopwatch.StartNew();
                // Fixture includes both original machines; the broad grant sees 98 new plus two retained.
                var all=await rosterReader.ReadOperatorEvidenceMachinesAsync(issuedHash,site);
                clock.Stop();
                Check(all.Count==100&&all.Select(m=>m.MachineId).Distinct().Count()==100,
                    "100 authorized machines enumerate without truncation");
                Activity[] readings;
                lock(rosterActivities)readings=rosterActivities.Where(a=>a.OperationName=="security.transaction").ToArray();
                Check(readings.Length>0&&readings.Max(a=>Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")))<=12,
                    "100-machine discovery SQL roundtrips stay bounded independently of machine count");
                Console.WriteLine($"MEASURE: operator visible machines=100 elapsedMs={clock.Elapsed.TotalMilliseconds:F2} maxRoundtrips={readings.Max(a=>Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")))}");
            }
            // Retire the extra roster machines before unrelated fixture assertions
            // that deliberately test bounded configuration/discovery behavior.
            await using(var cleanup=new SqlCommand("DELETE FROM [master].Machine WHERE SiteId=@site AND MachineId<>@primary AND MachineId<>@other",c))
            {
                cleanup.Parameters.AddWithValue("@site",site);
                cleanup.Parameters.AddWithValue("@primary",machine);
                cleanup.Parameters.AddWithValue("@other",otherMachine);
                await cleanup.ExecuteNonQueryAsync();
            }
            // Revoke broad permission before exercising exact machine denial.
            await service.RevokeGrantAsync(rootToken,Guid.NewGuid(),rosterGrant,await Epoch(),"restore exact-machine grant");
            Check((await rosterReader.ReadOperatorEvidenceMachinesAsync(issuedHash,site)).Count==1,
                "revoked broad grant cannot disclose another machine");
            Check((await writer.ReadOperatorEvidenceMachinesAsync(rootToken,site)).Count==0,
                "administrator without commissioning grant has no implicit evidence machines");
            await Denied(()=>writer.ReadMtConnectEvidenceAsync(issuedHash,site,otherMachine,Stream),
                "verified human session cannot read an unassigned machine");
            await Denied(()=>login.IssueAsync("sql-evidence-proof"),
                "verified authentication assertion cannot issue a second evidence session");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(issued.Token);
            CryptographicOperations.ZeroMemory(issuedHash);
        }
        if(Environment.GetEnvironmentVariable("FC2_BROWSER_ACCEPTANCE")=="1")
            count += await OperatorBrowserAcceptance.Run(target.ConnectionString,target.InstallationId,
                site,machine,rootToken,Epoch);
        count += await OperatorEvidenceHttpAcceptance.Run(target.ConnectionString,
            target.InstallationId,site,machine,otherMachine,rootToken,Epoch);
        await Denied(()=>writer.ReadMtConnectEvidenceAsync(rootToken,site,machine,Stream),"administrator has no implicit raw-evidence read authority");await Denied(()=>writer.ReadMtConnectEvidenceAsync(token,site,otherMachine,Stream),"raw evidence read respects exact machine grant");
        var repeat=await writer.IngestMtConnectAsync(pair.Channel,request);var repeatedAgain=await writer.IngestMtConnectAsync(pair.Channel,request);Check(repeat.SequenceEqual(repeatedAgain)&&await Count("acquisition.MtConnectBatch")==1&&await Count("acquisition.MtConnectObservation")==2,"exact ingestion retry records one effect and returns saved receipt");
        await Conflict(()=>writer.IngestMtConnectAsync(pair.Channel,request with{FromSequence=101,RawDocument=Frame(101,1)}),"operation identity rejects changed request bytes");
        var pending=await Intent();await pair.DisposeAsync();await using var restartedPair=await tls.Connect();pair=restartedPair;using var restartedHttp=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};client=new MtConnectSampleClient(restartedHttp);var restarted=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);context=await restarted.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);Check(context.Cursor?.NextSequence==102&&context.AcknowledgementId==ack.AcknowledgementId,"restart uses durable cursor and retains eligible last acknowledgement during replacement");
        fetched=await client.AcquireAsync(agent.Endpoint,Device,context.Cursor!.NextSequence,2);var second=await Request(fetched);await Transport(second);var next=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);Check(next.Cursor?.NextSequence==104&&await Count("acquisition.MtConnectObservation")==4,"restart acquisition resumes at committed next sequence");
        var values=await writer.ReadMtConnectEvidenceAsync(token,site,machine,Stream);Check(values.Single(x=>x.Sequence=="102").RawValue=="UNAVAILABLE"&&values.Single(x=>x.Sequence=="102").Quality=="unavailable","unavailable source value remains explicit without a fabricated zero");
        var example=values.ToArray();var overlap=await Request(MtConnectSampleParser.Parse(Frame(100,4),Device,100));await writer.IngestMtConnectAsync(pair.Channel,overlap);Check(await Count("acquisition.MtConnectObservation")==4,"overlap replay deduplicates exact source observations under a new operation");
        await writer.IngestMtConnectAsync(pair.Channel,request);Check((await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream)).Cursor?.BatchId==overlap.OperationId,"historical ingestion retry never rewinds the cursor");
        await Conflict(()=>writer.IngestMtConnectAsync(pair.Channel,second with{OperationId=Guid.NewGuid()}),"stale cursor receipt identity rejects competing progress");
        var changed=await Request(MtConnectSampleParser.Parse(Frame(100,4,changed:true),Device,100));await Conflict(()=>writer.IngestMtConnectAsync(pair.Channel,changed),"conflicting value under retained source identity cannot overwrite history");
        var instance=await Request(MtConnectSampleParser.Parse(Frame(104,2,instance:8),Device,104));await Conflict(()=>writer.IngestMtConnectAsync(pair.Channel,instance),"instance change holds cursor for explicit recovery");
        try{MtConnectSampleParser.Parse(Frame(104,2,first:105),Device,104);throw new InvalidOperationException("gap accepted");}catch(InvalidDataException){Check(true,"source range gap cannot silently advance cursor");}
        var wrong=Frame(104,2,device:Device.ToUpperInvariant());try{MtConnectSampleParser.Parse(wrong,Device,104);throw new InvalidOperationException("case folded");}catch(InvalidDataException){Check(true,"exact device UUID matching never falls back to a case-folded name");}
        var rollback=await Request(MtConnectSampleParser.Parse(Frame(104,2),Device,104));var before=await Count("acquisition.MtConnectObservation");
        await Exec("CREATE TRIGGER security.Fixture_IngestFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51112,'ingestion audit fixture',1; END;");
        try{await writer.IngestMtConnectAsync(pair.Channel,rollback);throw new InvalidOperationException("audit expected");}catch(SqlException error)when(error.Number==51112){Check(true,"late audit failure aborts ingestion transaction");}await Exec("DROP TRIGGER security.Fixture_IngestFailure");
        Check(await Count("acquisition.MtConnectObservation")==before&&(await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream)).Cursor?.NextSequence==104&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op",("@op",rollback.OperationId)))==0,"evidence cursor batch operation and audit roll back together");
        var twins=await Task.WhenAll(writer.IngestMtConnectAsync(pair.Channel,rollback),writer.IngestMtConnectAsync(pair.Channel,rollback));Check(twins[0].SequenceEqual(twins[1])&&await Count("acquisition.MtConnectObservation")==6,"concurrent identical retries commit one source append");
        var racing=await Request(MtConnectSampleParser.Parse(Frame(106,2),Device,106));var outcomes=await Task.WhenAll(Race(racing),Race(racing with{OperationId=Guid.NewGuid()}));Check(outcomes.Count(x=>x)==1&&await Count("acquisition.MtConnectObservation")==8,"different operations with the same cursor expectation serialize one winner");
        foreach(var table in new[]{"MtConnectBatch","MtConnectObservation"}){try{await Exec("DELETE acquisition."+table);throw new InvalidOperationException("mutable evidence");}catch(SqlException error)when(error.Number==51040){Check(true,table+" is immutable");}}
        agent.Reply=(_,_)=> (400,Encoding.UTF8.GetBytes("<MTConnectError><Header instanceId='7'/><Errors><Error errorCode='OUT_OF_RANGE'>source range unavailable</Error></Errors></MTConnectError>"),null);
        try{await client.AcquireAsync(agent.Endpoint,Device,108,2);throw new InvalidOperationException("source error ignored");}catch(MtConnectProtocolException error)when(error.ErrorResult.Errors.Single().Code=="OUT_OF_RANGE"&&error.ErrorResult.InstanceId==7){Check(true,"real HTTP OUT_OF_RANGE is explicit and never converted to an empty success");}
        agent.Reply=(_,_)=> (200,Array.Empty<byte>(),MtConnectSampleParser.MaximumDocumentBytes+1L);try{await client.AcquireAsync(agent.Endpoint,Device,108,2);throw new InvalidOperationException("oversized source");}catch(InvalidDataException){Check(true,"declared oversized HTTP response rejects before document allocation");}
        agent.Reply=(_,_)=> (200,new byte[MtConnectSampleParser.MaximumDocumentBytes+1],-1);try{await client.AcquireAsync(agent.Endpoint,Device,108,2);throw new InvalidOperationException("unbounded chunked source");}catch(InvalidDataException){Check(true,"unknown-length HTTP body is bounded while streaming");}
        InvalidSource(()=>MtConnectSampleParser.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE MTConnectStreams [<!ENTITY source 'x'>]>"+Encoding.UTF8.GetString(Frame(100,2))),Device,100),"DTD and entity expansion are prohibited");
        InvalidSource(()=>MtConnectSampleParser.Parse([0xC3,0x28],Device,100),"invalid UTF-8 cannot be normalized into source evidence");
        var malformed=XDocument.Parse(Encoding.UTF8.GetString(Frame(100,2)));malformed.Root!.Element("Header")!.SetAttributeValue("nextSequence",10000);InvalidSource(()=>MtConnectSampleParser.Parse(Encoding.UTF8.GetBytes(malformed.ToString()),Device,100),"source header cannot advance beyond available sequence frontier");
        malformed=XDocument.Parse(Encoding.UTF8.GetString(Frame(100,2)));malformed.Descendants("PartCount").First().SetAttributeValue("timestamp","12:30");InvalidSource(()=>MtConnectSampleParser.Parse(Encoding.UTF8.GetBytes(malformed.ToString()),Device,100),"timestamp interpretation cannot invent a date or local timezone");
        // Real SQL read/write ordering against approval withdrawal, with an observed audit barrier.
        foreach(var ingestionFirst in new[]{true,false})
        {
            (package,review)=await Package();activation=await Intent();ack=Ack(activation,1);await service.AcknowledgeActivationAsync(pair.Channel,ack);var cursor=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);var candidate=await Request(MtConnectSampleParser.Parse(Frame(cursor.Cursor!.NextSequence,2),Device,cursor.Cursor.NextSequence));
            await Exec("CREATE TRIGGER security.Fixture_IngestBarrier ON security.SecurityAudit AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:ingest:audit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'fixture barrier failed',1; END;");
            await using(var barrier=new SqlConnection(target.ConnectionString))
            {
                await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,"fc2:ingest:audit-barrier");var oldCount=await Count("acquisition.MtConnectObservation");
                var firstTask=ingestionFirst?writer.IngestMtConnectAsync(pair.Channel,candidate):enabling.WithdrawConfigurationApprovalAsync(reviewToken,await Withdrawal());Task<byte[]>? secondTask=null;
                try{await WaitForLock(ingestionFirst?"fc2ingest-writer":"fc2ingest-revoker","S");secondTask=ingestionFirst?enabling.WithdrawConfigurationApprovalAsync(reviewToken,await Withdrawal()):writer.IngestMtConnectAsync(pair.Channel,candidate);await WaitForLock(ingestionFirst?"fc2ingest-revoker":"fc2ingest-writer",ingestionFirst?"X":"S");Check(!secondTask.IsCompleted,"ingestion and revocation obey observed SQL gate ordering");}finally{await tx.CommitAsync();}
                await firstTask;if(ingestionFirst){await secondTask!;Check(await Count("acquisition.MtConnectObservation")==oldCount+2,"ingestion-first commit retains accepted evidence before later revocation");await Denied(()=>writer.IngestMtConnectAsync(pair.Channel,candidate),"completed receipt cannot bypass current approval revocation");}else{await Denied(()=>secondTask!,"revocation-first commit denies queued ingestion");Check(await Count("acquisition.MtConnectObservation")==oldCount,"revocation-first ordering appends no partial evidence");}
            }
            await Exec("DROP TRIGGER security.Fixture_IngestBarrier");
        }
        (package,review)=await Package();activation=await Intent();ack=Ack(activation,1);await service.AcknowledgeActivationAsync(pair.Channel,ack);context=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);
        agent.Reply=(from,count)=>(200,Frame(from,count,activity:true),null);var timer=Stopwatch.StartNew();var bulkFrame=await client.AcquireAsync(agent.Endpoint,Device,context.Cursor!.NextSequence,1000);var bulk=new MtConnectIngestionRequest(Guid.NewGuid(),site,machine,context.AcknowledgementId,Stream,context.Cursor.BatchId,context.Cursor.InstanceId,context.Cursor.NextSequence,bulkFrame.RawDocument);await Transport(bulk);timer.Stop();var trace=activities.Last(x=>x.GetTagItem("security.acquisition.ingest") is true&&x.GetTagItem("security.result")?.ToString()=="completed");var trips=Convert.ToInt64(trace.GetTagItem("security.sql.roundtrips"));Check(trips<=30&&timer.Elapsed<TimeSpan.FromSeconds(5),"1000-observation HTTP/TLS/SQL slice uses bounded full-transaction roundtrips and latency");Console.WriteLine($"MEASURE: MTConnect ingestion observations=1000 elapsedMs={timer.Elapsed.TotalMilliseconds:F2} roundtrips={trips}");
        var currentActivity=await mappingResolver.ResolveAsync(site,machine,
            package.Machines[0].Signals.Single(x=>x.SemanticKey=="machine-activity").MappingId,
            Stream);
        var firstBulk=bulkFrame.Observations[0].Sequence;
        var activityObserved=await acceptedReader.ReadBoundedAsync(site,
            ActivityQualificationIdentity.From(currentActivity.Signal,"discovery-not-continuity"),
            bulkFrame.InstanceId,firstBulk,firstBulk+3,100);
        Check(activityObserved.Count==2 &&
            activityObserved[0].RawValue=="ACTIVE" &&
            activityObserved[1].RawValue=="READY" &&
            activityObserved.All(x=>x.BatchId==bulk.OperationId),
            "approved execution-state signal resolves two actual immutable accepted observations");
        Check(currentActivity.EffectiveFrom>activityObserved[0].ObservedAt,
            "retained observation predates current approved interpretation interval");
        var trustedCandidate=AcceptedActivityContinuityQualifier.Qualify(
            currentActivity,activityObserved);
        var interpretedCandidate=ActivityCheckpointEvaluator.Advance(
            currentActivity.Signal,trustedCandidate.Qualification,
            ActivityCheckpointEvaluator.Initial(trustedCandidate.Qualification),
            trustedCandidate.Samples);
        Check(trustedCandidate.ProvenInternalLinks==1 &&
            interpretedCandidate.ClosedPeriods.Count==1 &&
            interpretedCandidate.ClosedPeriods[0].State==ActivityState.Running &&
            interpretedCandidate.ClosedPeriods[0].DurationTicks==TimeSpan.FromSeconds(2).Ticks,
            "retained accepted signals remain interpretable as candidates without publishing history");
        var atomicStore=new SqlActivityProcessorStore(target.ConnectionString);
        try
        {
            await atomicStore.ApplyAcceptedAsync(Guid.NewGuid(),target.InstallationId,
                site,machine,currentActivity.Signal.MappingId,Stream,
                bulkFrame.InstanceId,firstBulk,firstBulk+3,0);
            throw new InvalidOperationException("Pre-activation evidence was published.");
        }
        catch(InvalidDataException)
        {
            Check(true,"pre-activation observations fail closed without writing a checkpoint or period");
        }
        Check((await atomicStore.ReadCheckpointAsync(trustedCandidate.Qualification))
            .NextGeneration==0 &&
            (await atomicStore.ReadPeriodPageAsync(trustedCandidate.Qualification))
                .Rows.Count==0,
            "rejected historical interpretation leaves no activity effects");
        // A delayed source-to-checkpoint write cannot retain a mapping
        // eligibility decision made before a processor lock wait. Hold the
        // exact processor lock, race a new approved current head, then allow
        // the waiting writer to re-evaluate on its own transaction.
        var activityResource=$"fc2:activity:{machine:N}:{currentActivity.Signal.MappingId:N}:"+
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Stream)));
        var waitingStore=new SqlActivityProcessorStore(
            new SqlConnectionStringBuilder(target.ConnectionString){
                ApplicationName="fc2-activity-waiter"}.ConnectionString);
        await using(var lockConnection=new SqlConnection(target.ConnectionString))
        {
            await lockConnection.OpenAsync();
            await using var lockTransaction=(SqlTransaction)await lockConnection.BeginTransactionAsync();
            await Hold(lockConnection,lockTransaction,activityResource);
            var delayed=waitingStore.ApplyAcceptedAsync(Guid.NewGuid(),target.InstallationId,
                site,machine,currentActivity.Signal.MappingId,Stream,
                bulkFrame.InstanceId,firstBulk,firstBulk+3,0);
            try
            {
                await WaitForLock("fc2-activity-waiter","X");
                Check(!delayed.IsCompleted,
                    "accepted activity writer is observed waiting on the exact processor lock");
            }
            finally
            {
                await lockTransaction.CommitAsync();
            }
            try
            {
                await delayed;
                throw new InvalidOperationException("Pre-activation delayed writer published source.");
            }
            catch(InvalidDataException)
            {
                Check(true,
                    "accepted activity writer rechecks interval after processor wait and refuses historical source");
            }
        }
        var latest=await writer.ReadMtConnectEvidenceAsync(token,site,machine,Stream,100);Check(latest.Count==100,"latest accepted evidence read is explicitly bounded");
        // An accepted 100-observation read sharing one acquisition batch
        // transfers and hashes its source document only once, not 100 times.
        var bulkCounter=latest.First(x=>x.Address=="Counter");
        var bulkValue=bulkFrame.Observations.First(x=>x.DataItemId=="Counter").RawValue;
        var bulkSignal=new QualifiedActivitySignal(machine,Stream,bulkCounter.MappingId,
            "Counter",new Dictionary<string,ActivityState>(StringComparer.Ordinal)
            {[bulkValue]=ActivityState.Running});
        var bulkIdentity=ActivityQualificationIdentity.From(
            bulkSignal,"accepted-reader-bulk-fixture");
        var acceptedTraces=new List<Activity>();
        using(var acceptedListener=new ActivityListener
        {
            ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Activity.AcceptedEvidence",
            Sample=(ref ActivityCreationOptions<ActivityContext> _)=>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped=a=>{lock(acceptedTraces)acceptedTraces.Add(a);}
        })
        {
            ActivitySource.AddActivityListener(acceptedListener);
            var firstBulkSequence=bulkFrame.Observations[0].Sequence;
            var qualifiedRows=await acceptedReader.ReadBoundedAsync(site,bulkIdentity,
                bulkFrame.InstanceId,firstBulkSequence-1,firstBulkSequence+199,100);
            Check(qualifiedRows.Count==100 &&
                qualifiedRows.All(x=>x.BatchId==bulk.OperationId &&
                    x.CanonicalSha256.Length==64 && x.DocumentSha256.Length==64),
                "100 retained observations are verified against the exact accepted acquisition batch");
            Activity? sourceTrace;
            lock(acceptedTraces)sourceTrace=acceptedTraces
                .SingleOrDefault(x=>x.OperationName=="activity.accepted-source.read");
            Check(sourceTrace is not null &&
                Convert.ToInt32(sourceTrace.GetTagItem("accepted-source.observations"))==100 &&
                Convert.ToInt32(sourceTrace.GetTagItem("accepted-source.distinct-batches"))==1 &&
                Convert.ToInt32(sourceTrace.GetTagItem("accepted-source.batch-hash-verifications"))==1 &&
                Convert.ToInt32(sourceTrace.GetTagItem("accepted-source.batch-parses"))==1,
                "100-observation page verifies and parses the one source batch exactly once");
            Console.WriteLine("MEASURE: activity accepted observations=100 distinctBatchDocuments=1 "+
                $"aggregateBytes={sourceTrace?.GetTagItem("accepted-source.aggregate-bytes")} "+
                $"sqlRoundtrips={sourceTrace?.GetTagItem("accepted-source.sql.roundtrips")}");
        }
        for(var i=0;i<5;i++){var largeContext=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);var large=XDocument.Parse(Encoding.UTF8.GetString(Frame(largeContext.Cursor!.NextSequence,1)));large.Descendants("PartCount").Single().Value=new string('7',900000);var largeBytes=Encoding.UTF8.GetBytes(large.ToString(SaveOptions.DisableFormatting));await writer.IngestMtConnectAsync(pair.Channel,new(Guid.NewGuid(),site,machine,largeContext.AcknowledgementId,Stream,largeContext.Cursor.BatchId,largeContext.Cursor.InstanceId,largeContext.Cursor.NextSequence,largeBytes));}
        try{await writer.ReadMtConnectEvidenceAsync(token,site,machine,Stream,100);throw new InvalidOperationException("unbounded evidence bytes");}catch(SqlException error)when(error.Number==51044){Check(true,"evidence read rejects an oversized byte page instead of transferring all requested LOB values");}
        Check((await writer.ReadMtConnectEvidenceAsync(token,site,machine,Stream,1)).Single().RawValue.Length==900000,"bounded smaller page returns a legitimately retained large source value");
        var saved=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);await Exec("DELETE acquisition.MtConnectCursor WHERE MachineId=@machine",("@machine",machine));await Denied(()=>writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream),"missing cursor with retained batches cannot silently bootstrap a new stream");await Exec("INSERT acquisition.MtConnectCursor VALUES(@site,@machine,@stream,7,@next,100,@batch)",("@site",site),("@machine",machine),("@stream",Encoding.UTF8.GetBytes(Stream)),("@next",(decimal)saved.Cursor!.NextSequence),("@batch",saved.Cursor.BatchId));
        var heldRequest=await Request(MtConnectSampleParser.Parse(Frame(saved.Cursor!.NextSequence,2),Device,saved.Cursor.NextSequence));var opState=(Guid)(await Scalar("SELECT StateId FROM configuration.MachineOperationalState WHERE MachineId=@machine",("@machine",machine)))!;
        await service.SuspendMachineAsync(holdToken,new(Guid.NewGuid(),Guid.NewGuid(),site,machine,opState,1,await Epoch(),"hold ingestion fixture"));await Denied(()=>writer.IngestMtConnectAsync(pair.Channel,heldRequest),"machine suspension blocks prepared ingestion before commit");
        opState=(Guid)(await Scalar("SELECT StateId FROM configuration.MachineOperationalState WHERE MachineId=@machine",("@machine",machine)))!;var resume=new MachineEnableProposalRequest(Guid.NewGuid(),Guid.NewGuid(),site,machine,opState,2,await Epoch(),now.AddMinutes(10),"resume ingestion fixture");await service.ProposeMachineEnableAsync(token,resume);await service.ApproveMachineEnableAsync(reviewToken,Guid.NewGuid(),resume.ProposalId,Guid.NewGuid(),site,machine,await Epoch(),"independent resume");await Denied(()=>writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream),"resume does not revive old ingestion acknowledgement");activation=await Intent();ack=Ack(activation,3);await service.AcknowledgeActivationAsync(pair.Channel,ack);Check((await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream)).Cursor?.NextSequence==saved.Cursor.NextSequence,"fresh acknowledgement restores authorization without rewriting durable source cursor");
        var deadline=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(DATEADD(second,2,SYSUTCDATETIME()),'+00:00')"))!;activation=await Intent(until:deadline);ack=Ack(activation,3);await service.AcknowledgeActivationAsync(pair.Channel,ack);var expiryRequest=await Request(MtConnectSampleParser.Parse(Frame(saved.Cursor.NextSequence,2),Device,saved.Cursor.NextSequence));
        await using(var barrier=new SqlConnection(target.ConnectionString))
        {
            await barrier.OpenAsync();await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();await Hold(barrier,tx,FactoryConnect.Sql.Configuration.ConfigurationLockResources.Machine(target.InstallationId,machine));var expiring=writer.IngestMtConnectAsync(pair.Channel,expiryRequest);
            try{await WaitForLock("fc2ingest-writer","X");while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<deadline)await Task.Delay(20);}finally{await tx.CommitAsync();}
            await Denied(()=>expiring,"ingestion rechecks activation and acknowledgement expiry after machine lock wait");
        }
        activation=await Intent();ack=Ack(activation,3);await service.AcknowledgeActivationAsync(pair.Channel,ack);await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);
        var policy=await Proposal("schedule");await service.ProposePolicyRevocationAsync(token,policy);await Approve(policy);await Denied(()=>writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream),"effective policy revocation stops acquisition context without erasing evidence");
        Directory.CreateDirectory("artifacts/ingestion");await File.WriteAllTextAsync("artifacts/ingestion/first-machine.json",JsonSerializer.Serialize(new{schemaVersion=1,fixture=true,scope="Controlled real HTTP source and mutual TLS delivery, disposable V2 SQL; not Gajra production",source="abilgaiyan/FactoryConnect@088bf524",savedExample=example,finalCursor=new{instance="7",next=saved.Cursor.NextSequence.ToString(CultureInfo.InvariantCulture),baseline="100",completeness="retained-window-only"},batch1000=new{elapsedMs=timer.Elapsed.TotalMilliseconds,roundtrips=trips},checks=count},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("DEMO: actual saved source values: "+JsonSerializer.Serialize(example.Select(x=>new{x.Sequence,x.Address,x.RawValue,x.Quality})));return count;

        void InvalidSource(Action action,string name){try{action();}catch(Exception error)when(error is InvalidDataException or System.Xml.XmlException or DecoderFallbackException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<bool> Race(MtConnectIngestionRequest r){try{await writer.IngestMtConnectAsync(pair.Channel,r);return true;}catch(SecurityConflictException){return false;}}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<long> Count(string table)=>Convert.ToInt64(await Scalar("SELECT COUNT_BIG(*) FROM "+table));
        byte[] Frame(ulong from,int count,ulong instance=7,ulong first=100,string device=Device,bool changed=false,bool activity=false)
        {
            var samples=Enumerable.Range(0,count).Where(i=>!activity||i is not (1 or 3))
                .Select(i=>{
                    var sequence=from+(ulong)i;
                    return new XElement("PartCount",
                        new XAttribute("dataItemId",sequence%2==0?"Counter":"counter"),
                        new XAttribute("timestamp",stamp),new XAttribute("sequence",sequence),
                        changed&&sequence==100?"999":sequence==100?" 00042 ":
                        sequence==102?"UNAVAILABLE":sequence.ToString(CultureInfo.InvariantCulture));
                });
            var component=new XElement("ComponentStream",new XElement("Samples",samples));
            if(activity)
            {
                component.Add(new XElement("Events",new[]{1,3}.Where(i=>i<count).Select(i=>
                    new XElement("Execution",new XAttribute("dataItemId","execution"),
                        new XAttribute("timestamp",now.AddSeconds(i).ToString("O",CultureInfo.InvariantCulture)),
                        new XAttribute("sequence",from+(ulong)i),i==1?"ACTIVE":"READY"))));
            }
            var xml=new XElement("MTConnectStreams",
                new XElement("Header",new XAttribute("instanceId",instance),
                    new XAttribute("firstSequence",first),
                    new XAttribute("lastSequence",from+(ulong)count-1),
                    new XAttribute("nextSequence",from+(ulong)count)),
                new XElement("Streams",new XElement("DeviceStream",
                    new XAttribute("uuid",device),component)));
            return Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        }
        async Task<MtConnectIngestionRequest> Request(MtConnectSampleFrame frame){var current=await writer.ReadMtConnectAcquisitionContextAsync(pair.Channel,site,machine,Stream);var from=frame.Observations.Count>0?frame.Observations[0].Sequence:current.Cursor?.NextSequence??frame.FirstSequence;return new(Guid.NewGuid(),site,machine,current.AcknowledgementId,Stream,current.Cursor?.BatchId,current.Cursor?.InstanceId,from,frame.RawDocument);}
        async Task<byte[]> Transport(MtConnectIngestionRequest request)
        {
            var receiver=writer.ReceiveMtConnectAsync(pair.Channel,new(request.OperationId,request.SiteId,request.MachineId,request.AcknowledgementId,request.StreamKey,request.ExpectedBatchId,request.ExpectedInstanceId,request.FromSequence));var prefix=new byte[4];BinaryPrimitives.WriteInt32BigEndian(prefix,request.RawDocument.Length);await pair.Client.WriteAsync(prefix);await pair.Client.WriteAsync(request.RawDocument);await pair.Client.FlushAsync();return await receiver;
        }
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
            for(var i=0;i<size;i++){var id=i==0?machine:Guid.NewGuid();if(i>0)await Exec("INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Batch fixture')",("@machine",id),("@site",site));machines.Add(new(id,line,now.AddMinutes(-5),null,new(schedule,hash),new(breaks,hash),[new(Guid.NewGuid(),"counter-upper","mtconnect",Stream,"utf-8",Encoding.UTF8.GetBytes("Counter"),"identity-v1"),new(Guid.NewGuid(),"counter-lower","mtconnect",Stream,"utf-8",Encoding.UTF8.GetBytes("counter"),"identity-v1"),new(Guid.NewGuid(),"machine-activity","mtconnect",Stream,"utf-8",Encoding.UTF8.GetBytes("execution"),"activity-state-v1|ACTIVE=Running|READY=Idle")],["counter-reading"]));}
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
