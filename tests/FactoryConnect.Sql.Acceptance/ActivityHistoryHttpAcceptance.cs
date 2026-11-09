using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    // Acceptance-only command-start capture. Parameters, tokens and result data are excluded.
    private sealed class SqlCommandCapture:IObserver<DiagnosticListener>,IObserver<KeyValuePair<string,object?>>,IDisposable
    {
        private readonly List<IDisposable> subscriptions=new();
        private readonly List<(Activity Owner,string Text)> commands=new();
        private readonly IDisposable discovery;
        public SqlCommandCapture(){discovery=DiagnosticListener.AllListeners.Subscribe(this);}
        public void OnNext(DiagnosticListener listener)
        {
            if(listener.Name!="SqlClientDiagnosticListener")return;
            lock(subscriptions)subscriptions.Add(listener.Subscribe((IObserver<KeyValuePair<string,object?>>)this));
        }
        public void OnNext(KeyValuePair<string,object?> value)
        {
            if(value.Key!="Microsoft.Data.SqlClient.WriteCommandBefore" || value.Value is null)return;
            if(value.Value.GetType().GetProperty("Command")?.GetValue(value.Value) is not SqlCommand command)return;
            var owner=Activity.Current;
            while(owner is not null && owner.OperationName!="security.transaction")owner=owner.Parent;
            if(owner is null)return;
            var sql=Regex.Replace(command.CommandText,@"\\s+"," ").Trim();
            // Static SQL text only; never include parameter values.
            var prefix=sql.Length>110?sql[..110]+"...":sql;
            lock(commands)commands.Add((owner,prefix));
        }
        public void OnError(Exception error){}
        public void OnCompleted(){}
        public void Clear(){lock(commands)commands.Clear();}
        public string[] For(Activity owner)
        {
            lock(commands)return commands.Where(x=>ReferenceEquals(x.Owner,owner)).Select(x=>x.Text).ToArray();
        }
        public int Total{get{lock(commands)return commands.Count;}}
        public void Dispose()
        {
            discovery.Dispose();
            lock(subscriptions)foreach(var subscription in subscriptions)subscription.Dispose();
        }
    }

    private sealed class FixtureHold(SqlConnection connection,SqlTransaction transaction):IAsyncDisposable
    {
        private bool released;
        public SqlConnection Connection=>connection;
        public SqlTransaction Transaction=>transaction;
        public async ValueTask DisposeAsync()
        {
            if(released)return;
            released=true;
            try{await transaction.RollbackAsync();}
            finally{await transaction.DisposeAsync();await connection.DisposeAsync();}
        }
    }
    private static async Task<FixtureHold> HoldExclusive(string sql,string resource)
    {
        var connection=new SqlConnection(sql);
        try
        {
            await connection.OpenAsync();
            var transaction=(SqlTransaction)await connection.BeginTransactionAsync();
            try
            {
                await using var gate=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51099,'Fixture gate unavailable.',1;",connection,transaction);
                gate.Parameters.AddWithValue("@resource",resource);
                await gate.ExecuteNonQueryAsync();
                return new FixtureHold(connection,transaction);
            }
            catch{await transaction.DisposeAsync();throw;}
        }
        catch{await connection.DisposeAsync();throw;}
    }

    private sealed class Verifier(string subject):IHumanIdentityVerifier
    {
        public Task<VerifiedHumanIdentity> VerifyAsync(string assertion,CancellationToken ct)
        {
            if(assertion is not ("activity-http-proof" or "activity-http-expiring"))throw new SecurityDeniedException();
            var now=DateTimeOffset.UtcNow;
            return Task.FromResult(new VerifiedHumanIdentity("https://certificate.fixture/tenant",subject,
                Guid.NewGuid().ToString("N"),"activity-http-mfa",now.AddSeconds(-1),assertion=="activity-http-expiring"?now.AddSeconds(4):now.AddMinutes(3)));
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
        using var commandCapture=new SqlCommandCapture();
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
            // Existing security.transaction telemetry measures actual server roundtrips.
            // Capture each endpoint separately; the 100-distinct-operation Gate 5
            // threshold is not asserted against this small correctness fixture.
            long MeasureReadTrips(string path,out Activity owner)
            {
                Activity[] selected;
                lock(traces)selected=traces.Where(x=>x.GetTagItem("security.activity.read") is true &&
                    x.GetTagItem("security.sql.roundtrips") is not null).ToArray();
                if(selected.Length==0)throw new InvalidOperationException("Missing activity read SQL telemetry: "+path);
                Check(selected.Length==1,"exactly one activity read transaction belongs to "+path);
                owner=selected[0];
                return Convert.ToInt64(owner.GetTagItem("security.sql.roundtrips"));
            }
            void ReportCommands(string path,Activity owner,long roundtrips)
            {
                var commands=commandCapture.For(owner);
                Console.WriteLine($"MEASURE: activity HTTP {path} SQL commandStarts={commands.Length} serverRoundtrips={roundtrips} roundtripMinusCommandStarts={roundtrips-commands.Length} unattributedCommandStarts={commandCapture.Total-commands.Length}");
                for(var i=0;i<commands.Length;i++)
                    Console.WriteLine($"TRACE: {path} command {i+1:00}: {commands[i]}");
                Check(commands.Length>0,"SqlClient diagnostic command-start capture correlated to "+path);
            }
            lock(traces)traces.Clear();commandCapture.Clear();
            using var series=await Get(list);using var sj=JsonDocument.Parse(await series.Content.ReadAsStringAsync());
            var discoveryTrips=MeasureReadTrips("series discovery",out var discoveryOwner);
            Console.WriteLine($"MEASURE: activity HTTP series discovery SQL roundtrips={discoveryTrips}");
            ReportCommands("series discovery",discoveryOwner,discoveryTrips);
            Check(series.StatusCode==HttpStatusCode.OK && sj.RootElement.GetProperty("series").GetArrayLength()==1 &&
                sj.RootElement.GetProperty("currentConnectivity").ValueKind==JsonValueKind.Null &&
                sj.RootElement.GetProperty("verification").GetString()=="immutable-publication-receipts",
                "authorized series discovery exposes exact persisted qualification without live-state claims");
            lock(traces)traces.Clear();commandCapture.Clear();
            using var first=await Get(page+"?take=1");using var fj=JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var pageTrips=MeasureReadTrips("history page",out var pageOwner);
            Console.WriteLine($"MEASURE: activity HTTP history page SQL roundtrips={pageTrips}");
            ReportCommands("history page",pageOwner,pageTrips);
            // This is a small-fixture regression guard, not the required
            // 100-distinct-operation benchmark. Keep the latter gate open.
            Check(discoveryTrips<=17 && pageTrips<=17,
                "small-fixture authenticated series and page reads remain within 17 SQL roundtrips");

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
            // Traverse the complete retained history rather than validating only
            // the first continuation. The keyset must neither repeat nor omit rows.
            var expectedPeriodCount=Convert.ToInt32(await Scalar(
                "SELECT COUNT(*) FROM activity.ClosedPeriod WHERE MachineId=@machine AND MappingId=@mapping",
                ("@machine",machine),("@mapping",mapping)));
            var traversed=new HashSet<Guid>{row.GetProperty("effectId").GetGuid()};
            var nextCursor=cursor;
            while(nextCursor is not null)
            {
                using var continuation=await Get(page+"?take=100&cursor="+Uri.EscapeDataString(nextCursor));
                Check(continuation.StatusCode==HttpStatusCode.OK,
                    "complete keyset traversal returns a successful bounded page");
                using var continuationJson=JsonDocument.Parse(await continuation.Content.ReadAsStringAsync());
                var continuationRows=continuationJson.RootElement.GetProperty("rows");
                Check(continuationRows.GetArrayLength() is >0 and <=100,
                    "complete keyset traversal returns a bounded nonempty page");
                foreach(var period in continuationRows.EnumerateArray())
                    Check(traversed.Add(period.GetProperty("effectId").GetGuid()),
                        "complete keyset traversal never duplicates a period");
                var next=continuationJson.RootElement.GetProperty("nextCursor");
                nextCursor=next.ValueKind==JsonValueKind.Null?null:next.GetString();
                Check(traversed.Count<=expectedPeriodCount,
                    "complete keyset traversal cannot exceed retained period count");
            }
            Check(traversed.Count==expectedPeriodCount,
                "complete keyset traversal returns every retained period exactly once");
            string deepCursor;
            await using(var deep=new SqlCommand("SELECT EffectId,StartedAt,InstanceId,StartSequence FROM activity.ClosedPeriod WHERE MachineId=@machine AND MappingId=@mapping ORDER BY StartedAt,InstanceId,StartSequence,EffectId OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY",c))
            {
                // A real old position simulates a cursor obtained by earlier bounded browsing.
                deep.Parameters.AddWithValue("@machine",machine);deep.Parameters.AddWithValue("@mapping",mapping);
                await using var r=await deep.ExecuteReaderAsync();
                Check(await r.ReadAsync(),"history fixture has a real deep keyset position");
                deepCursor=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
                    MachineId=machine,StreamKey="mtconnect:uuid:fixture-device",MappingId=mapping,
                    Qualification=sj.RootElement.GetProperty("series")[0].GetProperty("qualificationSha256").GetString(),
                    StartedAt=r.GetDateTimeOffset(1).ToString("O",System.Globalization.CultureInfo.InvariantCulture),
                    InstanceId=r.GetDecimal(2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StartSequence=r.GetDecimal(3).ToString(System.Globalization.CultureInfo.InvariantCulture),EffectId=r.GetGuid(0)
                })).TrimEnd('=').Replace('+','-').Replace('/','_');
            }
            Directory.CreateDirectory("artifacts/ingestion");
            var profile=await ActivityHistorySqlProfile.CaptureAsync(c,appName,async()=> {
                var start=DateTimeOffset.UtcNow;
                using var discovery=await Get(list);
                using var head=await Get(page+"?take=1");
                using var older=await Get(page+"?take=1&cursor="+Uri.EscapeDataString(deepCursor));
                using var deepResult=JsonDocument.Parse(await older.Content.ReadAsStringAsync());
                Check(deepResult.RootElement.GetProperty("rows").GetArrayLength()==1 && deepResult.RootElement.GetProperty("nextCursor").ValueKind==JsonValueKind.Null,"deep keyset read returns the oldest exact retained publication");
                Check(discovery.IsSuccessStatusCode&&head.IsSuccessStatusCode&&older.IsSuccessStatusCode,"profiled discovery and keyset pages return verified publications");
                return new[]{new ActivityHistoryMeasurementAcceptance.Span("http-history",start,DateTimeOffset.UtcNow-start,new Dictionary<string,object?>())};
            },$"artifacts/ingestion/activity-http-profile-{before}.json","ClosedPeriod");
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
            // A cursor is bound to the exact immutable qualification digest, not
            // merely to the machine/mapping identifiers. Reject a syntactically
            // valid cursor from a different qualification before reading history.
            var foreignQualification=JsonNode.Parse(Convert.FromBase64String(decoded))!;
            foreignQualification["Qualification"]=Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()));
            var wrongQualification=Convert.ToBase64String(Encoding.UTF8.GetBytes(foreignQualification.ToJsonString()))
                .TrimEnd('=').Replace('+','-').Replace('/','_');
            using(var wrong=await Get(page+"?cursor="+Uri.EscapeDataString(wrongQualification)))
                Check(wrong.StatusCode==HttpStatusCode.BadRequest,
                    "HTTP cursor with foreign qualification digest fails closed");
            using(var tooLong=await Get(page+"?cursor="+new string('A',2049)))
                Check(tooLong.StatusCode==HttpStatusCode.BadRequest,
                    "HTTP oversized cursor is rejected before base64 decoding");
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
                using(var big=await Get(page))
                {
                    var payload=await big.Content.ReadAsByteArrayAsync();
                    using var problem=JsonDocument.Parse(payload);
                    Check(big.StatusCode==HttpStatusCode.ServiceUnavailable && payload.Length<4096 &&
                        problem.RootElement.GetProperty("title").GetString()=="Activity history read exceeds bounded budget" &&
                        !Encoding.UTF8.GetString(payload).Contains("FORGED",StringComparison.Ordinal),
                        "SQL byte budget returns distinct bounded 503 without exposing period contents");
                }
            }
            finally
            {
                await Exec("UPDATE activity.ClosedPeriod SET StartRawValue=@value WHERE EffectId=@effect",("@value",original),("@effect",effect));
                await Exec("ENABLE TRIGGER activity.TR_ClosedPeriodImmutable ON activity.ClosedPeriod");
            }
            // This ingestion fixture can contain only legacy publications. Do not
            // pretend that it qualifies a version-4 source-witness HTTP read.
            // Version-4 tamper acceptance requires a separately seeded v4 fixture.
            var witnessedOperations=Convert.ToInt32(await Scalar("""
                SELECT COUNT(DISTINCT w.OperationId)
                FROM activity.PublicationSourceWitness w
                JOIN activity.ProcessorOperation o ON o.OperationId=w.OperationId
                WHERE o.MachineId=@machine AND o.MappingId=@mapping
                """,("@machine",machine),("@mapping",mapping)));
            Console.WriteLine($"MEASURE: activity HTTP existing fixture witnessed operations={witnessedOperations}; v4 HTTP tamper gate remains separate");
            var broken=JsonNode.Parse(cp)!;broken["AcceptedSource"]!["OperationId"]=Guid.NewGuid();
            await SetCheckpoint(broken.ToJsonString());
            try { using var corrupt=await Get(list);Check(corrupt.StatusCode==HttpStatusCode.ServiceUnavailable,"checksum-consistent checkpoint forgery fails immutable publication ancestry"); }
            finally { await SetCheckpoint(cp); }

            using(var expiringLogin=await http.PostAsync("/api/security/v2/human-sessions",new StringContent("activity-http-expiring")))
            {
                Check(expiringLogin.IsSuccessStatusCode,"post-wait expiry fixture begins with a valid human session");
                using var expiringJson=JsonDocument.Parse(await expiringLogin.Content.ReadAsStringAsync());
                var expiringBearer=expiringJson.RootElement.GetProperty("bearer").GetString()!;
                var expires=expiringJson.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
                await using var holder=new SqlConnection(sql);await holder.OpenAsync();
                await using var transaction=(SqlTransaction)await holder.BeginTransactionAsync();
                await using(var gate=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51099,'Fixture gate unavailable.',1; SELECT @@SPID",holder,transaction))
                {
                    gate.Parameters.AddWithValue("@resource",SqlActivityProcessorStore.ProcessorLockResource(machine,mapping,"mtconnect:uuid:fixture-device"));
                    var holderId=Convert.ToInt32(await gate.ExecuteScalarAsync());
                    using var request=new HttpRequestMessage(HttpMethod.Get,page);
                    request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",expiringBearer);
                    var waiting=http.SendAsync(request);var waitObserved=false;
                    try
                    {
                        var waitDeadline=Stopwatch.StartNew();
                        while(waitDeadline.Elapsed<TimeSpan.FromSeconds(2))
                        {
                            if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id WHERE s.program_name=@app AND r.blocking_session_id=@holder AND r.wait_type LIKE 'LCK_M_%'",("@app",appName),("@holder",holderId)))>0){waitObserved=true;break;}
                            await Task.Delay(20);
                        }
                        Check(waitObserved&&!waiting.IsCompleted && (DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<expires,
                            "currently eligible HTTP read genuinely waits behind the processor gate");
                        while((DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!<=expires)await Task.Delay(20);
                    }
                    finally { await transaction.RollbackAsync(); }
                    using var expired=await waiting;
                    Check(expired.StatusCode==HttpStatusCode.Unauthorized,"session expiring during processor wait cannot release historical data");
                }
            }

            // Observe transaction-owned application locks on the HTTP connection from a separate
            // SQL session. Identify the exact resources, not an aggregate count of unrelated locks.
            {
                var processorResource=SqlActivityProcessorStore.ProcessorLockResource(machine,mapping,"mtconnect:uuid:fixture-device");
                var machineResource=FactoryConnect.Sql.Configuration.ConfigurationLockResources.Machine(installation,machine);
                // Learn SQL Server's own lock descriptions from fixture-owned sessions.
                async Task<string> FixtureDescription(string resource)
                {
                    await using var hold=await HoldExclusive(sql,resource);
                    await using var spid=new SqlCommand("SELECT @@SPID",hold.Connection,hold.Transaction);
                    var session=Convert.ToInt32(await spid.ExecuteScalarAsync());
                    await using var query=new SqlCommand("""
                        SELECT resource_description FROM sys.dm_tran_locks
                        WHERE request_session_id=@session AND resource_type='APPLICATION'
                          AND request_status='GRANT' AND request_mode='X'
                        """,c);
                    query.Parameters.AddWithValue("@session",session);
                    var descriptions=new List<string>();
                    await using(var reader=await query.ExecuteReaderAsync())
                        while(await reader.ReadAsync())descriptions.Add(reader.GetString(0));
                    Check(descriptions.Count==1,"fixture exclusive applock has one SQL resource description");
                    Console.WriteLine($"TRACE: fixture session={session} resource={descriptions[0].TrimEnd()}");
                    return descriptions[0].TrimEnd();
                }
                var machineDescription=await FixtureDescription(machineResource);
                var processorDescription=await FixtureDescription(processorResource);
                Check(machineDescription!=processorDescription,"machine and processor SQL applock descriptions are distinct");
                // Materialize one DMV lock snapshot and attribute rows only to an
                // actively executing LockInOrder batch, never to program_name alone.
                async Task<(int Session,string Command,string Mode,string Status,string Resource)[]> Observe()
                {
                    await using var query=new SqlCommand("""
                        SELECT request_session_id,request_mode,request_status,resource_description
                        INTO #activity_lock_snapshot FROM sys.dm_tran_locks WHERE resource_type='APPLICATION';
                        SELECT r.session_id,CONVERT(nvarchar(max),st.text) AS command_text,
                               COALESCE(l.request_mode,''),COALESCE(l.request_status,''),
                               COALESCE(l.resource_description,'')
                        FROM sys.dm_exec_requests r
                        JOIN sys.dm_exec_sessions sess ON sess.session_id=r.session_id
                        CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
                        LEFT JOIN #activity_lock_snapshot l ON l.request_session_id=r.session_id
                        WHERE sess.program_name=@app
                          AND CONVERT(nvarchar(max),st.text) LIKE '%sp_getapplock @Resource=@first%'
                        ORDER BY r.session_id,l.resource_description,l.request_status;
                        DROP TABLE #activity_lock_snapshot;
                        """,c);
                    query.Parameters.AddWithValue("@app",appName);
                    var rows=new List<(int Session,string Command,string Mode,string Status,string Resource)>();
                    await using(var reader=await query.ExecuteReaderAsync())
                        while(await reader.ReadAsync())
                        {
                            var command=reader.GetString(1);
                            rows.Add((reader.GetInt32(0),command.Length>60?command[..60]:command,
                                reader.GetString(2),reader.GetString(3),reader.GetString(4).TrimEnd()));
                        }
                    foreach(var row in rows)
                        Console.WriteLine($"TRACE: lock session={row.Session} command={row.Command} mode={row.Mode} status={row.Status} resource={row.Resource}");
                    return rows.ToArray();
                }
                async Task<(int Session,string Command,string Mode,string Status,string Resource)[]> WaitBlocked(
                    Task<HttpResponseMessage> request,string description,bool expectMachineGrant)
                {
                    var watch=Stopwatch.StartNew();
                    var stable=0;int? stableSession=null;
                    while(watch.Elapsed<TimeSpan.FromSeconds(5) && !request.IsCompleted)
                    {
                        var locks=await Observe();
                        var readers=locks.Select(x=>x.Session).Distinct().ToArray();
                        if(readers.Length>1)throw new InvalidOperationException("Activity HTTP: ambiguous active LockInOrder reader sessions");
                        if(readers.Length==1)
                        {
                            var session=readers[0];
                            var own=locks.Where(x=>x.Session==session).ToArray();
                            var waiting=own.Any(x=>x.Resource==description && x.Status=="WAIT");
                            var machineGranted=own.Any(x=>x.Resource==machineDescription && x.Status=="GRANT");
                            var processorGranted=own.Any(x=>x.Resource==processorDescription && x.Status=="GRANT");
                            var expected=waiting && !processorGranted &&
                                (expectMachineGrant?machineGranted:!machineGranted);
                            if(expected)
                            {
                                stable=stableSession==session?stable+1:1;
                                stableSession=session;
                                if(stable>=3)return own;
                            }
                            else
                            {
                                // Three consecutive contradictory observations of the same
                                // blocked reader constitute a real ordering violation.
                                if(waiting && (processorGranted || (expectMachineGrant?!machineGranted:machineGranted)))
                                {
                                    stable=stableSession==session?stable-1:-1;
                                    stableSession=session;
                                    if(stable<=-3)throw new InvalidOperationException(
                                        $"Activity HTTP: stable lock ordering violation in reader session {session}");
                                }
                                else {stable=0;stableSession=null;}
                            }
                        }
                        else {stable=0;stableSession=null;}
                        await Task.Delay(50);
                    }
                    throw new InvalidOperationException("Activity HTTP: attributed reader lock state not stable or not observable");
                }
                await using(var hold=await HoldExclusive(sql,processorResource))
                {
                    var pending=Get(page);
                    var locks=await WaitBlocked(pending,processorDescription,true);
                    Check(locks.Any(x=>x.Resource==processorDescription && x.Status=="WAIT") &&
                        locks.Any(x=>x.Resource==machineDescription && x.Status=="GRANT") &&
                        !locks.Any(x=>x.Resource==processorDescription && x.Status=="GRANT"),
                        "machine applock is acquired before blocked processor applock");
                    await hold.DisposeAsync();
                    using var response=await pending;
                    Check(response.StatusCode==HttpStatusCode.OK,"processor lock release resumes history read");
                }
                await using(var hold=await HoldExclusive(sql,machineResource))
                {
                    var pending=Get(page);
                    var locks=await WaitBlocked(pending,machineDescription,false);
                    Check(locks.Any(x=>x.Resource==machineDescription && x.Status=="WAIT") &&
                        !locks.Any(x=>x.Resource==processorDescription && x.Status=="GRANT"),
                        "blocked machine lock prevents processor applock acquisition");
                    await hold.DisposeAsync();
                    using var response=await pending;
                    Check(response.StatusCode==HttpStatusCode.OK,"machine lock release resumes history read");
                }
                await using(var hold=await HoldExclusive(sql,processorResource))
                {
                    var watch=Stopwatch.StartNew();
                    using var response=await Get(page);
                    watch.Stop();
                    Check(response.StatusCode==HttpStatusCode.ServiceUnavailable &&
                        watch.Elapsed>=TimeSpan.FromSeconds(9) && watch.Elapsed<TimeSpan.FromSeconds(25),
                        "processor contention preserves ten-second timeout and authority-unavailable response");
                    var locks=await Observe();
                    Check(!locks.Any(x=>x.Resource==machineDescription || x.Resource==processorDescription),"processor timeout releases machine and processor HTTP applocks");
                    await using var probe=await HoldExclusive(sql,machineResource);
                    Check(true,"machine applock is immediately available after processor timeout");
                }
                await using(var hold=await HoldExclusive(sql,machineResource))
                {
                    var watch=Stopwatch.StartNew();
                    using var response=await Get(page);
                    watch.Stop();
                    Check(response.StatusCode==HttpStatusCode.ServiceUnavailable &&
                        watch.Elapsed>=TimeSpan.FromSeconds(9) && watch.Elapsed<TimeSpan.FromSeconds(25),
                        "machine contention preserves ten-second timeout and authority-unavailable response");
                    var locks=await Observe();
                    Check(!locks.Any(x=>x.Resource==machineDescription || x.Resource==processorDescription),"machine timeout releases machine and processor HTTP applocks");
                    await using var probe=await HoldExclusive(sql,processorResource);
                    Check(true,"processor applock is available after machine timeout");
                }
                using var recovered=await Get(page);
                Check(recovered.StatusCode==HttpStatusCode.OK,"history read recovers after both lock timeouts");
            }

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
