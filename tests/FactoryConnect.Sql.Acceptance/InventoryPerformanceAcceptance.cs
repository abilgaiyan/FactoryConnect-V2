using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Inventory;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class InventoryPerformanceAcceptance
{
    private sealed record Trace(string Operation,string? Outcome,int Rows,int DataCommands,long Roundtrips,
        double TotalMs,double OpenMs,double GateMs,double AuthorizationMs,double TransactionMs,int OpenAttempts);
    private sealed record QueryCounters(long LogicalReads,long Executions,long WorkerMicroseconds);
    private sealed record SessionCounters(int Sessions);
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;const int samples=128;const int poolSize=8;
        var application="fc2-inventory-performance";
        var pooled=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=application,MaxPoolSize=poolSize,MinPoolSize=poolSize}.ConnectionString;
        await using var control=new SqlConnection(target.ConnectionString);await control.OpenAsync();
        var site=Guid.NewGuid();var smallSite=Guid.NewGuid();var actor=Guid.NewGuid();var token=RandomNumberGenerator.GetBytes(32);
        await Execute("""
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Performance fixture'),(@small,N'Single-machine fixture');
            ;WITH n AS (SELECT 1 AS i UNION ALL SELECT i+1 FROM n WHERE i<100)
            INSERT [master].Machine(MachineId,SiteId,DisplayName,IsRetired)
                SELECT NEWID(),@site,CONCAT(N'Synthetic ',i),CONVERT(bit,CASE WHEN i%10=0 THEN 1 ELSE 0 END) FROM n OPTION(MAXRECURSION 100);
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(NEWID(),@small,N'Synthetic single');
            INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@actor,'H',0x70657266,0x61646D696E);
            INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'performance-fixture',0);
            INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                VALUES(NEWID(),@actor,@actor,'bootstrap','configuration.read','I',1,0,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()));
            UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1;
            """,("@site",site),("@small",smallSite),("@actor",actor),("@token",token));
        // Large-fleet machine discovery uses an independently authorized site.
        // Exercise exact page boundaries and 1,200 distinct machine identities.
        var discoverySite=Guid.NewGuid();
        await Execute("""
            INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Paginated discovery fixture');
            ;WITH n AS (SELECT 1 AS i UNION ALL SELECT i+1 FROM n WHERE i<1200)
            INSERT [master].Machine(MachineId,SiteId,DisplayName,IsRetired)
                SELECT NEWID(),@site,CONCAT(N'Authorized machine ',i),CONVERT(bit,0)
                FROM n OPTION(MAXRECURSION 0);
            INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,SiteId,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                VALUES(NEWID(),@actor,@actor,'bootstrap','commissioning.read','S',@site,1,0,
                    DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()));
            """,("@site",discoverySite),("@actor",actor));
        var discovery=new SqlSecurityAuthority(pooled,target.InstallationId);
        var discovered=new HashSet<Guid>();
        Guid? afterMachine=null;
        var pageSizes=new List<int>();
        var discoveryWatch=Stopwatch.StartNew();
        while (true)
        {
            var (machines,hasMore)=await discovery.ReadOperatorEvidenceMachinePageAsync(token,discoverySite,afterMachine);
            pageSizes.Add(machines.Count);
            Check(machines.Count<=100&&machines.All(m=>m.SiteId==discoverySite)&&
                machines.All(m=>discovered.Add(m.MachineId)),
                "large-fleet discovery returns distinct authorized machines in bounded pages");
            if (!hasMore)
            {
                Check(machines.Count>0||discovered.Count==0,
                    "terminal discovery page preserves the final authorized machines");
                break;
            }
            Check(machines.Count==100,"nonterminal machine page is full");
            afterMachine=machines[^1].MachineId;
        }
        discoveryWatch.Stop();
        Check(discovered.Count==1200&&pageSizes.Count==12&&pageSizes.All(size=>size==100),
            "1,200 authorized machines require exactly 12 complete keyset pages");
        Console.WriteLine($"MEASURE: authorized discovery machines=1200 pages={pageSizes.Count} elapsedMs={discoveryWatch.Elapsed.TotalMilliseconds:F2}");
        // Exactly 100 must be complete; 101 must advertise one further page.
        // These fixtures exercise the SQL lookahead, not just client metadata.
        var exactlyHundredSite=Guid.NewGuid();
        var hundredOneSite=Guid.NewGuid();
        await Execute("""
            INSERT [master].Site(SiteId,DisplayName)
                VALUES(@hundred,N'100-machine discovery boundary'),(@hundredOne,N'101-machine discovery boundary');
            ;WITH n AS (SELECT 1 AS i UNION ALL SELECT i+1 FROM n WHERE i<101)
            INSERT [master].Machine(MachineId,SiteId,DisplayName,IsRetired)
                SELECT NEWID(),@hundred,CONCAT(N'Boundary 100-',i),0
                FROM n WHERE i<=100 OPTION(MAXRECURSION 101);
            ;WITH n AS (SELECT 1 AS i UNION ALL SELECT i+1 FROM n WHERE i<101)
            INSERT [master].Machine(MachineId,SiteId,DisplayName,IsRetired)
                SELECT NEWID(),@hundredOne,CONCAT(N'Boundary 101-',i),0
                FROM n OPTION(MAXRECURSION 101);
            INSERT security.PermissionGrant
                (GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,SiteId,
                 UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                SELECT NEWID(),@actor,@actor,'bootstrap','commissioning.read','S',SiteId,
                    1,0,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME())
                FROM (VALUES(@hundred),(@hundredOne)) AS boundaries(SiteId);
            """,("@hundred",exactlyHundredSite),("@hundredOne",hundredOneSite),("@actor",actor));
        var (exactlyHundred,hasHundredMore)=await discovery.ReadOperatorEvidenceMachinePageAsync(token,exactlyHundredSite);
        Check(exactlyHundred.Count==100&&!hasHundredMore,
            "exactly 100 authorized machines are a complete first page");
        var (hundredOneFirst,hasHundredOneMore)=await discovery.ReadOperatorEvidenceMachinePageAsync(token,hundredOneSite);
        Check(hundredOneFirst.Count==100&&hasHundredOneMore,
            "101 authorized machines expose a nonterminal first page");
        var (hundredOneLast,hasHundredOneLastMore)=await discovery.ReadOperatorEvidenceMachinePageAsync(
            token,hundredOneSite,hundredOneFirst[^1].MachineId);
        Check(hundredOneLast.Count==1&&!hasHundredOneLastMore&&
            !hundredOneFirst.Any(m=>m.MachineId==hundredOneLast[0].MachineId),
            "101st authorized machine is returned once on a complete second page");
        var traces=new ConcurrentQueue<Trace>();
        using var listener=new ActivityListener
        {
            ShouldListenTo=s=>s.Name is "FactoryConnect.V2.Inventory" or "FactoryConnect.V2.Security",
            Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllData,
            ActivityStopped=a=>
            {
                double Number(string tag)=>Convert.ToDouble(a.GetTagItem(tag)??0);
                traces.Enqueue(new(a.OperationName,(string?)a.GetTagItem(a.OperationName=="security.transaction"?"security.result":"inventory.result"),
                    (int)Number("inventory.rows"),(int)Number("inventory.reader.commands"),(long)Number("security.sql.roundtrips"),
                    Number("security.elapsed.ms"),Number("security.connection.open.ms"),Number("security.gate.wait.ms"),Number("security.authorization.elapsed.ms"),
                    Number("security.transaction.elapsed.ms"),(int)Number("security.connection.open.attempts")));
                CheckSafeTags(a);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var reader=new SqlProtectedMachineInventoryReader(pooled,target.InstallationId);
        // Hold all leases simultaneously to warm an explicitly bounded pool; this is not cold-SQL evidence.
        var warm=await Task.WhenAll(Enumerable.Range(0,poolSize).Select(async _=>{var c=new SqlConnection(pooled);await c.OpenAsync();return c;}));
        foreach(var connection in warm)await connection.DisposeAsync();
        await reader.ReadSiteAsync(token,smallSite);
        var small=traces.Single(t=>t.Operation=="security.transaction");traces.Clear();
        var hundred=await reader.ReadSiteAsync(token,site);
        var large=traces.Single(t=>t.Operation=="security.transaction");
        Check(hundred.Count==100&&hundred.Count(m=>m.IsRetired)==10,"performance fixture preserves all 100 identities and retirement truth");
        Check(small.Roundtrips>0&&large.Roundtrips==small.Roundtrips&&large.Roundtrips<=12,"full protected SQL roundtrips are bounded and independent of machine count");
        Check(large.OpenAttempts==1,"protected inventory uses one connection lease per request");
        var results=new List<object>();
        foreach(var concurrency in new[]{1,8})
        {
            traces.Clear();var queryBefore=await DataQueryCounters();
            var allocated=GC.GetTotalAllocatedBytes();using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;
            var times=new ConcurrentBag<double>();var started=Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(Enumerable.Range(0,samples),new ParallelOptions{MaxDegreeOfParallelism=concurrency},async(_,ct)=>
            {
                var begin=Stopwatch.GetTimestamp();var items=await reader.ReadSiteAsync(token,site,ct);
                if(items.Count!=100||items.Any(m=>m.SiteId!=site))throw new InvalidOperationException("Performance run changed inventory semantics.");
                times.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            });
            var wall=Stopwatch.GetElapsedTime(started).TotalMilliseconds;process.Refresh();
            var after=await Counters();var queryAfter=await DataQueryCounters();var transactions=traces.Where(t=>t.Operation=="security.transaction").ToArray();
            var inventories=traces.Where(t=>t.Operation=="inventory.read-site").ToArray();var latencies=times.Order().ToArray();
            var observedExecutions=queryAfter.Executions-queryBefore.Executions;
            var logicalReads=observedExecutions==samples&&queryAfter.LogicalReads>=queryBefore.LogicalReads?(long?)(queryAfter.LogicalReads-queryBefore.LogicalReads):null;
            results.Add(new{concurrency,samples,warm=true,machines=100,wallMs=wall,
                latencyMs=new{p50=Percentile(latencies,0.5),p95=Percentile(latencies,0.95),p99=Percentile(latencies,0.99),max=latencies[^1]},
                connectionOpenP95Ms=Percentile(transactions.Select(t=>t.OpenMs).Order().ToArray(),0.95),
                gateAcquisitionP95Ms=Percentile(transactions.Select(t=>t.GateMs).Order().ToArray(),0.95),
                authorizationP95Ms=Percentile(transactions.Select(t=>t.AuthorizationMs).Order().ToArray(),0.95),
                transactionP95Ms=Percentile(transactions.Select(t=>t.TransactionMs).Order().ToArray(),0.95),
                maxSqlRoundtrips=transactions.Max(t=>t.Roundtrips),inventoryDataLogicalReadsDelta=logicalReads,inventoryDataExecutionsDelta=observedExecutions,
                inventoryDataLogicalReadsBefore=queryBefore.LogicalReads,inventoryDataLogicalReadsAfter=queryAfter.LogicalReads,
                inventoryDataWorkerCpuMsDelta=observedExecutions==samples?(double?)(queryAfter.WorkerMicroseconds-queryBefore.WorkerMicroseconds)/1000:null,
                pooledSqlSessions=after.Sessions,clientAllocatedBytes=GC.GetTotalAllocatedBytes()-allocated,
                clientCpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds,clientWorkingSetBytes=process.WorkingSet64});
            Console.WriteLine($"MEASURE: SQL inventory machines=100 concurrency={concurrency} samples={samples} p95Ms={Percentile(latencies,0.95):F2} p99Ms={Percentile(latencies,0.99):F2} maxRoundtrips={transactions.Max(t=>t.Roundtrips)} logicalReadsDelta={logicalReads?.ToString()??"unavailable"}");
            await SaveReport(false);
            Check(observedExecutions==samples&&logicalReads>=samples,"inventory logical-read measurement covers exactly 128 data-query executions at concurrency "+concurrency);
            Check(transactions.Length==samples&&transactions.All(t=>t.Outcome=="completed"&&t.OpenAttempts==1&&t.Roundtrips is >0 and <=12),"sampled full-path command/connection budget holds at concurrency "+concurrency);
            Check(inventories.Length==samples&&inventories.All(t=>t.Outcome=="completed"&&t.Rows==100&&t.DataCommands==1),"one inventory data query per measured request at concurrency "+concurrency);
            // CI regression ceilings, not production SLAs. At least 100 samples per scenario for tail reporting.
            Check(Percentile(latencies,0.95)<2000&&Percentile(latencies,0.99)<5000,"warm SQL inventory p95/p99 retain dashboard-timeout headroom at concurrency "+concurrency);
            Check(after.Sessions<=poolSize,"SQL inventory pool remains bounded at concurrency "+concurrency);

        }
        traces.Clear();
        // Observe an actual security gate wait, then cancel the SQL command before releasing the holder.
        await using(var blocker=new SqlConnection(target.ConnectionString))
        {
            await blocker.OpenAsync();await using var tx=(SqlTransaction)await blocker.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@gate,@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture gate failed.',1;",blocker,tx))
            {q.Parameters.AddWithValue("@gate",SqlSecurityAuthority.GateResource(target.InstallationId));await q.ExecuteNonQueryAsync();}
            using var cancellation=new CancellationTokenSource();var waiting=reader.ReadSiteAsync(token,site,cancellation.Token);
            try
            {
                await WaitForGate();cancellation.Cancel();
                try{await waiting.WaitAsync(TimeSpan.FromSeconds(3));throw new InvalidOperationException("Expected cancellation.");}
                catch(Exception e)when(cancellation.IsCancellationRequested&&e is OperationCanceledException or SqlException){Check(true,"cancellation interrupts a real protected SQL gate wait");}
                Check(traces.Any(t=>t.Operation=="security.transaction"&&t.Outcome=="cancelled")&&traces.All(t=>t.Operation!="inventory.read-site"||t.DataCommands==0),"cancelled gate wait is observable and executes no inventory query");
            }
            finally{cancellation.Cancel();await tx.RollbackAsync();}
        }
        Check((await reader.ReadSiteAsync(token,site)).Count==100,"cancelled read releases its transaction and connection for subsequent reads");
        await SaveReport(true);
        return count;

        async Task SaveReport(bool complete)
        {
        var path=Environment.GetEnvironmentVariable("FC2_PERFORMANCE_REPORT")??"artifacts/performance/inventory.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var report=new{schemaVersion=1,complete,scope="synthetic protected SQL inventory, not HTTP/ingestion/reporting capacity",measuredAtUtc=DateTimeOffset.UtcNow,
            commit=Environment.GetEnvironmentVariable("GITHUB_SHA"),runtime=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
            processors=Environment.ProcessorCount,sqlEngine=Convert.ToString(await Scalar("SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')),' / ',CONVERT(nvarchar(128),SERVERPROPERTY('Edition')))")),
            fixture=new{sites=2,machines=101,measuredSiteMachines=100,retired=10,maxPoolSize=poolSize},
            budgets=new{maxFullPathRoundtrips=12,warmP95Ms=2000,warmP99Ms=5000},scenarios=results,
            limitations=new[]{"Warm pooled SQL reads with sampled instrumentation; no cold-SQL claim.","Inventory logical reads use isolated database query-cache totals and exactly 128 executions; null if unavailable/reset. Full authorization logical reads and query plans are not measured. Inventory-data worker CPU is from the same query-cache counters, not SQL process profiling.","Client CPU/allocations/working set include sampled tracing; not SQL process resource usage.","Ingestion, activation, reporting, HTTP and production 100-machine capacity remain unqualified."}};
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        }

        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task<QueryCounters> DataQueryCounters()
        {
            await using var q=new SqlCommand("""
                SELECT COALESCE(SUM(qs.total_logical_reads),0),COALESCE(SUM(qs.execution_count),0),COALESCE(SUM(qs.total_worker_time),0)
                FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                WHERE CHARINDEX(@needle,st.text)>0
                    AND EXISTS(SELECT 1 FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa
                        WHERE pa.attribute='dbid' AND CONVERT(int,pa.value)=DB_ID());
                """,control);
            q.Parameters.AddWithValue("@needle","FROM [master].Machine m");
            await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();return new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2));
        }
        async Task<SessionCounters> Counters()
        {await using var q=new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name=@app",control);q.Parameters.AddWithValue("@app",application);await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();return new(r.GetInt32(0));}
        async Task WaitForGate()
        {
            for(var i=0;i<150;i++)
            {
                if(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT'",("@app",application)))>0)return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("Actual protected SQL gate wait was not observed.");
        }
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,control);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Execute(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,control);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static double Percentile(double[] sorted,double quantile)=>sorted[Math.Clamp((int)Math.Ceiling(sorted.Length*quantile)-1,0,sorted.Length-1)];
    private static void CheckSafeTags(Activity activity)
    {
        var allowed=new HashSet<string>(StringComparer.Ordinal){"inventory.result","inventory.rows","inventory.reader.commands","inventory.elapsed.ms",
            "security.result","security.gate.mode","security.connection.open.ms","security.operation.lock.wait.ms","security.gate.wait.ms",
            "security.connection.open.attempts","security.elapsed.ms","security.transaction.elapsed.ms","security.sql.roundtrips","security.sql.bytes.received","security.authorization.elapsed.ms"};
        if(activity.TagObjects.Any(tag=>!allowed.Contains(tag.Key)))throw new InvalidOperationException("Unexpected security/inventory diagnostic tag.");
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {foreach(var(name,value)in values)if(value is byte[] b)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=b;else q.Parameters.AddWithValue(name,value);}
}
