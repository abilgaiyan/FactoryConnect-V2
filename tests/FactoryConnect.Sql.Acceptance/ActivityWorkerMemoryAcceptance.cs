using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

// A single long-lived worker with no ingestion, profiler, plan XML or result history.
internal static class ActivityWorkerMemoryAcceptance
{
    internal sealed record Configuration(string ConnectionString,Guid Installation,Guid Site,Guid Machine,Guid Mapping,string Stream);
    internal sealed record Command(Guid Operation,ulong Instance,ulong After,ulong Through,ulong Generation,bool SampleMemory);
    internal sealed record Memory(long WorkingSet,long ManagedLive,long HeapSize,long Fragmented,long Committed);
    internal sealed record Response(int ProcessId,ActivityDurableResult Result,double ElapsedMs,long AllocatedBytes,Memory? Memory);
    private sealed record Sample(int Cut,double ElapsedMs,long AllocatedBytes,Memory? Memory);

    internal static async Task<int> RunChildAsync()
    {
        try
        {
            var text=await Console.In.ReadLineAsync()??throw new InvalidDataException("Missing worker measurement configuration.");
            var config=JsonSerializer.Deserialize<Configuration>(text)??throw new InvalidDataException("Invalid worker configuration.");
            RequireDisposable(new SqlConnectionStringBuilder(config.ConnectionString).InitialCatalog);
            var store=new SqlActivityProcessorStore(config.ConnectionString);
            while(await Console.In.ReadLineAsync() is { } line)
            {
                if(line=="stop")return 0;
                var command=JsonSerializer.Deserialize<Command>(line)??throw new InvalidDataException("Invalid worker command.");
                var allocation=GC.GetTotalAllocatedBytes(precise:true);
                var timer=Stopwatch.StartNew();
                var result=await store.ApplyAcceptedAsync(command.Operation,config.Installation,config.Site,
                    config.Machine,config.Mapping,config.Stream,command.Instance,command.After,command.Through,command.Generation);
                timer.Stop();
                var allocated=GC.GetTotalAllocatedBytes(precise:true)-allocation;
                Memory? memory=null;
                if(command.SampleMemory)
                {
                    var live=GC.GetTotalMemory(forceFullCollection:true);
                    var info=GC.GetGCMemoryInfo();
                    memory=new(Environment.WorkingSet,live,info.HeapSizeBytes,info.FragmentedBytes,info.TotalCommittedBytes);
                }
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Response(
                    Environment.ProcessId,result,timer.Elapsed.TotalMilliseconds,allocated,memory)));
                await Console.Out.FlushAsync();
                // Only the current command/result remains live, never a historical list.
            }
            return 0;
        }
        catch(Exception error)
        {
            // Do not echo stdin, SQL credentials, source payloads or command strings.
            await Console.Error.WriteLineAsync("Activity memory worker failed: "+error.GetType().Name);
            return 20;
        }
    }

    internal static async Task<int> RunAsync(InstallationTarget target,Guid site,ActivityQualificationIdentity identity,
        Func<Task<ActivityHistoryMeasurementAcceptance.Input>> ingest)
    {
        RequireDisposable(target.Database);
        var checks=0;
        var store=new SqlActivityProcessorStore(target.ConnectionString);
        var checkpoint=await store.ReadCheckpointAsync(identity);
        // Finish the history fixture's pending negative-test cut before measuring.
        await using(var c=new SqlConnection(target.ConnectionString))
        {
            await c.OpenAsync();
            await using var q=new SqlCommand("SELECT InstanceId,NextSequence FROM acquisition.MtConnectCursor WHERE MachineId=@machine AND StreamKeyBytes=@stream",c);
            q.Parameters.AddWithValue("@machine",identity.MachineId);
            q.Parameters.AddWithValue("@stream",System.Text.Encoding.UTF8.GetBytes(identity.StreamKey));
            await using var r=await q.ExecuteReaderAsync();
            if(!await r.ReadAsync())throw new InvalidDataException("Worker fixture source cursor missing.");
            var instance=decimal.ToUInt64(r.GetDecimal(0));
            var through=decimal.ToUInt64(r.GetDecimal(1))-1;
            if(through>checkpoint.AcceptedSource!.ThroughSequence)
                checkpoint=(await store.ApplyAcceptedAsync(Guid.NewGuid(),target.InstallationId,site,identity.MachineId,
                    identity.MappingId,identity.StreamKey,instance,checkpoint.AcceptedSource.ThroughSequence,through,
                    checkpoint.NextGeneration)).Checkpoint;
        }
        var connection=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName="fc2-activity-memory-"+Guid.NewGuid().ToString("N")}.ConnectionString;
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet") {
            UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--activity-memory-child");
        using var process=Process.Start(start)??throw new InvalidOperationException("Memory worker did not start.");
        var stderr=process.StandardError.ReadToEndAsync();
        var samples=new List<Sample>();
        var firstGeneration=checkpoint.NextGeneration;
        var initialCounts=await Counts();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Configuration(connection,target.InstallationId,
                site,identity.MachineId,identity.MappingId,identity.StreamKey)));
            await process.StandardInput.FlushAsync();
            for(var cut=1;cut<=640;cut++)
            {
                var input=await ingest();
                var rows=input.Rows;
                if(input.RawObservationCount!=100 || rows.Count!=2)
                    throw new InvalidDataException("Memory worker requires an ingested bounded two-state cut.");
                var command=new Command(Guid.NewGuid(),rows[0].InstanceId,checkpoint.AcceptedSource!.ThroughSequence,
                    input.Through,checkpoint.NextGeneration,cut%128==0);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
                await process.StandardInput.FlushAsync();
                var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var result=line is null ? null : JsonSerializer.Deserialize<Response>(line);
                if(result is null || result.ProcessId!=process.Id || result.Result.Replayed ||
                    result.Result.NewPeriods.Count!=1 || result.Result.Checkpoint.NextGeneration!=checkpoint.NextGeneration+1 ||
                    result.Result.Checkpoint.Qualification!=identity ||
                    result.Result.Checkpoint.AcceptedSource?.OperationId!=command.Operation ||
                    result.Result.Checkpoint.AcceptedSource.ThroughSequence!=input.Through ||
                    result.Result.Checkpoint.AcceptedSource.LastObservation!=rows[1])
                    throw new InvalidDataException("Memory worker changed exact cut/result/provenance semantics.");
                var expected=new ClosedActivityPeriod(identity.MachineId,identity.StreamKey,identity.MappingId,
                    ActivityState.Running,rows[0].ObservedAt,rows[1].ObservedAt,rows[1].ObservedAt.UtcTicks-rows[0].ObservedAt.UtcTicks,
                    rows[0].InstanceId,rows[0].Sequence,rows[1].Sequence,rows[0].RawValue,rows[1].RawValue);
                if(result.Result.NewPeriods[0]!=expected)
                    throw new InvalidDataException("Memory worker period differs from exact accepted-source oracle.");
                checkpoint=result.Result.Checkpoint;
                // Warm 128 cuts, then retain only numerical samples in the parent.
                if(cut>=128)samples.Add(new(cut,result.ElapsedMs,result.AllocatedBytes,result.Memory));
                if(result.Memory is not null)
                    Console.WriteLine($"MEASURE: isolated activity worker cut={cut} processId={process.Id} "+
                        $"managedLive={result.Memory.ManagedLive} workingSet={result.Memory.WorkingSet}");
            }
            await process.StandardInput.WriteLineAsync("stop");await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.ExitCode==0 && (await stderr).Length==0,"fresh long-lived worker completes 640 serial cuts without fixture or profiler retention");
            var finalCounts=await Counts();
            Check(checkpoint.NextGeneration==firstGeneration+640 && await store.ReadCheckpointAsync(identity)==checkpoint &&
                finalCounts.Periods==initialCounts.Periods+640 && finalCounts.Receipts==initialCounts.Receipts+640,
                "all isolated-worker cuts preserve exact durable periods receipts checkpoint progress and provenance");
            var memory=samples.Where(x=>x.Memory is not null).ToArray();
            Check(memory.Length==5 && memory.All(x=>x.Memory!.WorkingSet>0 && x.Memory.ManagedLive>0),
                "five live-heap and working-set points cover warmup and 512 steady cuts");
            var liveSpread=memory.Max(x=>x.Memory!.ManagedLive)-memory.Min(x=>x.Memory!.ManagedLive);
            Directory.CreateDirectory("artifacts/performance");
            await File.WriteAllTextAsync("artifacts/performance/activity-isolated-worker-memory.json",JsonSerializer.Serialize(new {
                schemaVersion=1,fixture=true,commit=Environment.GetEnvironmentVariable("GITHUB_SHA"),checks=checks+(liveSpread<=8L*1024*1024?1:0),
                processId=process.Id,warmupCuts=128,steadyCuts=512,observationsPerCut=100,
                finalGeneration=checkpoint.NextGeneration,liveSpread,samples,
                limits=new[]{"Fresh worker has one fixed connection pool and no ingestion, XE, raw plan retention or historical result list.",
                    "Managed snapshots force collection outside cut timing at 128-cut boundaries; latency excludes stdout/GC and is not a production SLO.",
                    "Live-heap spread bound uses one existing 8-MiB source budget for this controlled workload; fleet/native lifetime limits are not implied.",
                    "Working set includes CLR/JIT/SqlClient/native pools; its observed plateau must be reviewed separately from managed retention."}
            },new JsonSerializerOptions{WriteIndented=true}));
            Check(liveSpread<=8L*1024*1024,
                "post-warm live managed spread stays within one existing 8-MiB source budget in this controlled workload");
            return checks;
        }
        finally
        {
            if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
            await stderr.WaitAsync(TimeSpan.FromSeconds(5));
        }
        async Task<(long Periods,long Receipts)> Counts()
        {
            await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
            await using var q=new SqlCommand("""
                SELECT (SELECT COUNT_BIG(*) FROM activity.ClosedPeriod WHERE MachineId=@machine AND MappingId=@mapping),
                    (SELECT COUNT_BIG(*) FROM activity.ProcessorOperation WHERE MachineId=@machine AND MappingId=@mapping)
                """,c);
            q.Parameters.AddWithValue("@machine",identity.MachineId);q.Parameters.AddWithValue("@mapping",identity.MappingId);
            await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();return(r.GetInt64(0),r.GetInt64(1));
        }
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("Isolated activity memory: "+label);checks++;Console.WriteLine("PASS: isolated activity memory "+label);}
    }
    private static void RequireDisposable(string database)
    {
        const string prefix="FactoryConnect_V2_Test_";
        if(!database.StartsWith(prefix,StringComparison.Ordinal) || !Guid.TryParseExact(database[prefix.Length..],"N",out _))
            throw new InvalidDataException("Activity memory qualification requires a generated disposable database.");
    }
}
