using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

// Acceptance-only process mode. No worker registration, provisioning or migration.
internal static class ActivityProcessRestartAcceptance
{
    internal sealed record Cut(Guid OperationId,ulong After,ulong Through,ulong Generation);
    internal sealed record ChildInput(string ConnectionString,Guid InstallationId,Guid SiteId,
        Guid MachineId,Guid MappingId,string StreamKey,ulong InstanceId,Cut Cut,bool PauseAfterCommit);
    internal sealed record ChildEvent(int ProcessId,string Status,ActivityDurableResult? Result,
        string? Error=null,int? SqlNumber=null,string? Reason=null);
    private sealed record Child(Process Process,Task<string?> Line,Task<string> Error,string Application);
    private sealed record Snapshot(string Checkpoint,long Periods,long Receipts);

    internal static async Task<int> RunChildAsync()
    {
        try
        {
            var line=await Console.In.ReadLineAsync()
                ?? throw new InvalidDataException("Missing acceptance child input.");
            var input=JsonSerializer.Deserialize<ChildInput>(line)
                ?? throw new InvalidDataException("Invalid acceptance child input.");
            RequireDisposable(new SqlConnectionStringBuilder(input.ConnectionString).InitialCatalog);
            var result=await new SqlActivityProcessorStore(input.ConnectionString).ApplyAcceptedAsync(
                input.Cut.OperationId,input.InstallationId,input.SiteId,input.MachineId,
                input.MappingId,input.StreamKey,input.InstanceId,input.Cut.After,
                input.Cut.Through,input.Cut.Generation);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                new ChildEvent(Environment.ProcessId,"committed",result)));
            await Console.Out.FlushAsync();
            // Parent kills this process only after independently observing SQL
            // durability. Waiting on stdin keeps it alive without further work.
            if(input.PauseAfterCommit)await Console.In.ReadLineAsync();
            return 0;
        }
        catch(Exception error)when(error is InvalidDataException or InvalidOperationException or SqlException)
        {
            // Only invariant messages are surfaced; never echo configuration or credentials.
            var reason=error is InvalidDataException ? error.Message : null;
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new ChildEvent(
                Environment.ProcessId,"held",null,error.GetType().Name,
                (error as SqlException)?.Number,reason)));
            return 20;
        }
        catch(Exception error)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new ChildEvent(
                Environment.ProcessId,"failed",null,error.GetType().Name)));
            return 1;
        }
    }

    internal static async Task<int> RunAsync(InstallationTarget target,Guid siteId,
        ApprovedActivitySignal approved,ActivityQualificationIdentity identity,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> opening,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> crossBatch,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> pending)
    {
        RequireDisposable(target.Database);
        var checks=0;
        var children=new List<Child>();
        var evidence=new List<ChildEvent>();
        var killed=new List<int>();
        var barrierResource="fc2:activity:crash:"+Guid.NewGuid().ToString("N");
        var store=new SqlActivityProcessorStore(target.ConnectionString);
        await using var c=new SqlConnection(target.ConnectionString);
        await c.OpenAsync();
        var initial=await store.ReadCheckpointAsync(identity);
        Check(initial.AcceptedSource is not null &&
            initial.AcceptedSource.PackageRevisionId==approved.PackageRevisionId &&
            initial.AcceptedSource.AcknowledgementId==approved.AcknowledgementId &&
            opening.Count==3 && crossBatch.Count==2 && pending.Count==2 &&
            opening[0].RawValue=="ACTIVE" && opening[1].RawValue=="ACTIVE" && opening[2].RawValue=="READY" &&
            opening.Select(x=>x.BatchId).Distinct().Count()==1 &&
            crossBatch.Select(x=>x.BatchId).Distinct().Count()==1 &&
            opening[0].BatchId!=crossBatch[0].BatchId && crossBatch[0].BatchId!=pending[0].BatchId,
            "actual accepted repeated-state and separate-batch documents seed the process fixture");
        var initialSnapshot=await ReadSnapshot();
        var openCut=new Cut(Guid.NewGuid(),initial.AcceptedSource!.ThroughSequence,
            opening[1].Sequence,initial.NextGeneration);
        var tailCut=new Cut(Guid.NewGuid(),openCut.Through,opening[2].Sequence,openCut.Generation+1);
        var crossCut=new Cut(Guid.NewGuid(),tailCut.Through,crossBatch[1].Sequence,tailCut.Generation+1);
        var pendingCut=new Cut(Guid.NewGuid(),crossCut.Through,pending[1].Sequence,crossCut.Generation+1);
        try
        {
            var opener=await Start(openCut,pause:true);
            var opened=await Committed(opener,waitForExit:false);
            Check(!opened.Replayed && opened.NewPeriods.Count==0 &&
                opened.Checkpoint.OpenAnchor?.Sequence==opening[0].Sequence &&
                opened.Checkpoint.LastConsumedSample?.Sequence==opening[1].Sequence &&
                opened.Checkpoint.OpenAnchor.Sequence!=opened.Checkpoint.LastConsumedSample.Sequence &&
                opened.Checkpoint.AcceptedSource?.OpenAnchorObservation==opening[0],
                "child commits the original open anchor across a repeated state");
            Check(await store.ReadCheckpointAsync(identity)==opened.Checkpoint,
                "parent independently reads committed child checkpoint before abrupt termination");
            await Kill(opener);
            var afterOpen=await ReadSnapshot();
            Check(await store.ReadCheckpointAsync(identity)==opened.Checkpoint &&
                afterOpen.Periods==initialSnapshot.Periods && afterOpen.Receipts==initialSnapshot.Receipts+1,
                "kill after commit preserves exact checkpoint ancestry and one receipt");

            // This trigger exists solely in the uniquely named disposable test
            // database. Store order is checkpoint -> receipt -> periods -> commit.
            await Exec($"""
                CREATE TRIGGER activity.Fixture_ProcessCommitBarrier ON activity.ClosedPeriod AFTER INSERT AS
                BEGIN
                    DECLARE @r int;
                    EXEC @r=sys.sp_getapplock @Resource='{barrierResource}',@LockMode='Shared',
                        @LockOwner='Transaction',@LockTimeout=15000;
                    IF @r<0 THROW 51090,'Activity process acceptance barrier unavailable.',1;
                END;
                """);
            try
            {
                await using var barrier=new SqlConnection(target.ConnectionString);
                await barrier.OpenAsync();
                await using var tx=(SqlTransaction)await barrier.BeginTransactionAsync();
                await using(var hold=new SqlCommand("""
                    DECLARE @r int;
                    EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
                        @LockOwner='Transaction',@LockTimeout=10000;
                    IF @r<0 THROW 51090,'Activity process acceptance barrier unavailable.',1;
                    """,barrier,tx))
                {
                    hold.Parameters.AddWithValue("@resource",barrierResource);
                    await hold.ExecuteNonQueryAsync();
                }
                var blocked=await Start(tailCut,pause:false);
                try
                {
                    await Wait(async()=>Convert.ToInt32(await Scalar("""
                        SELECT COUNT(*) FROM sys.dm_tran_locks l
                        JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id
                        WHERE s.program_name=@app AND l.resource_type='APPLICATION'
                            AND l.request_status='WAIT' AND l.request_mode='S'
                        """,("@app",blocked.Application)))==1,"child period-insert barrier",blocked);
                    Check(!blocked.Process.HasExited && !blocked.Line.IsCompleted,
                        "SQL observes child blocked after period insert and before transaction commit");
                    await Kill(blocked,waitForSql:false);
                }
                finally
                {
                    if(!blocked.Process.HasExited)await Kill(blocked,waitForSql:false);
                    await tx.CommitAsync();
                }
                await DrainSql(blocked);
            }
            finally { await Exec("DROP TRIGGER IF EXISTS activity.Fixture_ProcessCommitBarrier"); }
            Check(await ReadSnapshot()==afterOpen && await store.ReadCheckpointAsync(identity)==opened.Checkpoint,
                "kill before commit rolls back checkpoint period and operation receipt atomically");

            var resumedChild=await Start(tailCut,pause:true);
            var resumed=await Committed(resumedChild,waitForExit:false);
            var expected=new ClosedActivityPeriod(identity.MachineId,identity.StreamKey,identity.MappingId,
                ActivityState.Running,opening[0].ObservedAt,opening[2].ObservedAt,
                opening[2].ObservedAt.UtcTicks-opening[0].ObservedAt.UtcTicks,opening[0].InstanceId,
                opening[0].Sequence,opening[2].Sequence,opening[0].RawValue,opening[2].RawValue);
            Check(!resumed.Replayed && resumed.NewPeriods.Count==1 && resumed.NewPeriods[0]==expected &&
                resumed.Checkpoint.NextGeneration==tailCut.Generation+1 &&
                resumed.Checkpoint.LastConsumedSample?.ContinuityFromPrevious==true &&
                resumed.Checkpoint.AcceptedSource?.OperationId==tailCut.OperationId,
                "new OS process retries the rolled-back operation and preserves the original same-batch anchor");
            var afterResume=await ReadSnapshot();
            Check(afterResume.Periods==afterOpen.Periods+1 && afterResume.Receipts==afterOpen.Receipts+1 &&
                (await store.ReadPeriodPageAsync(identity)).Rows.Any(x=>x.Period==expected),
                "restart publishes the exact retained-source period once with atomic durable receipt");
            await Kill(resumedChild);
            Check(await ReadSnapshot()==afterResume,
                "kill after period commit preserves the exact period checkpoint and receipt");

            var crossed=await Committed(await Start(crossCut,pause:false));
            var crossExpected=new ClosedActivityPeriod(identity.MachineId,identity.StreamKey,identity.MappingId,
                ActivityState.Running,crossBatch[0].ObservedAt,crossBatch[1].ObservedAt,
                crossBatch[1].ObservedAt.UtcTicks-crossBatch[0].ObservedAt.UtcTicks,crossBatch[0].InstanceId,
                crossBatch[0].Sequence,crossBatch[1].Sequence,crossBatch[0].RawValue,crossBatch[1].RawValue);
            Check(!crossed.Replayed && crossed.NewPeriods.Count==1 && crossed.NewPeriods[0]==crossExpected &&
                crossed.Checkpoint.NextGeneration==crossCut.Generation+1,
                "restart fences the previous batch and emits only the next batch's internally proven period");
            var stable=await ReadSnapshot();
            Check(stable.Periods==afterResume.Periods+1 && stable.Receipts==afterResume.Receipts+1,
                "cross-batch restart invents no bridging period");

            var replay=await Committed(await Start(tailCut,pause:false));
            Check(replay.Replayed && replay.Checkpoint==resumed.Checkpoint &&
                replay.NewPeriods.SequenceEqual(resumed.NewPeriods) &&
                await ReadSnapshot()==stable && await store.ReadCheckpointAsync(identity)==crossed.Checkpoint,
                "historical operation replay returns its original receipt while preserving newer durable progress");

            foreach(var damaged in new[]{
                crossed.Checkpoint with { AcceptedSource=null },
                crossed.Checkpoint with { AcceptedSource=crossed.Checkpoint.AcceptedSource! with {
                    LastObservation=crossed.Checkpoint.AcceptedSource.LastObservation with { BatchId=Guid.NewGuid() } } } })
            {
                await SetCheckpoint(damaged);
                try
                {
                    var beforeHeld=await ReadSnapshot();
                    var held=await Held(await Start(pendingCut,pause:false));
                    Check(held.Error==nameof(InvalidDataException) &&
                        held.Reason==(damaged.AcceptedSource is null
                            ? "Accepted checkpoint predecessor provenance is missing."
                            : "Accepted checkpoint differs from its immutable operation receipt.") &&
                        await ReadSnapshot()==beforeHeld,
                        "new process rejects missing or altered checkpoint provenance without advancing durable state");
                }
                finally { await SetCheckpoint(crossed.Checkpoint); }
            }
            // Missing retained predecessor, independent of the new cut's rows.
            await Exec("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
            try
            {
                await Exec("""
                    UPDATE acquisition.MtConnectObservation SET AddressBytes=0x464F52474544
                    WHERE MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND Sequence=@sequence
                    """,("@machine",identity.MachineId),("@stream",System.Text.Encoding.UTF8.GetBytes(identity.StreamKey)),
                    ("@instance",(decimal)crossBatch[1].InstanceId),("@sequence",(decimal)crossBatch[1].Sequence));
                var held=await Held(await Start(pendingCut,pause:false));
                Check(held.Error==nameof(SqlException) && held.SqlNumber==51078 && await ReadSnapshot()==stable,
                    "new process rejects a missing exact retained predecessor with no partial effects");
            }
            finally
            {
                await Exec("""
                    UPDATE acquisition.MtConnectObservation SET AddressBytes=@address
                    WHERE MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND Sequence=@sequence
                    """,("@address",System.Text.Encoding.UTF8.GetBytes(identity.SourceAddress)),
                    ("@machine",identity.MachineId),("@stream",System.Text.Encoding.UTF8.GetBytes(identity.StreamKey)),
                    ("@instance",(decimal)crossBatch[1].InstanceId),("@sequence",(decimal)crossBatch[1].Sequence));
                await Exec("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
            }
            Check(await ReadSnapshot()==stable && await store.ReadCheckpointAsync(identity)==crossed.Checkpoint &&
                children.Select(x=>x.Process.Id).Distinct().Count()==children.Count && children.Count==8,
                "eight distinct child lifetimes recover only SQL state and preserve final exact checkpoint");
            Directory.CreateDirectory("artifacts/ingestion");
            await File.WriteAllTextAsync("artifacts/ingestion/activity-process-restart.json",JsonSerializer.Serialize(new {
                schemaVersion=1,fixture=true,commit=Environment.GetEnvironmentVariable("GITHUB_SHA"),checks,
                scope="Activity accepted-evidence processing across real child-process lifetimes; disposable SQL only",
                childProcessIds=children.Select(x=>x.Process.Id).ToArray(),killedProcessIds=killed.ToArray(),
                baseline=initialSnapshot,final=stable,events=evidence,
                limitations=new[]{"Same verified batch can link; cross-batch continuity remains explicitly broken.",
                    "Retained accepted source integrity does not establish physical MTConnect completeness.",
                    "No production worker, activity API, UI, large-history or fleet capacity qualification."}
            },new JsonSerializerOptions{WriteIndented=true}));
            return checks;
        }
        finally
        {
            foreach(var child in children)
            {
                if(!child.Process.HasExited)
                {
                    child.Process.Kill(entireProcessTree:true);
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                await child.Line.WaitAsync(TimeSpan.FromSeconds(5));
                await child.Error.WaitAsync(TimeSpan.FromSeconds(5));
                child.Process.Dispose();
            }
        }

        async Task<Child> Start(Cut cut,bool pause)
        {
            var application="fc2-activity-child-"+Guid.NewGuid().ToString("N");
            var connection=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=application};
            var host=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet";
            var info=new ProcessStartInfo(host){UseShellExecute=false,RedirectStandardOutput=true,
                RedirectStandardError=true,RedirectStandardInput=true};
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--activity-child");
            var process=Process.Start(info)??throw new InvalidOperationException("Activity acceptance child did not start.");
            var child=new Child(process,process.StandardOutput.ReadLineAsync(),process.StandardError.ReadToEndAsync(),application);
            children.Add(child);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new ChildInput(
                connection.ConnectionString,target.InstallationId,siteId,identity.MachineId,identity.MappingId,
                identity.StreamKey,opening[0].InstanceId,cut,pause)));
            await process.StandardInput.FlushAsync();
            return child;
        }
        async Task<ChildEvent> Event(Child child)
        {
            var line=await child.Line.WaitAsync(TimeSpan.FromSeconds(20));
            var result=line is null ? null : JsonSerializer.Deserialize<ChildEvent>(line);
            if(result is null || result.ProcessId!=child.Process.Id)
                throw new InvalidOperationException("Activity child returned no process-bound event.");
            evidence.Add(result);
            return result;
        }
        async Task<ActivityDurableResult> Committed(Child child,bool waitForExit=true)
        {
            var result=await Event(child);
            if(waitForExit)await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Status=="committed" && result.Result is not null &&
                (!waitForExit || child.Process.ExitCode==0),"child reports successful accepted-source commit or exact replay");
            return result.Result!;
        }
        async Task<ChildEvent> Held(Child child)
        {
            var result=await Event(child);
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Status=="held" && child.Process.ExitCode==20 && result.Result is null,
                "child holds invalid durable evidence instead of reporting success");
            return result;
        }
        async Task Kill(Child child,bool waitForSql=true)
        {
            Check(!child.Process.HasExited,"acceptance terminates a live child abruptly");
            killed.Add(child.Process.Id);
            child.Process.Kill(entireProcessTree:true);
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if(waitForSql)await DrainSql(child);
        }
        async Task DrainSql(Child child)
        {
            await Wait(async()=>Convert.ToInt32(await Scalar(
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name=@app",("@app",child.Application)))==0,
                "killed child SQL sessions and rollback drained");
        }
        async Task Wait(Func<Task<bool>> condition,string label,Child? child=null)
        {
            var clock=Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromSeconds(10))
            {
                if(await condition())return;
                if(child?.Process.HasExited==true)
                    throw new InvalidOperationException("Activity child exited before "+label);
                await Task.Delay(20);
            }
            throw new InvalidOperationException("Timed out waiting for "+label);
        }
        async Task<Snapshot> ReadSnapshot()
        {
            await using var q=new SqlCommand("""
                SELECT CheckpointJson,
                    (SELECT COUNT_BIG(*) FROM activity.ClosedPeriod WHERE MachineId=@machine AND MappingId=@mapping),
                    (SELECT COUNT_BIG(*) FROM activity.ProcessorOperation WHERE MachineId=@machine AND MappingId=@mapping)
                FROM activity.ProcessorCheckpoint WHERE MachineId=@machine AND MappingId=@mapping AND StreamKeyBytes=@stream
                """,c);
            Bind(q,("@machine",identity.MachineId),("@mapping",identity.MappingId),
                ("@stream",System.Text.Encoding.UTF8.GetBytes(identity.StreamKey)));
            await using var r=await q.ExecuteReaderAsync();
            if(!await r.ReadAsync())throw new InvalidDataException("Activity process fixture checkpoint missing.");
            return new(r.GetString(0),r.GetInt64(1),r.GetInt64(2));
        }
        async Task SetCheckpoint(ActivityProcessorCheckpoint checkpoint)
        {
            await Exec("""
                UPDATE activity.ProcessorCheckpoint SET CheckpointJson=@json,
                    CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json))
                WHERE MachineId=@machine AND MappingId=@mapping
                """,("@json",JsonSerializer.Serialize(checkpoint)),("@machine",identity.MachineId),("@mapping",identity.MappingId));
        }
        async Task<object?> Scalar(string sql,params (string,object)[] parameters)
        {
            await using var q=new SqlCommand(sql,c);Bind(q,parameters);return await q.ExecuteScalarAsync();
        }
        async Task Exec(string sql,params (string,object)[] parameters)
        {
            await using var q=new SqlCommand(sql,c);Bind(q,parameters);await q.ExecuteNonQueryAsync();
        }
        void Check(bool value,string label)
        {
            if(!value)throw new InvalidOperationException("Activity process acceptance: "+label);
            checks++;Console.WriteLine("PASS: activity process "+label);
        }
    }
    private static void RequireDisposable(string database)
    {
        const string prefix="FactoryConnect_V2_Test_";
        if(!database.StartsWith(prefix,StringComparison.Ordinal) ||
           !Guid.TryParseExact(database[prefix.Length..],"N",out _))
            throw new InvalidDataException("Activity process acceptance requires a generated disposable database.");
    }
    private static void Bind(SqlCommand command,params (string Name,object Value)[] parameters)
    {
        foreach(var(name,value)in parameters)
            if(value is byte[] bytes)command.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;
            else command.Parameters.AddWithValue(name,value);
    }
}
