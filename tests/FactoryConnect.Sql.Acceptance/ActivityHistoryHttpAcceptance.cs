using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryConnect.Api;
using FactoryConnect.Sql.ActivityProcessing;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class ActivityHistoryHttpAcceptance
{
    private sealed class Verifier(string subject):IHumanIdentityVerifier
    {
        public Task<VerifiedHumanIdentity> VerifyAsync(string assertion,CancellationToken ct)
        {
            if(assertion!="activity-http-proof")throw new SecurityDeniedException();
            var now=DateTimeOffset.UtcNow;
            return Task.FromResult(new VerifiedHumanIdentity("https://certificate.fixture/tenant",subject,
                Guid.NewGuid().ToString("N"),"activity-http-mfa",now.AddSeconds(-1),now.AddMinutes(3)));
        }
    }
    internal static async Task<int> Run(string sql,Guid installation,Guid site,Guid machine,Guid other,
        Guid mapping,byte[] admin,Func<Task<long>> epoch)
    {
        var checks=0;var principal=Guid.NewGuid();var subject="activity-http-"+principal.ToString("N");
        var authority=new SqlSecurityAuthority(sql,installation);
        await authority.EnrollHumanAsync(admin,new(Guid.NewGuid(),principal,"https://certificate.fixture/tenant",subject,await epoch(),"activity HTTP fixture"));
        await using var c=new SqlConnection(sql);await c.OpenAsync();
        var parent=(Guid)(await Scalar("SELECT g.GrantId FROM security.PermissionGrant g JOIN security.SessionAuthority s ON s.PrincipalId=g.GranteeId WHERE s.TokenHash=@token AND g.Permission='commissioning.read'",("@token",admin)))!;
        var grant=await Grant();
        var traces=new List<Activity>();
        using var listener=new ActivityListener {
            ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",
            Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped=a=>{lock(traces)traces.Add(a);}
        };ActivitySource.AddActivityListener(listener);
        var appName="fc2-activity-history-http-"+Guid.NewGuid().ToString("N");
        using var factory=new WebApplicationFactory<HostAssemblyMarker>().WithWebHostBuilder(host=> {
            host.UseContentRoot(Directory.GetCurrentDirectory());
            host.ConfigureAppConfiguration((_,config)=>config.AddInMemoryCollection(new Dictionary<string,string?> {
                ["ConnectionStrings:Evidence"]=new SqlConnectionStringBuilder(sql){ApplicationName=appName}.ConnectionString,
                ["Evidence:InstallationId"]=installation.ToString(),["Evidence:SiteId"]=site.ToString(),
                ["Evidence:StreamKey"]="mtconnect:uuid:fixture-device"
            }));
            host.ConfigureTestServices(services=>services.AddSingleton<IHumanIdentityVerifier>(new Verifier(subject)));
        });
        using var http=factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost/"),AllowAutoRedirect=false });
        using var login=await http.PostAsync("/api/security/v2/human-sessions",new StringContent("activity-http-proof"));
        Check(login.StatusCode==HttpStatusCode.OK,"independently verified human proof creates activity read session");
        using var identity=JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var bearer=identity.RootElement.GetProperty("bearer").GetString()!;
        var bytes=Convert.FromBase64String(bearer);var hash=SHA256.HashData(bytes);CryptographicOperations.ZeroMemory(bytes);
        var list=$"/api/commissioning/v2/machines/{machine}/activity";
        var page=list+"/"+mapping;
        var before=(long)(await Scalar("SELECT COUNT_BIG(*) FROM activity.ProcessorOperation WHERE MachineId=@machine",("@machine",machine)))!;
        var cp=(string)(await Scalar("SELECT CheckpointJson FROM activity.ProcessorCheckpoint WHERE MachineId=@machine AND MappingId=@mapping",("@machine",machine),("@mapping",mapping)))!;
        try
        {
            using var series=await Get(list);using var sj=JsonDocument.Parse(await series.Content.ReadAsStringAsync());
            Check(series.StatusCode==HttpStatusCode.OK && sj.RootElement.GetProperty("series").GetArrayLength()==1 &&
                sj.RootElement.GetProperty("currentConnectivity").ValueKind==JsonValueKind.Null &&
                sj.RootElement.GetProperty("verification").GetString()=="immutable-publication-receipts",
                "authorized series discovery exposes exact persisted qualification without live-state claims");
            using var first=await Get(page+"?take=1");using var fj=JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var row=fj.RootElement.GetProperty("rows")[0];
            Check(first.StatusCode==HttpStatusCode.OK && first.Headers.CacheControl?.NoStore==true &&
                row.GetProperty("state").GetString()=="Running" && row.GetProperty("startRawValue").GetString()=="ACTIVE" &&
                row.GetProperty("endRawValue").GetString()=="READY" && row.GetProperty("instanceId").ValueKind==JsonValueKind.String &&
                row.GetProperty("startSequence").ValueKind==JsonValueKind.String && row.GetProperty("durationTicks").ValueKind==JsonValueKind.String,
                "bounded HTTP page preserves exact source values positions and duration as decimal strings");
            var cursor=fj.RootElement.GetProperty("nextCursor").GetString()!;
            Check(!string.IsNullOrEmpty(cursor),"multi-period fixture returns explicit bounded next cursor");
            using var second=await Get(page+"?take=1&cursor="+Uri.EscapeDataString(cursor));
            using var secondJson=JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Check(second.StatusCode==HttpStatusCode.OK && secondJson.RootElement.GetProperty("rows")[0].GetProperty("effectId").GetGuid()!=row.GetProperty("effectId").GetGuid(),
                "keyset continuation returns a distinct older exact period");
            Directory.CreateDirectory("artifacts/ingestion");
            var profile=await ActivityHistorySqlProfile.CaptureAsync(c,appName,async()=> {
                var start=DateTimeOffset.UtcNow;
                using var discovery=await Get(list);
                using var head=await Get(page+"?take=1");
                using var older=await Get(page+"?take=1&cursor="+Uri.EscapeDataString(cursor));
                Check(discovery.IsSuccessStatusCode&&head.IsSuccessStatusCode&&older.IsSuccessStatusCode,"profiled discovery and keyset pages return verified publications");
                return new[]{new ActivityHistoryMeasurementAcceptance.Span("http-history",start,DateTimeOffset.UtcNow-start,new Dictionary<string,object?>())};
            },$"artifacts/ingestion/activity-http-profile-{before}.json");
            var periodAccess=profile.Accesses.Where(x=>x.Table=="ClosedPeriod").ToArray();
            Check(profile.PlanEvents>0 && periodAccess.Length>0 &&
                periodAccess.All(x=>x.ActualRowsRead.HasValue && x.ActualRowsRead<=10) &&
                !profile.Accesses.Any(x=>x.Table is "MtConnectObservation" or "MtConnectBatch"),
                "profiled HTTP pages use bounded period access without raw historical reconstruction");
            Console.WriteLine($"MEASURE: activity HTTP retained operations={before}, period rows read={periodAccess.Sum(x=>x.ActualRowsRead)}, command logical reads={profile.Commands.Sum(x=>x.LogicalReads)}");
            var decoded=cursor.Replace('-','+').Replace('_','/');decoded=decoded.PadRight((decoded.Length+3)/4*4,'=');
            var altered=JsonNode.Parse(Convert.FromBase64String(decoded))!;altered["MappingId"]=Guid.NewGuid();
            var forged=Convert.ToBase64String(Encoding.UTF8.GetBytes(altered.ToJsonString())).TrimEnd('=').Replace('+','-').Replace('/','_');
            using(var invalid=await Get(page+"?cursor="+forged))Check(invalid.StatusCode==HttpStatusCode.BadRequest,"cross-qualification cursor cannot silently query another history");
            using(var oversized=await Get(page+"?take=101"))Check(oversized.StatusCode==HttpStatusCode.BadRequest,"activity page count is explicitly bounded");
            using(var denied=await Get($"/api/commissioning/v2/machines/{other}/activity"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"activity series cannot disclose an unassigned machine");
            using(var plaintext=await Get("http://localhost"+page))Check(plaintext.StatusCode==HttpStatusCode.Forbidden,"activity bearer is refused over plaintext HTTP");
            using(var unknown=await Get(list+"/"+Guid.NewGuid()))Check(unknown.StatusCode==HttpStatusCode.NotFound,"absent series is explicit and does not return invented intervals");

            var effect=row.GetProperty("effectId").GetGuid();var original=row.GetProperty("startRawValue").GetString()!;
            await Exec("DISABLE TRIGGER activity.TR_ClosedPeriodImmutable ON activity.ClosedPeriod");
            try
            {
                await Exec("UPDATE activity.ClosedPeriod SET StartRawValue=N'FORGED' WHERE EffectId=@effect",("@effect",effect));
                using(var corrupt=await Get(page))Check(corrupt.StatusCode==HttpStatusCode.ServiceUnavailable,"altered published value fails receipt integrity without disclosing a partial page");
                await Exec("UPDATE activity.ClosedPeriod SET StartRawValue=REPLICATE(CONVERT(nvarchar(max),N'X'),5000000) WHERE EffectId=@effect",("@effect",effect));
                using(var big=await Get(page))Check(big.StatusCode==HttpStatusCode.ServiceUnavailable && (await big.Content.ReadAsByteArrayAsync()).Length<4096,
                    "SQL byte budget rejects oversized period data before HTTP transfer");
            }
            finally
            {
                await Exec("UPDATE activity.ClosedPeriod SET StartRawValue=@value WHERE EffectId=@effect",("@value",original),("@effect",effect));
                await Exec("ENABLE TRIGGER activity.TR_ClosedPeriodImmutable ON activity.ClosedPeriod");
            }
            var broken=JsonNode.Parse(cp)!;broken["AcceptedSource"]!["OperationId"]=Guid.NewGuid();
            await SetCheckpoint(broken.ToJsonString());
            try { using var corrupt=await Get(list);Check(corrupt.StatusCode==HttpStatusCode.ServiceUnavailable,"checksum-consistent checkpoint forgery fails immutable publication ancestry"); }
            finally { await SetCheckpoint(cp); }

            await authority.RevokeGrantAsync(admin,Guid.NewGuid(),grant,await epoch(),"activity read grant revoked");
            using(var revoked=await Get(page))Check(revoked.StatusCode==HttpStatusCode.Unauthorized,"grant revocation denies published activity reads");
            grant=await Grant();
            using(var restored=await Get(page))Check(restored.StatusCode==HttpStatusCode.OK,"new explicit grant restores read authority without rewriting evidence");
            await authority.RevokeSessionAsync(admin,Guid.NewGuid(),hash,await epoch(),"activity read session revoked");
            using(var revoked=await Get(list))Check(revoked.StatusCode==HttpStatusCode.Unauthorized,"session revocation denies activity series discovery");
            using(var revoked=await Get(page))Check(revoked.StatusCode==HttpStatusCode.Unauthorized,"session revocation denies activity page retrieval");
            Check((string)(await Scalar("SELECT CheckpointJson FROM activity.ProcessorCheckpoint WHERE MachineId=@machine AND MappingId=@mapping",("@machine",machine),("@mapping",mapping)))! == cp &&
                (long)(await Scalar("SELECT COUNT_BIG(*) FROM activity.ProcessorOperation WHERE MachineId=@machine",("@machine",machine)))! == before,
                "activity HTTP reads do not process evidence or advance checkpoint/receipts");
            Activity[] observed;lock(traces)observed=traces.Where(x=>x.GetTagItem("security.activity.read") is true).ToArray();
            var trips=observed.Max(x=>Convert.ToInt64(x.GetTagItem("security.sql.roundtrips")));
            Check(observed.Length>5 && trips<=50,"activity HTTP read SQL work is observable and has no per-period SQL loop");
            Console.WriteLine($"MEASURE: activity HTTP maximum SQL roundtrips={trips}");
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
        return checks;

        async Task<Guid> Grant()
        {
            var id=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
            await authority.GrantPermissionAsync(admin,new(Guid.NewGuid(),id,principal,parent,"commissioning.read",AuthorizationScope.Machine(site,machine),true,false,
                now.AddSeconds(-1),now.AddMinutes(10),await epoch(),"activity HTTP fixture grant"));return id;
        }
        async Task<HttpResponseMessage> Get(string path)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);return await http.SendAsync(request);
        }
        async Task<object?> Scalar(string text,params (string,object)[] args){await using var q=new SqlCommand(text,c);Bind(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string text,params (string,object)[] args){await using var q=new SqlCommand(text,c);Bind(q,args);await q.ExecuteNonQueryAsync();}
        async Task SetCheckpoint(string json)=>await Exec("UPDATE activity.ProcessorCheckpoint SET CheckpointJson=@json,CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json)) WHERE MachineId=@machine AND MappingId=@mapping",
            ("@json",json),("@machine",machine),("@mapping",mapping));
        void Check(bool value,string label){if(!value)throw new InvalidOperationException("Activity HTTP: "+label);checks++;Console.WriteLine("PASS: activity HTTP "+label);}
    }
    private static void Bind(SqlCommand q,params (string Name,object Value)[] args)
    {foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,System.Data.SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
