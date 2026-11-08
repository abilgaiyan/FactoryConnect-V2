using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

internal static class ActivityHostAcceptance
{
    sealed record Child(Process Process,Channel<JsonElement> Events,Task Reader,Task<string> Errors,string App);
    sealed record Snapshot(string Checkpoint,long Periods,long Receipts);

    internal static async Task<ActivityDurableResult> Bootstrap(InstallationTarget target,ActivityWorkerScope scope,ActivityWorkCandidate candidate)
    {
        RequireDisposable(target.Database);
        var child=await Start(target,scope,2);
        try
        {
            var committed=await Until(child,"committed");
            if(committed.GetProperty("OperationId").GetGuid()!=candidate.OperationId ||
                committed.GetProperty("generation").GetString()!="1")throw new InvalidDataException("Fresh worker did not commit the deterministic initial cut.");
            await Until(child,"idle");await Stop(child);
            await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
            await using var q=new SqlCommand("SELECT ResultBytes FROM activity.ProcessorOperation WHERE OperationId=@op",c);
            q.Parameters.AddWithValue("@op",candidate.OperationId);
            return JsonSerializer.Deserialize<ActivityDurableResult>((byte[])(await q.ExecuteScalarAsync())!)!;
        }
        finally { await Dispose(child); }
    }

    internal static async Task<int> Run(InstallationTarget target,ActivityWorkerScope scope,ActivityQualificationIdentity identity,
        Func<int,bool,Task<IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow>>> ingest)
    {
        RequireDisposable(target.Database);var checks=0;var children=new List<Child>();
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var store=new SqlActivityProcessorStore(target.ConnectionString);
        try
        {
        var consumer=await Launch();await Until(consumer,"idle");
        var baseline=await SnapshotState();
        var before=await store.ReadCheckpointAsync(identity);
        var discovery=await store.DiscoverAcceptedWorkAsync(scope,1);
        Check(discovery.Candidate is null && discovery.Generation==before.NextGeneration,"empty poll resumes SQL frontier without creating a publication");
        await Until(consumer,"idle");
        Check(await SnapshotState()==baseline,"repeated idle poll leaves checkpoint periods and receipts unchanged");
        var barrier="fc2:activity-host-test:"+Guid.NewGuid().ToString("N");
        await Exec($"""
            CREATE TRIGGER activity.Fixture_ActivityHostBarrier ON activity.ClosedPeriod AFTER INSERT AS
            BEGIN
                DECLARE @r int;
                EXEC @r=sys.sp_getapplock @Resource=N'{barrier}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=30000;
                IF @r<0 THROW 51098,'Fixture barrier failed.',1;
            END
            """);
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> opening;
        try
        {
            await using var holder=new SqlConnection(target.ConnectionString);await holder.OpenAsync();
            await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();
            await using(var lockCommand=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51098,'Fixture lock failed.',1;",holder,tx))
            {lockCommand.Parameters.AddWithValue("@key",barrier);await lockCommand.ExecuteNonQueryAsync();}
            opening=await ingest(120,true);
            await Until(consumer,"committed");await Until(consumer,"committed");
            await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode='S'",("@app",consumer.App)))==1);
            Check(!consumer.Process.HasExited,"real host waits after period insert and before atomic commit");
            consumer.Process.Kill(true);await consumer.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await tx.RollbackAsync();
            await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name=@app",("@app",consumer.App)))==0);
        }
        finally { if(!consumer.Process.HasExited){consumer.Process.Kill(true);await consumer.Process.WaitForExitAsync();} await Exec("DROP TRIGGER IF EXISTS activity.Fixture_ActivityHostBarrier"); }
        var rolledBack=await SnapshotState();var open=await store.ReadCheckpointAsync(identity);
        Check(rolledBack.Periods==baseline.Periods && rolledBack.Receipts==baseline.Receipts+2 &&
            open.NextGeneration==before.NextGeneration+2 && open.OpenAnchor?.Sequence==opening[0].Sequence &&
            open.LastConsumedSample?.Sequence==opening[1].Sequence,
            "forced termination rolls back the closing cut while retaining the original same-batch anchor");
        var restarted=await Launch();await Until(restarted,"committed");await Until(restarted,"idle");
        var closed=await store.ReadCheckpointAsync(identity);var page=await store.ReadPeriodPageAsync(identity,100);
        var expected=new ClosedActivityPeriod(scope.MachineId,scope.StreamKey,scope.MappingId,ActivityState.Running,
            opening[0].ObservedAt,opening[2].ObservedAt,opening[2].ObservedAt.UtcTicks-opening[0].ObservedAt.UtcTicks,
            scope.InstanceId,opening[0].Sequence,opening[2].Sequence,"ACTIVE","READY");
        Check(closed.NextGeneration==open.NextGeneration+1 && page.Rows.Count(x=>x.Period==expected)==1,
            "new host process discovers and retries the rolled-back tail with exact original anchor output");
        var cross=await ingest(130,false);await Until(restarted,"committed");await Until(restarted,"committed");await Until(restarted,"idle");
        page=await store.ReadPeriodPageAsync(identity,100);
        var nextExpected=new ClosedActivityPeriod(scope.MachineId,scope.StreamKey,scope.MappingId,ActivityState.Running,
            cross[0].ObservedAt,cross[1].ObservedAt,cross[1].ObservedAt.UtcTicks-cross[0].ObservedAt.UtcTicks,
            scope.InstanceId,cross[0].Sequence,cross[1].Sequence,"ACTIVE","READY");
        var afterCross=await SnapshotState();
        Check(afterCross.Periods==baseline.Periods+2 && page.Rows.Count(x=>x.Period==nextExpected)==1 &&
            !page.Rows.Any(x=>x.Period.StartSequence==opening[2].Sequence && x.Period.EndSequence==cross[0].Sequence),
            "continuous host discovers later ingestion and preserves explicit cross-batch breaks");
        await Stop(restarted);
        var idleRestart=await Launch();await Until(idleRestart,"idle");await Stop(idleRestart);
        Check(await SnapshotState()==afterCross,"post-commit restart neither reprocesses history nor duplicates receipts");
        foreach(var wrong in new[]{scope with{InstanceId=scope.InstanceId+1},scope with{MappingId=Guid.NewGuid()}})
        {var denied=await Launch(wrong);await Until(denied,"held");await denied.Process.WaitForExitAsync();Check(denied.Process.ExitCode==20 && await SnapshotState()==afterCross,"wrong source or mapping holds without durable effects");}
        var originalJson=afterCross.Checkpoint;
        var forged=System.Text.Json.Nodes.JsonNode.Parse(originalJson)!;
        forged["AcceptedSource"]!["OperationId"]=Guid.NewGuid();
        async Task SetCheckpoint(string json)=>await Exec("UPDATE activity.ProcessorCheckpoint SET CheckpointJson=@json,CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json)) WHERE MachineId=@machine AND MappingId=@mapping",("@json",json),("@machine",scope.MachineId),("@mapping",scope.MappingId));
        await SetCheckpoint(forged.ToJsonString());
        try
        {
            var bad=await Launch();await Until(bad,"held");await bad.Process.WaitForExitAsync();
            Check(bad.Process.ExitCode==20,"idle restart rejects a checksum-consistent fabricated checkpoint ancestor");
        }
        finally { await SetCheckpoint(originalJson); }
        var checkpoint=await store.ReadCheckpointAsync(identity);
        await Exec("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        try
        {
            await Exec("UPDATE acquisition.MtConnectObservation SET AddressBytes=0x00 WHERE MachineId=@machine AND MappingId=@mapping AND InstanceId=@instance AND Sequence=@sequence",
                ("@machine",scope.MachineId),("@mapping",scope.MappingId),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)checkpoint.LastConsumedSample!.Sequence));
            var bad=await Launch();await Until(bad,"held");await bad.Process.WaitForExitAsync();
            Check(bad.Process.ExitCode==20 && await SnapshotState()==afterCross,"idle restart revalidates retained predecessor and rejects missing provenance");
        }
        finally
        {
            await Exec("UPDATE acquisition.MtConnectObservation SET AddressBytes=@address WHERE MachineId=@machine AND MappingId=@mapping AND InstanceId=@instance AND Sequence=@sequence",
                ("@address",Encoding.UTF8.GetBytes(identity.SourceAddress)),("@machine",scope.MachineId),("@mapping",scope.MappingId),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)checkpoint.LastConsumedSample!.Sequence));
            await Exec("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        }
        Check(children.Select(x=>x.Process.Id).Distinct().Count()==children.Count,"hosting acceptance uses distinct real process lifetimes");
        Directory.CreateDirectory("artifacts/ingestion");
        await File.WriteAllTextAsync("artifacts/ingestion/activity-host.json",JsonSerializer.Serialize(new {fixture=true,checks,
            processIds=children.Select(x=>x.Process.Id),before=baseline,after=afterCross,
            limitations="Single exact source instance/mapping; no physical completeness, automatic recovery or production commissioning claim."}));
        return checks;
        }
        finally { foreach(var child in children)await Dispose(child); }

        async Task<Child> Launch(ActivityWorkerScope? selected=null){var child=await Start(target,selected??scope,1);children.Add(child);return child;}
        async Task<Snapshot> SnapshotState()
        {
            await using var q=new SqlCommand("SELECT CheckpointJson,(SELECT COUNT_BIG(*) FROM activity.ClosedPeriod WHERE MachineId=@machine AND MappingId=@mapping),(SELECT COUNT_BIG(*) FROM activity.ProcessorOperation WHERE MachineId=@machine AND MappingId=@mapping) FROM activity.ProcessorCheckpoint WHERE MachineId=@machine AND MappingId=@mapping",c);
            Bind(q,("@machine",scope.MachineId),("@mapping",scope.MappingId));await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();return new(r.GetString(0),r.GetInt64(1),r.GetInt64(2));
        }
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Bind(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Bind(q,args);await q.ExecuteNonQueryAsync();}
        void Check(bool result,string label){if(!result)throw new InvalidOperationException("Activity host: "+label);checks++;Console.WriteLine("PASS: activity host "+label);}
    }
    static async Task<Child> Start(InstallationTarget target,ActivityWorkerScope scope,int size)
    {
        var app="fc2-activity-host-"+Guid.NewGuid().ToString("N");
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"FactoryConnect.slnx")))directory=directory.Parent;
        var dll=Path.Combine(directory!.FullName,"tools","FactoryConnect.ActivityHost","bin",new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,"net10.0","FactoryConnect.ActivityHost.dll");
        var info=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(dll);info.ArgumentList.Add("--stdin");var process=Process.Start(info)!;
        var events=Channel.CreateBounded<JsonElement>(100);
        var reader=Task.Run(async()=>{try{while(await process.StandardOutput.ReadLineAsync() is {} line){using var json=JsonDocument.Parse(line);if(!events.Writer.TryWrite(json.RootElement.Clone()))throw new InvalidDataException("Host event bound exceeded.");}events.Writer.Complete();}catch(Exception e){events.Writer.TryComplete(e);}});
        var child=new Child(process,events,reader,process.StandardError.ReadToEndAsync(),app);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {version=1,scope.InstallationId,scope.SiteId,scope.MachineId,scope.MappingId,scope.StreamKey,
            InstanceId=scope.InstanceId.ToString(CultureInfo.InvariantCulture),BootstrapAfterSequence=scope.BootstrapAfterSequence.ToString(CultureInfo.InvariantCulture),
            SqlConnectionString=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=app}.ConnectionString,CutSize=size,PollIntervalMilliseconds=100,OperationTimeoutSeconds=15}));
        await process.StandardInput.FlushAsync();return child;
    }
    static async Task<JsonElement> Until(Child child,string status)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(await child.Events.Reader.WaitToReadAsync(timeout.Token))
            while(child.Events.Reader.TryRead(out var item))
            {var actual=item.GetProperty("status").GetString();if(actual==status)return item;if(actual=="held")throw new InvalidOperationException("Activity host unexpectedly held: "+item);}
        throw new InvalidOperationException("Activity host exited before "+status+": "+await child.Errors);
    }
    static async Task Stop(Child child){await child.Process.StandardInput.WriteLineAsync("stop");await child.Process.StandardInput.FlushAsync();await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));if(child.Process.ExitCode!=0)throw new InvalidOperationException("Activity host did not stop cleanly.");}
    static async Task Dispose(Child child){if(!child.Process.HasExited){child.Process.Kill(true);await child.Process.WaitForExitAsync();}await child.Reader;await child.Errors;child.Process.Dispose();}
    static async Task Wait(Func<Task<bool>> condition){var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromSeconds(10)){if(await condition())return;await Task.Delay(20);}throw new TimeoutException("Activity host SQL barrier was not observed.");}
    static void RequireDisposable(string name){const string prefix="FactoryConnect_V2_Test_";if(!name.StartsWith(prefix,StringComparison.Ordinal)||!Guid.TryParseExact(name[prefix.Length..],"N",out _))throw new InvalidDataException("Generated disposable SQL database required.");}
    static void Bind(SqlCommand q,params (string Name,object Value)[] args){foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,512).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
