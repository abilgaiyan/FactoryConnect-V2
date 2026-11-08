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
            await LegacyReplay(target,scope,candidate);
            await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
            await using var q=new SqlCommand("SELECT ResultBytes FROM activity.ProcessorOperation WHERE OperationId=@op",c);
            q.Parameters.AddWithValue("@op",candidate.OperationId);
            return JsonSerializer.Deserialize<ActivityDurableResult>((byte[])(await q.ExecuteScalarAsync())!)!;
        }
        finally { await Dispose(child); }
    }

    private static async Task LegacyReplay(InstallationTarget target,ActivityWorkerScope scope,ActivityWorkCandidate cut)
    {
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        string original;byte[] result;
        await using(var q=new SqlCommand("SELECT p.CheckpointJson,o.ResultBytes FROM activity.ProcessorCheckpoint p JOIN activity.ProcessorOperation o ON o.OperationId=@op WHERE p.MachineId=@machine AND p.MappingId=@mapping",c))
        {q.Parameters.AddWithValue("@op",cut.OperationId);q.Parameters.AddWithValue("@machine",scope.MachineId);q.Parameters.AddWithValue("@mapping",scope.MappingId);await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();original=r.GetString(0);result=(byte[])r[1];}
        var cp=System.Text.Json.Nodes.JsonNode.Parse(original)!;
        cp["AcceptedSource"]!.AsObject().Remove("SiteId");cp["AcceptedSource"]!.AsObject().Remove("FrontierBatch");
        var receipt=System.Text.Json.Nodes.JsonNode.Parse(result)!;
        receipt["Checkpoint"]!["AcceptedSource"]!.AsObject().Remove("SiteId");receipt["Checkpoint"]!["AcceptedSource"]!.AsObject().Remove("FrontierBatch");
        async Task Put(string json,byte[] bytes)
        {
            await using var q=new SqlCommand("UPDATE activity.ProcessorCheckpoint SET CheckpointJson=@json,CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json)) WHERE MachineId=@machine AND MappingId=@mapping; UPDATE activity.ProcessorOperation SET ResultBytes=@result WHERE OperationId=@op",c);
            q.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=json;q.Parameters.Add("@result",SqlDbType.VarBinary,-1).Value=bytes;
            q.Parameters.AddWithValue("@machine",scope.MachineId);q.Parameters.AddWithValue("@mapping",scope.MappingId);q.Parameters.AddWithValue("@op",cut.OperationId);await q.ExecuteNonQueryAsync();
        }
        await using(var disable=new SqlCommand("DISABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation",c))await disable.ExecuteNonQueryAsync();
        try
        {
            await Put(cp.ToJsonString(),Encoding.UTF8.GetBytes(receipt.ToJsonString()));
            var replay=await new SqlActivityProcessorStore(target.ConnectionString).ApplyAcceptedAsync(cut.OperationId,scope.InstallationId,scope.SiteId,scope.MachineId,scope.MappingId,scope.StreamKey,scope.InstanceId,cut.AfterSequence,cut.ThroughSequence,cut.ExpectedGeneration);
            if(!replay.Replayed || replay.NewPeriods.Count!=1)throw new InvalidDataException("Legacy v2 request/receipt replay changed.");
            Console.WriteLine("PASS: activity host legacy nonempty request bytes and pre-frontier JSON receipts remain replay-compatible");
        }
        finally
        {
            await Put(original,result);
            await using var enable=new SqlCommand("ENABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation",c);await enable.ExecuteNonQueryAsync();
        }
    }

    internal static async Task<int> ExpiringEmptyCut(InstallationTarget target,ActivityWorkerScope scope,
        ActivityWorkCandidate candidate,DateTimeOffset expires)
    {
        RequireDisposable(target.Database);
        var app="fc2-empty-expiry-"+Guid.NewGuid().ToString("N");
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        await using var holder=new SqlConnection(target.ConnectionString);await holder.OpenAsync();
        await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();
        var resource=$"fc2:activity:{scope.MachineId:N}:{scope.MappingId:N}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(scope.StreamKey)))}";
        await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51098,'Fixture lock unavailable.',1;",holder,tx))
        {q.Parameters.AddWithValue("@resource",resource);await q.ExecuteNonQueryAsync();}
        var store=new SqlActivityProcessorStore(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=app}.ConnectionString);
        var publishing=store.ApplyAcceptedWorkAsync(candidate);
        try
        {
            await Wait(async()=> {
                await using var q=new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode='X'",c);
                q.Parameters.AddWithValue("@app",app);return Convert.ToInt32(await q.ExecuteScalarAsync())==1;
            });
            if(publishing.IsCompleted)throw new InvalidOperationException("Empty publication did not pass preflight before its lock wait.");
            while(true)
            {
                await using var clock=new SqlCommand("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')",c);
                if((DateTimeOffset)(await clock.ExecuteScalarAsync())!>=expires)break;
                await Task.Delay(20);
            }
        }
        finally { await tx.RollbackAsync(); }
        var rejected=false;
        try { await publishing; }catch(Exception e)when(e is SqlException or InvalidDataException or FactoryConnect.Sql.Security.SecurityDeniedException){rejected=true;}
        await using var unchanged=new SqlCommand("SELECT (SELECT COUNT(*) FROM activity.ProcessorCheckpoint WHERE MachineId=@machine)+(SELECT COUNT(*) FROM activity.ProcessorOperation WHERE MachineId=@machine)+(SELECT COUNT(*) FROM activity.ClosedPeriod WHERE MachineId=@machine)",c);
        unchanged.Parameters.AddWithValue("@machine",scope.MachineId);
        if(!rejected || Convert.ToInt32(await unchanged.ExecuteScalarAsync())!=0)throw new InvalidOperationException("Expiring no-state publication changed durable state.");
        Console.WriteLine("PASS: currently eligible no-state cut passes preflight, waits on the processor gate, expires and rejects without checkpoint receipt or periods");
        return 1;
    }

    private static async Task<int> RejectChangedEmptyProjection(InstallationTarget target,ActivityWorkerScope scope,ActivityWorkCandidate candidate)
    {
        var app="fc2-empty-projection-"+Guid.NewGuid().ToString("N");
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        await using var holder=new SqlConnection(target.ConnectionString);await holder.OpenAsync();
        await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();
        var resource=$"fc2:activity:{scope.MachineId:N}:{scope.MappingId:N}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(scope.StreamKey)))}";
        await using(var gate=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@rsc,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51098,'Fixture lock unavailable.',1;",holder,tx))
        {gate.Parameters.AddWithValue("@rsc",resource);await gate.ExecuteNonQueryAsync();}
        var publishing=new SqlActivityProcessorStore(new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=app}.ConnectionString).ApplyAcceptedWorkAsync(candidate);
        var sequence=scope.BootstrapAfterSequence+1;Guid original;
        await using(var q=new SqlCommand("SELECT MappingId FROM acquisition.MtConnectObservation WHERE MachineId=@machine AND InstanceId=@instance AND Sequence=@seq",c))
        {q.Parameters.AddWithValue("@machine",scope.MachineId);q.Parameters.AddWithValue("@instance",(decimal)scope.InstanceId);q.Parameters.AddWithValue("@seq",(decimal)sequence);original=(Guid)(await q.ExecuteScalarAsync())!;}
        async Task Mapping(Guid id)
        {
            await using var q=new SqlCommand("UPDATE acquisition.MtConnectObservation SET MappingId=@mapping WHERE MachineId=@machine AND InstanceId=@instance AND Sequence=@seq",c);
            q.Parameters.AddWithValue("@mapping",id);q.Parameters.AddWithValue("@machine",scope.MachineId);q.Parameters.AddWithValue("@instance",(decimal)scope.InstanceId);q.Parameters.AddWithValue("@seq",(decimal)sequence);await q.ExecuteNonQueryAsync();
        }
        await Wait(async()=> {
            await using var q=new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode='X'",c);
            q.Parameters.AddWithValue("@app",app);return Convert.ToInt32(await q.ExecuteScalarAsync())==1;
        });
        await using(var disable=new SqlCommand("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation",c))await disable.ExecuteNonQueryAsync();
        try
        {
            await Mapping(scope.MappingId);await tx.RollbackAsync();
            var rejected=false;try{await publishing;}catch(SqlException e)when(e.Number==51093){rejected=true;}
            await using var count=new SqlCommand("SELECT (SELECT COUNT(*) FROM activity.ProcessorCheckpoint WHERE MachineId=@machine)+(SELECT COUNT(*) FROM activity.ProcessorOperation WHERE MachineId=@machine)+(SELECT COUNT(*) FROM activity.ClosedPeriod WHERE MachineId=@machine)",c);
            count.Parameters.AddWithValue("@machine",scope.MachineId);
            if(!rejected||Convert.ToInt32(await count.ExecuteScalarAsync())!=0)throw new InvalidOperationException("Changed no-state projection was published.");
        }
        finally
        {
            await Mapping(original);
            await using var enable=new SqlCommand("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation",c);await enable.ExecuteNonQueryAsync();
        }
        Console.WriteLine("PASS: no-state writer revalidates projections after its processor wait and rolls back a changed source cut");
        return 1;
    }

    private static async Task<int> CrashEmptyBeforeCommit(InstallationTarget target,ActivityWorkerScope scope)
    {
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var barrier="fc2-empty-commit-"+Guid.NewGuid().ToString("N");
        await using(var trigger=new SqlCommand($"""
            CREATE TRIGGER activity.Fixture_NoStateHostCommit ON activity.ProcessorOperation AFTER INSERT AS
            BEGIN
                IF EXISTS(SELECT 1 FROM inserted WHERE JSON_VALUE(CONVERT(varchar(max),RequestBytes),'$.kind')='verified-no-state-cut-v1')
                BEGIN
                    DECLARE @r int;
                    EXEC @r=sys.sp_getapplock @Resource=N'{barrier}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=30000;
                    IF @r<0 THROW 51098,'Fixture barrier unavailable.',1;
                END
            END
            """,c))await trigger.ExecuteNonQueryAsync();
        Child? child=null;
        try
        {
            await using var holder=new SqlConnection(target.ConnectionString);await holder.OpenAsync();
            await using var tx=(SqlTransaction)await holder.BeginTransactionAsync();
            await using(var gate=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51098,'Fixture lock unavailable.',1;",holder,tx))
            {gate.Parameters.AddWithValue("@resource",barrier);await gate.ExecuteNonQueryAsync();}
            child=await Start(target,scope,1);var observed=child;
            try
            {
                await Wait(async()=> {
                    await using var q=new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode='S'",c);
                    q.Parameters.AddWithValue("@app",observed.App);return Convert.ToInt32(await q.ExecuteScalarAsync())==1;
                });
                if(child.Process.HasExited)throw new InvalidOperationException("No-state child exited before its commit barrier.");
                child.Process.Kill(true);await child.Process.WaitForExitAsync();
            }
            finally {if(!child.Process.HasExited){child.Process.Kill(true);await child.Process.WaitForExitAsync();}await tx.RollbackAsync();}
            await Wait(async()=> {
                await using var q=new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name=@app",c);q.Parameters.AddWithValue("@app",observed.App);return Convert.ToInt32(await q.ExecuteScalarAsync())==0;
            });
            await using var unchanged=new SqlCommand("SELECT (SELECT COUNT(*) FROM activity.ProcessorCheckpoint WHERE MachineId=@machine)+(SELECT COUNT(*) FROM activity.ProcessorOperation WHERE MachineId=@machine)+(SELECT COUNT(*) FROM activity.ClosedPeriod WHERE MachineId=@machine)",c);
            unchanged.Parameters.AddWithValue("@machine",scope.MachineId);if(Convert.ToInt32(await unchanged.ExecuteScalarAsync())!=0)throw new InvalidOperationException("Killed no-state publication left partial durable effects.");
        }
        finally
        {
            if(child is not null)await Dispose(child);
            await using var drop=new SqlCommand("DROP TRIGGER IF EXISTS activity.Fixture_NoStateHostCommit",c);await drop.ExecuteNonQueryAsync();
        }
        Console.WriteLine("PASS: no-state child killed after checkpoint/receipt insertion and before commit rolls back all effects");
        return 1;
    }

    internal static async Task<int> QuietBootstrap(InstallationTarget target,ActivityWorkerScope scope,
        ActivityQualificationIdentity identity,Func<Task<IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow>>> ingest,
        Func<Task<int>> publishedSeriesCount)
    {
        RequireDisposable(target.Database);var checks=0;var store=new SqlActivityProcessorStore(target.ConnectionString);
        var candidate=(await store.DiscoverAcceptedWorkAsync(scope,1)).Candidate!;
        Check(candidate.IsEmpty,"quiet bootstrap proposes proof-bearing progress without an observation");
        checks+=await RejectChangedEmptyProjection(target,scope,candidate);
        checks+=await CrashEmptyBeforeCommit(target,scope);
        var first=await Start(target,scope,1);
        try { var committed=await Until(first,"committed");var next=await Until(first,"committed");Check(committed.GetProperty("verifiedNoStateCut").GetBoolean() && next.GetProperty("verifiedNoStateCut").GetBoolean() && committed.GetProperty("periods").GetInt32()==0 && next.GetProperty("periods").GetInt32()==0,"fresh OS process commits two bounded no-state frontiers across 101 quiet batches");await Until(first,"idle");
            Check((await store.ReadCheckpointAsync(identity)).NextGeneration==2,"parent independently observes durable no-state progress before termination");
            first.Process.Kill(true);await first.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await Dispose(first); }
        var empty=await store.ReadCheckpointAsync(identity);
        Check(empty.NextGeneration==2 && empty.LastConsumedSample is null && empty.OpenAnchor is null && empty.OpenState is null &&
            empty.AcceptedSource?.LastObservation is null && empty.AcceptedSource?.FrontierBatch is not null,
            "quiet bootstrap stores explicit absence instead of a fabricated sample or anchor");
        Check(await publishedSeriesCount()==0,"authorized history remains no-published-history before an actual activity observation");
        var replay=await store.ApplyAcceptedWorkAsync(candidate);
        Check(replay.Replayed && replay.NewPeriods.Count==0 && await store.ReadCheckpointAsync(identity)==empty,"quiet bootstrap receipt replays exactly without advancement");
        var rows=await ingest();var second=await Start(target,scope,1);
        try { await Until(second,"committed");await Until(second,"committed");await Until(second,"committed");await Until(second,"idle");await Stop(second); }
        finally { await Dispose(second); }
        var result=await store.ReadCheckpointAsync(identity);var page=await store.ReadPeriodPageAsync(identity,100);
        Check(result.NextGeneration==5 && page.Rows.Count==1 && page.Rows[0].Period.StartSequence==rows[0].Sequence &&
            page.Rows[0].Period.EndSequence==rows[2].Sequence && page.Rows[0].Period.DurationTicks==rows[2].ObservedAt.UtcTicks-rows[0].ObservedAt.UtcTicks,
            "a new process preserves the first real anchor across repeated state and publishes one exact interval");
        Check(await publishedSeriesCount()==1,"authorized history discovers the actual observation-backed publication");
        return checks;
        void Check(bool value,string label){if(!value)throw new InvalidOperationException("Quiet bootstrap: "+label);checks++;Console.WriteLine("PASS: quiet bootstrap "+label);}
    }

    internal static async Task<int> Run(InstallationTarget target,ActivityWorkerScope scope,ActivityQualificationIdentity identity,
        Func<int,bool,Task<IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow>>> ingest,Func<int,Task> ingestQuiet)
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
        var closed=await store.ReadCheckpointAsync(identity);var page=await store.ReadPeriodPageAsync(identity,100,new ActivityPeriodCursor(opening[0].ObservedAt.AddTicks(-1),0,0,Guid.Empty));
        var expected=new ClosedActivityPeriod(scope.MachineId,scope.StreamKey,scope.MappingId,ActivityState.Running,
            opening[0].ObservedAt,opening[2].ObservedAt,opening[2].ObservedAt.UtcTicks-opening[0].ObservedAt.UtcTicks,
            scope.InstanceId,opening[0].Sequence,opening[2].Sequence,"ACTIVE","READY");
        Check(closed.NextGeneration==open.NextGeneration+1 && page.Rows.Count(x=>x.Period==expected)==1,
            "new host process discovers and retries the rolled-back tail with exact original anchor output");
        var cross=await ingest(130,false);await Until(restarted,"committed");await Until(restarted,"committed");await Until(restarted,"idle");
        page=await store.ReadPeriodPageAsync(identity,100,new ActivityPeriodCursor(opening[0].ObservedAt.AddTicks(-1),0,0,Guid.Empty));
        var nextExpected=new ClosedActivityPeriod(scope.MachineId,scope.StreamKey,scope.MappingId,ActivityState.Running,
            cross[0].ObservedAt,cross[1].ObservedAt,cross[1].ObservedAt.UtcTicks-cross[0].ObservedAt.UtcTicks,
            scope.InstanceId,cross[0].Sequence,cross[1].Sequence,"ACTIVE","READY");
        var afterCross=await SnapshotState();
        Check(afterCross.Periods==baseline.Periods+2 && page.Rows.Count(x=>x.Period==nextExpected)==1 &&
            !page.Rows.Any(x=>x.Period.StartSequence==opening[2].Sequence && x.Period.EndSequence==cross[0].Sequence),
            "continuous host discovers later ingestion and preserves explicit cross-batch breaks");
        await Stop(restarted);
        checks+=await ActivityBoundaryAcceptance.Run(target,scope,opening[^1],cross[0]);
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
        ActivityProcessorCheckpoint priorGeneration;
        await using(var q=new SqlCommand("SELECT TOP(1) ResultBytes FROM activity.ProcessorOperation WHERE MachineId=@machine AND MappingId=@mapping ORDER BY AppliedGeneration",c))
        {q.Parameters.AddWithValue("@machine",scope.MachineId);q.Parameters.AddWithValue("@mapping",scope.MappingId);priorGeneration=JsonSerializer.Deserialize<ActivityDurableResult>((byte[])(await q.ExecuteScalarAsync())!)!.Checkpoint;}
        async Task PutGeneration(ActivityProcessorCheckpoint cp)=>await Exec("UPDATE activity.ProcessorCheckpoint SET Generation=@generation,LastInstanceId=@instance,LastSequence=@sequence,CheckpointJson=@json,CheckpointHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@json)) WHERE MachineId=@machine AND MappingId=@mapping",
            ("@generation",checked((long)cp.NextGeneration)),("@instance",(decimal)cp.LastConsumedSample!.InstanceId),("@sequence",(decimal)cp.LastConsumedSample.Sequence),
            ("@json",JsonSerializer.Serialize(cp)),("@machine",scope.MachineId),("@mapping",scope.MappingId));
        var latest=await store.ReadCheckpointAsync(identity);await PutGeneration(priorGeneration);
        try {var rewound=await Launch();await Until(rewound,"held");await rewound.Process.WaitForExitAsync();Check(rewound.Process.ExitCode==20,"a genuine older receipt cannot rewind the current worker checkpoint");}
        finally {await PutGeneration(latest);}
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
        await ingestQuiet(101);
        var emptyWork=(await store.DiscoverAcceptedWorkAsync(scope,1)).Candidate!;
        Check(emptyWork.IsEmpty,"a long quiet history produces a bounded verified no-state candidate");
        var quiet=await Launch();var firstEmpty=await Until(quiet,"committed");var secondEmpty=await Until(quiet,"committed");await Until(quiet,"idle");
        await Stop(quiet);var afterQuiet=await SnapshotState();var quietCp=await store.ReadCheckpointAsync(identity);
        Check(firstEmpty.GetProperty("verifiedNoStateCut").GetBoolean() && secondEmpty.GetProperty("verifiedNoStateCut").GetBoolean() &&
            afterQuiet.Periods==afterCross.Periods && afterQuiet.Receipts==afterCross.Receipts+2 &&
            quietCp.LastConsumedSample==checkpoint.LastConsumedSample && quietCp.OpenAnchor is null && quietCp.OpenState is null &&
            quietCp.AcceptedSource?.FrontierBatch is not null,
            "101 quiet batches advance in two atomic proof-bearing cuts without inventing an interval");
        var historical=await store.ApplyAcceptedWorkAsync(emptyWork);
        Check(historical.Replayed && historical.NewPeriods.Count==0 && await SnapshotState()==afterQuiet,
            "historical no-state operation replay returns its receipt without rewinding newer progress");
        var frontier=quietCp.AcceptedSource!.FrontierBatch!;
        byte[] originalDocument;
        await using(var q=new SqlCommand("SELECT RawDocument FROM acquisition.MtConnectBatch WHERE BatchId=@batch",c))
        {q.Parameters.AddWithValue("@batch",frontier.BatchId);originalDocument=(byte[])(await q.ExecuteScalarAsync())!;}
        async Task Document(byte[] raw)=>await Exec("UPDATE acquisition.MtConnectBatch SET RawDocument=@raw,DocumentHash=HASHBYTES('SHA2_256',@raw) WHERE BatchId=@batch",("@raw",raw),("@batch",frontier.BatchId));
        await Exec("DISABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");
        try
        {
            await Document(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(originalDocument).Replace("PartCount","ChangedCount",StringComparison.Ordinal)));
            var altered=await Launch();await Until(altered,"held");await altered.Process.WaitForExitAsync();
            Check(altered.Process.ExitCode==20 && await SnapshotState()==afterQuiet,
                "idle restart rejects an altered no-state frontier document even with a consistent SQL hash");
        }
        finally { await Document(originalDocument);await Exec("ENABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch"); }
        var quietRestart=await Launch();await Until(quietRestart,"idle");
        Check(await SnapshotState()==afterQuiet,"restart reconstructs the no-state frontier with no duplicate effects");
        await Stop(quietRestart);
        var later=await ingest(140,false);
        var observed=(await store.DiscoverAcceptedWorkAsync(scope,1)).Candidate!;
        try
        {
            await store.ApplyAcceptedWorkAsync(observed with{IsEmpty=true,ThroughSequence=later[^1].Sequence});
            throw new InvalidOperationException("Observed activity was classified as empty.");
        }
        catch(InvalidDataException){Check(await SnapshotState()==afterQuiet,"a no-state candidate cannot omit genuine activity observations");}
        var observedHost=await Launch();await Until(observedHost,"committed");await Until(observedHost,"committed");await Until(observedHost,"idle");await Stop(observedHost);
        var final=await SnapshotState();page=await store.ReadPeriodPageAsync(identity,100,new ActivityPeriodCursor(opening[0].ObservedAt.AddTicks(-1),0,0,Guid.Empty));
        Check(final.Periods==afterQuiet.Periods+1 && final.Receipts==afterQuiet.Receipts+2 && page.Rows.Count(x=>
            x.Period.StartSequence==later[0].Sequence && x.Period.EndSequence==later[1].Sequence &&
            x.Period.DurationTicks==later[1].ObservedAt.UtcTicks-later[0].ObservedAt.UtcTicks)==1,
            "later source state closes only its own qualified batch after the quiet frontier");
        afterCross=final;
        Check(children.Select(x=>x.Process.Id).Distinct().Count()==children.Count,"hosting acceptance uses distinct real process lifetimes");
        Directory.CreateDirectory("artifacts/ingestion");
        await File.WriteAllTextAsync("artifacts/ingestion/activity-host.json",JsonSerializer.Serialize(new {fixture=true,checks,
            processIds=children.Select(x=>x.Process.Id),before=baseline,after=afterCross,
            limitations="Single exact source instance/mapping; Verified no-state windows advance within source-proof bounds. No physical completeness, automatic recovery or production commissioning claim."}));
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
    static void Bind(SqlCommand q,params (string Name,object Value)[] args){foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
