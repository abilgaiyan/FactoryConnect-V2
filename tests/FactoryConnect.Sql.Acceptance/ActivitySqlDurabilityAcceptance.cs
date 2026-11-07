using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

internal static class ActivitySqlDurabilityAcceptance
{
    internal static async Task<int> Run(InstallationTarget target)
    {
        var passed=0;
        void Check(bool ok,string label)
        {
            if(!ok)throw new InvalidOperationException("Activity SQL durability: "+label);
            passed++;
            Console.WriteLine("PASS: activity SQL "+label);
        }
        async Task Reject(Func<Task> action,string label)
        {
            try{await action();}
            catch(Exception ex) when(ex is InvalidOperationException or InvalidDataException or SqlException)
            {
                Check(true,label);
                return;
            }
            throw new InvalidOperationException("Expected activity SQL rejection: "+label);
        }
        var machine=Guid.NewGuid();
        var site=Guid.NewGuid();
        var mapping=Guid.NewGuid();
        const string stream="mtconnect:uuid:activity-sql-fixture";
        var qualified=new QualifiedActivitySignal(machine,stream,mapping,"execution",
            new Dictionary<string,ActivityState>(StringComparer.Ordinal)
            {["ACTIVE"]=ActivityState.Running,["READY"]=ActivityState.Idle,
             ["STOPPED"]=ActivityState.Stopped});
        var identity=ActivityQualificationIdentity.From(qualified,"independent-continuity-fixture-v1");
        var time=new DateTimeOffset(2026,11,7,10,0,0,TimeSpan.Zero);
        ActivityInputSample Sample(ulong seq,int seconds,string value,bool continuous=true)
            => new(machine,stream,mapping,"execution",7,seq,time.AddSeconds(seconds),
                value,"source-reported",continuous);
        await using(var c=new SqlConnection(target.ConnectionString))
        {
            await c.OpenAsync();
            await using var q=new SqlCommand("""
                INSERT [master].Site(SiteId,DisplayName) VALUES(@site,N'Activity SQL fixture');
                INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(@machine,@site,N'Activity machine');
                """,c);
            q.Parameters.AddWithValue("@site",site);
            q.Parameters.AddWithValue("@machine",machine);
            await q.ExecuteNonQueryAsync();
        }
        var store=new SqlActivityProcessorStore(target.ConnectionString);
        var initial=await store.ReadCheckpointAsync(identity);
        Check(initial.NextGeneration==0&&initial.OpenAnchor is null,
            "missing checkpoint is an explicit initial state");

        var firstOp=Guid.NewGuid();
        var firstSamples=new[]{Sample(100,0,"ACTIVE"),Sample(101,10,"ACTIVE")};
        var first=await store.ApplyAsync(firstOp,qualified,identity,0,firstSamples);
        Check(first.Checkpoint.NextGeneration==1&&first.Checkpoint.OpenAnchor==firstSamples[0]&&
              first.Checkpoint.LastConsumedSample==firstSamples[1]&&first.NewPeriods.Count==0,
              "first atomic commit retains original anchor and latest source sample");
        var restored=new SqlActivityProcessorStore(target.ConnectionString);
        Check(await restored.ReadCheckpointAsync(identity)==first.Checkpoint,
            "reconstructed SQL client recovers entire open activity checkpoint");

        var replay=await restored.ApplyAsync(firstOp,qualified,identity,0,firstSamples);
        Check(replay.Replayed&&replay.Checkpoint==first.Checkpoint&&replay.NewPeriods.Count==0,
            "identical operation retry returns durable receipt without checkpoint advancement");
        await Reject(async()=>{await store.ApplyAsync(firstOp,qualified,identity,0,
            [Sample(100,0,"READY")]);},"operation ID cannot bind different input");

        var secondSamples=new[]{Sample(102,20,"READY"),Sample(103,30,"STOPPED")};
        var secondOp=Guid.NewGuid();
        await Reject(async()=>{await restored.ApplyAsync(secondOp,qualified,identity,1,
            secondSamples,injectRollback:true);},"fault after period inserts rolls back entire transaction");
        Check((await store.ReadCheckpointAsync(identity)).NextGeneration==1&&
            (await store.ReadPeriodsAsync(identity)).Count==0,
            "failed commit leaves no checkpoint advancement or closed periods");
        var afterFailure=await restored.ApplyAsync(secondOp,qualified,identity,1,secondSamples);
        Check(afterFailure.Checkpoint.NextGeneration==2&&afterFailure.NewPeriods.Count==2,
            "retry after rollback can commit both closed intervals");
        var stored=await store.ReadPeriodsAsync(identity);
        Check(stored.Count==2&&stored.SequenceEqual(afterFailure.NewPeriods),
            "immutable SQL periods equal deterministic evaluator output");
        Check((await restored.ApplyAsync(secondOp,qualified,identity,1,secondSamples)).Replayed&&
            (await store.ReadPeriodsAsync(identity)).Count==2,
            "retry of closed-period operation never duplicates intervals");
        await Reject(async()=>{await store.ApplyAsync(Guid.NewGuid(),qualified,identity,1,
            [Sample(104,40,"ACTIVE")]);},"stale processor generation is rejected");

        var racing=new[]{new SqlActivityProcessorStore(target.ConnectionString),
                         new SqlActivityProcessorStore(target.ConnectionString)};
        var contenders=await Task.WhenAll(racing.Select(async (client,i)=>{
            try
            {
                var written=await client.ApplyAsync(Guid.NewGuid(),qualified,identity,2,
                    [Sample(104,40,i==0?"ACTIVE":"READY")]);
                return (Success:true,Result:written,Error:(Exception?)null);
            }
            catch(Exception error) when(error is InvalidOperationException or SqlException)
            {
                return (Success:false,Result:(ActivityDurableResult?)null,Error:error);
            }
        }));
        Check(contenders.Count(x=>x.Success)==1&&contenders.Count(x=>!x.Success)==1,
            "simultaneous writers with same expected generation commit exactly once");
        Check((await store.ReadCheckpointAsync(identity)).NextGeneration==3,
            "concurrent losing writer cannot overwrite committed generation");
        await Reject(async()=>{await restored.ApplyAsync(Guid.NewGuid(),qualified,
            identity with {ContinuityIdentity="changed"},3,[Sample(105,50,"ACTIVE")]);},
            "different continuity qualification cannot advance existing processor");

        // SQL migration ensures independent immutable evidence effects.
        await Reject(async()=>{
            await using var c=new SqlConnection(target.ConnectionString);
            await c.OpenAsync();
            await using var q=new SqlCommand("DELETE FROM activity.ClosedPeriod WHERE MachineId=@machine",c);
            q.Parameters.AddWithValue("@machine",machine);
            await q.ExecuteNonQueryAsync();
        },"SQL trigger prevents modifying committed activity history");

        var finalPeriods=await restored.ReadPeriodsAsync(identity);
        var winningEffects=contenders.Single(x=>x.Success).Result!.NewPeriods;
        Check(finalPeriods.Count==stored.Count+winningEffects.Count &&
            finalPeriods.Take(stored.Count).SequenceEqual(stored) &&
            finalPeriods.Skip(stored.Count).SequenceEqual(winningEffects),
            "reconstructed store reads the original immutable periods plus the winning concurrent effect");
        var pageOne=await restored.ReadPeriodPageAsync(identity,take:1);
        var expectedPersistedHash=Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
        Check(pageOne.Rows.Count==1 && pageOne.Next is not null &&
            pageOne.Rows[0].Qualification==identity &&
            pageOne.Rows[0].PersistedQualificationSha256==expectedPersistedHash,
            "first keyset page preserves actual persisted qualification hash and continuation");
        var pageTwo=await restored.ReadPeriodPageAsync(identity,take:1,after:pageOne.Next);
        Check(pageTwo.Rows.Count==1 && pageTwo.Rows[0].Qualification==identity &&
            pageTwo.Rows[0].PersistedQualificationSha256==expectedPersistedHash &&
            pageTwo.Rows[0].Period==finalPeriods[1],
            "second keyset page resumes with stored qualification, without repeating effects");
        Check((pageTwo.Next is null)==(finalPeriods.Count==2),
            "keyset page signals exhaustion exactly at the final committed effect");
        if(pageTwo.Next is not null)
        {
            var pageThree=await restored.ReadPeriodPageAsync(identity,take:1,after:pageTwo.Next);
            Check(pageThree.Rows.Count==1 && pageThree.Next is null &&
                pageThree.Rows[0].Period==finalPeriods[2] &&
                pageThree.Rows[0].Qualification==identity,
                "third keyset page returns the winning concurrent effect without duplicates");
        }
        await Reject(async()=>{await restored.ReadPeriodPageAsync(
            identity with {ContinuityIdentity="unqualified-change"});},
            "period read rejects different continuity identity");
        await Reject(async()=>{await restored.ReadPeriodPageAsync(
            identity with {ExactValueMapSha256=new string('A',64)});},
            "period read rejects different value mapping digest");
        await Reject(async()=>{await restored.ReadPeriodsAsync(
            identity with {ContinuityIdentity="changed"});},
            "compatibility period read cannot bypass qualification validation");
        try
        {
            await restored.ReadPeriodPageAsync(identity,take:101);
            throw new InvalidOperationException("Over-sized activity period page unexpectedly accepted.");
        }
        catch(ArgumentOutOfRangeException)
        {
            Check(true,"period page size is capped at 100 rows");
        }
        // Larger transition-density qualification of the current per-period
        // INSERT loop. This is disposable SQL, not a production load claim.
        var loadMachine=Guid.NewGuid();
        await using(var c=new SqlConnection(target.ConnectionString))
        {
            await c.OpenAsync();
            await using var q=new SqlCommand("""
                INSERT [master].Machine(MachineId,SiteId,DisplayName)
                VALUES(@machine,@site,N'Activity high-transition fixture')
                """,c);
            q.Parameters.AddWithValue("@machine",loadMachine);
            q.Parameters.AddWithValue("@site",site);
            await q.ExecuteNonQueryAsync();
        }
        var loadMapping=Guid.NewGuid();
        var loadSignal=new QualifiedActivitySignal(loadMachine,stream,loadMapping,"execution",
            new Dictionary<string,ActivityState>(StringComparer.Ordinal)
            {["ACTIVE"]=ActivityState.Running,["READY"]=ActivityState.Idle});
        var loadIdentity=ActivityQualificationIdentity.From(loadSignal,"qualified-load-v1");
        var loadSamples=Enumerable.Range(0,121).Select(i=>new ActivityInputSample(
            loadMachine,stream,loadMapping,"execution",7,(ulong)(1000+i),
            time.AddSeconds(i),i%2==0?"ACTIVE":"READY","source-reported",true)).ToArray();
        var activities=new List<Activity>();
        using(var listener=new ActivityListener
        {
            ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Activity",
            Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped=a=>{lock(activities)activities.Add(a);}
        })
        {
            ActivitySource.AddActivityListener(listener);
            var clock=Stopwatch.StartNew();
            var inserted=await new SqlActivityProcessorStore(target.ConnectionString).ApplyAsync(
                Guid.NewGuid(),loadSignal,loadIdentity,0,loadSamples);
            clock.Stop();
            Check(inserted.NewPeriods.Count==120 &&
                inserted.Checkpoint.NextGeneration==1,
                "dense transition fixture commits exactly 120 immutable activity periods");
            Activity[] observations;
            lock(activities)observations=activities
                .Where(a=>a.OperationName=="activity.sql.apply").ToArray();
            Check(observations.Length==1 &&
                Convert.ToInt32(observations[0].GetTagItem("activity.sql.period.insert.commands"))==120,
                "per-period SQL INSERT commands measured explicitly for 120 transitions");
            var roundtrips=Convert.ToInt64(observations.Single().GetTagItem("activity.sql.roundtrips"));
            var insertMs=Convert.ToDouble(observations.Single().GetTagItem("activity.sql.period.insert.elapsed.ms"));
            Check(roundtrips>=120&&insertMs>=0,
                "large-transition SQL fixture captures the per-row roundtrip cost");
            Console.WriteLine($"MEASURE: activity transitions=120 periodInsertCommands=120 " +
                $"sqlRoundtrips={roundtrips} insertElapsedMs={insertMs:F2} applyElapsedMs={clock.Elapsed.TotalMilliseconds:F2}");
        }
        var loadStore=new SqlActivityProcessorStore(target.ConnectionString);
        var loadPage1=await loadStore.ReadPeriodPageAsync(loadIdentity,take:100);
        var loadPage2=await loadStore.ReadPeriodPageAsync(loadIdentity,take:100,after:loadPage1.Next);
        Check(loadPage1.Rows.Count==100&&loadPage1.Next is not null &&
            loadPage2.Rows.Count==20&&loadPage2.Next is null &&
            loadPage1.Rows.All(x=>x.Qualification==loadIdentity) &&
            loadPage2.Rows.All(x=>x.Qualification==loadIdentity),
            "120 committed periods read via bounded 100+20 keyset pages");
        await Reject(async()=>{await loadStore.ReadPeriodsAsync(loadIdentity);},
            "compatibility period read refuses unbounded 120-row history");
        return passed;
    }
}
