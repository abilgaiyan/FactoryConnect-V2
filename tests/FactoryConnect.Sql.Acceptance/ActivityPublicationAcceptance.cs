using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

internal static class ActivityPublicationAcceptance
{
    internal sealed record Input(string ConnectionString,ActivityWorkCandidate Work,bool PauseBeforeResponse);
    internal sealed record Event(int ProcessId,string Status,ActivityDurableResult? Result=null,string? Error=null,Dictionary<string,object?>? Metrics=null,int? SqlNumber=null,string? Reason=null);
    private sealed record Child(Process Process,string App,Task<string> Errors);
    internal static async Task<int> ChildMain()
    {
        try
        {
            var input=JsonSerializer.Deserialize<Input>((await Console.In.ReadLineAsync())!)!;
            RequireDisposable(new SqlConnectionStringBuilder(input.ConnectionString).InitialCatalog);
            var metrics=new Dictionary<string,object?>();
            using var listener=new ActivityListener{ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Activity",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped=a=>{if(a.OperationName=="activity.sql.apply")foreach(var tag in a.TagObjects)metrics[tag.Key]=tag.Value;}};
            ActivitySource.AddActivityListener(listener);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Event(Environment.ProcessId,"started")));await Console.Out.FlushAsync();
            var result=await new SqlActivityProcessorStore(input.ConnectionString).ApplyAcceptedWorkAsync(input.Work);
            if(input.PauseBeforeResponse)await Console.In.ReadLineAsync();
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Event(Environment.ProcessId,"committed",result,Metrics:metrics)));await Console.Out.FlushAsync();return 0;
        }
        catch(Exception error)
        {await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Event(Environment.ProcessId,"held",Error:error.GetType().Name,
            SqlNumber:(error as SqlException)?.Number,Reason:error is InvalidDataException?error.Message:null)));return 20;}
    }
    internal static async Task<int> Run(InstallationTarget target,ActivityWorkerScope scope,ApprovedActivitySignal approved,
        Func<int,bool,bool,Task<IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow>>> ingest,
        Func<Task<ActivityWorkCandidate>> independent,Func<Task> revoke)
    {
        RequireDisposable(target.Database);var checks=0;var children=new List<Child>();var events=new List<Event>();
        var store=new SqlActivityProcessorStore(target.ConnectionString);var identity=AcceptedActivityContinuityQualifier.QualificationFor(approved,scope.PublicationProfile);
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        async Task<object?> Scalar(string sql,params (string,object)[] args)
        {await using var q=new SqlCommand(sql,c);foreach(var(n,v)in args)q.Parameters.AddWithValue(n,v);return await q.ExecuteScalarAsync();}
        async Task<string> Snapshot()=>JsonSerializer.Serialize(new{
            checkpoint=await Scalar("SELECT CheckpointJson FROM activity.ProcessorCheckpoint WHERE MachineId=@m AND MappingId=@s",("@m",scope.MachineId),("@s",scope.MappingId)),
            witnesses=await Scalar("SELECT COUNT(*) FROM activity.PublicationSourceWitness WHERE MachineId=@m",("@m",scope.MachineId)),
            ancestry=await Scalar("SELECT COUNT(*) FROM activity.PublicationReceiptAncestry WHERE MachineId=@m",("@m",scope.MachineId)),
            periods=await Scalar("SELECT COUNT(*) FROM activity.ClosedPeriod WHERE MachineId=@m",("@m",scope.MachineId)),
            receipts=await Scalar("SELECT COUNT(*) FROM activity.ProcessorOperation WHERE MachineId=@m",("@m",scope.MachineId))});
        void Check(bool pass,string name){if(!pass)throw new InvalidOperationException(name);checks++;Console.WriteLine("PASS: publication "+name);}
        async Task Reject(Func<Task> action,string name)
        {try{await action();}catch(Exception e)when(e is InvalidDataException or InvalidOperationException or SqlException or FactoryConnect.Sql.Security.SecurityDeniedException)
            {if(e is InvalidOperationException && e.Message=="negative fixture did not reject")throw;Check(true,name);return;}throw new InvalidOperationException("negative fixture did not reject");}
        async Task<ActivityWorkCandidate> Work(int take=100)=>(await store.DiscoverAcceptedWorkAsync(scope,take)).Candidate??throw new InvalidOperationException("Missing publication candidate.");
        async Task Wait(Func<Task<bool>> probe,string label)
        {var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromSeconds(12)){if(await probe())return;await Task.Delay(20);}throw new InvalidOperationException("Timed out: "+label);}
        async Task<Child> Start(ActivityWorkCandidate work,bool pause=false)
        {
            var app="fc2-publish-child-"+Guid.NewGuid().ToString("N");var cs=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=app};
            var info=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);info.ArgumentList.Add("--publication-child");
            var p=Process.Start(info)!;var child=new Child(p,app,p.StandardError.ReadToEndAsync());children.Add(child);
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Input(cs.ConnectionString,work,pause)));await p.StandardInput.FlushAsync();
            var started=await Read(child);Check(started.Status=="started","new OS process starts with explicit publication work");return child;
        }
        async Task<Event> Read(Child child)
        {var line=await child.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));var e=line is null?null:JsonSerializer.Deserialize<Event>(line);
            if(e is null || e.ProcessId!=child.Process.Id)throw new InvalidOperationException("No process-bound publication event.");
            if(e.Status=="held")Console.WriteLine("HOLD: publication child "+JsonSerializer.Serialize(e));events.Add(e);return e;}
        async Task<ActivityDurableResult> Committed(Child child)
        {var e=await Read(child);await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Check(e.Status=="committed" && e.Result is not null && child.Process.ExitCode==0,"child commits or retrieves exact immutable publication");return e.Result!;}
        async Task Kill(Child child)
        {Check(!child.Process.HasExited,"terminate a live publishing process");child.Process.Kill(true);await child.Process.WaitForExitAsync();}
        async Task Drain(Child child)=>await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name=@app",("@app",child.App)))==0,"terminated SQL sessions drain");
        async Task<(SqlConnection,SqlTransaction)> Gate(string resource)
        {var holder=new SqlConnection(target.ConnectionString);await holder.OpenAsync();var tx=(SqlTransaction)await holder.BeginTransactionAsync();await using var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@rsc,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; IF @r<0 THROW 51098,'Fixture gate unavailable.',1;",holder,tx);q.Parameters.AddWithValue("@rsc",resource);await q.ExecuteNonQueryAsync();return(holder,tx);}
        async Task WaitProcessor(Child child)=>await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode='X'",("@app",child.App)))==1,"publication passed preflight and waits on processor");
        try
        {
            var opening=await ingest(10,true,false);var first=await Work();
            var firstChild=await Start(first,pause:true);
            await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM activity.ProcessorOperation WHERE OperationId=@op",("@op",first.OperationId)))==1,"commit before response");
            var cp=await store.ReadCheckpointAsync(identity);
            Check(cp.NextGeneration==1 && cp.OpenAnchor?.Sequence==opening[0].Sequence && cp.AcceptedSource?.PublicationAncestryJson is not null,"parent observes durable new-policy proof and original anchor before response");
            await Kill(firstChild);await Drain(firstChild);var firstReplay=await Committed(await Start(first));
            Check(firstReplay.Replayed && firstReplay.Checkpoint==cp,"post-commit pre-response death replays exactly");
            var other=await independent();var second=await ingest(20,true,false);var secondWork=await Work();
            var machineGate=SqlActivityProcessorStore.ProcessorLockResource(scope.MachineId,scope.MappingId,scope.StreamKey);
            var (holder,tx)=await Gate(machineGate);Child secondChild;
            await using(holder)await using(tx)
            {
                secondChild=await Start(secondWork);await WaitProcessor(secondChild);
                var otherResult=await store.ApplyAcceptedWorkAsync(other).WaitAsync(TimeSpan.FromSeconds(8));
                Check(otherResult.Checkpoint.Qualification.MachineId!=scope.MachineId && otherResult.NewPeriods.Count==1 && (await store.ReadCheckpointAsync(identity))==cp,
                    "independent eligible machine publishes while this processor waits");await tx.RollbackAsync();
            }
            var continued=await Committed(secondChild);
            Check(continued.NewPeriods.Count==0 && continued.Checkpoint.OpenAnchor?.Sequence==opening[0].Sequence &&
                continued.Checkpoint.LastConsumedSample?.Sequence==second[^1].Sequence,"new process preserves original anchor across a verified acquisition boundary");
            var closing=await ingest(30,false,false);var close=await Work();var beforeClose=await Snapshot();
            await Reject(()=>store.ApplyAcceptedWorkAsync(close with{Scope=scope with{InstanceId=scope.InstanceId+1}}),"changed source instance rejects");
            await Reject(()=>store.ApplyAcceptedWorkAsync(close with{Scope=scope with{MappingId=Guid.NewGuid()}}),"changed mapping rejects");
            var originalRequest=(byte[])(await Scalar("SELECT RequestBytes FROM security.SecurityOperation WHERE OperationId=@op",("@op",closing[0].BatchId)))!;
            await Scalar("DISABLE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation");
            try
            {
                var node=JsonNode.Parse(originalRequest)!;node["ExpectedBatchId"]=Guid.NewGuid();var altered=Encoding.UTF8.GetBytes(node.ToJsonString());
                await Scalar("UPDATE security.SecurityOperation SET RequestBytes=@b,RequestHash=HASHBYTES('SHA2_256',@b) WHERE OperationId=@op",("@b",altered),("@op",closing[0].BatchId));
                await Reject(()=>store.ApplyAcceptedWorkAsync(close),"checksum-consistent unrelated ingestion predecessor cannot publish");
                await Scalar("UPDATE security.SecurityOperation SET RequestBytes=@b,RequestHash=HASHBYTES('SHA2_256',@b),ScopeKind='S',MachineId=NULL WHERE OperationId=@op",("@b",originalRequest),("@op",closing[0].BatchId));
                await Reject(()=>store.ApplyAcceptedWorkAsync(close),"missing exact-scope immutable ingestion receipt rejects");
            }
            finally
            {await Scalar("UPDATE security.SecurityOperation SET RequestBytes=@b,RequestHash=HASHBYTES('SHA2_256',@b),ScopeKind='M',MachineId=@m WHERE OperationId=@op",("@b",originalRequest),("@m",scope.MachineId),("@op",closing[0].BatchId));await Scalar("ENABLE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation");}
            var raw=(byte[])(await Scalar("SELECT RawDocument FROM acquisition.MtConnectBatch WHERE BatchId=@op",("@op",closing[0].BatchId)))!;
            await Scalar("DISABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");
            try
            {
                var changed=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw).Replace("ACTIVE","ALTERED",StringComparison.Ordinal));
                await Scalar("UPDATE acquisition.MtConnectBatch SET RawDocument=@b,DocumentHash=HASHBYTES('SHA2_256',@b) WHERE BatchId=@op",("@b",changed),("@op",closing[0].BatchId));
                await Reject(()=>store.ApplyAcceptedWorkAsync(close),"altered original document with consistent SQL hash rejects");
            }
            finally
            {await Scalar("UPDATE acquisition.MtConnectBatch SET RawDocument=@b,DocumentHash=HASHBYTES('SHA2_256',@b) WHERE BatchId=@op",("@b",raw),("@op",closing[0].BatchId));await Scalar("ENABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");}
            Check(await Snapshot()==beforeClose,"all provenance negatives leave checkpoint periods and operation receipts unchanged");
            var (mutationHolder,mutationTx)=await Gate(machineGate);
            await using(mutationHolder)await using(mutationTx)
            {
                var waiting=await Start(close);await WaitProcessor(waiting);
                await Scalar("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
                try
                {
                    await Scalar("UPDATE acquisition.MtConnectObservation SET RawValue=N'changed-after-preflight' WHERE MachineId=@m AND InstanceId=@instance AND Sequence=@sequence",
                        ("@m",scope.MachineId),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)closing[0].Sequence));
                    await mutationTx.RollbackAsync();var held=await Read(waiting);await waiting.Process.WaitForExitAsync();
                    Check(held.Status=="held" && waiting.Process.ExitCode==20,"projection changed after observed processor wait rejects inside publisher");
                }
                finally
                {
                    if(!waiting.Process.HasExited){waiting.Process.Kill(true);await waiting.Process.WaitForExitAsync();}
                    await Scalar("UPDATE acquisition.MtConnectObservation SET RawValue=@value WHERE MachineId=@m AND InstanceId=@instance AND Sequence=@sequence",
                        ("@value",closing[0].RawValue),("@m",scope.MachineId),("@instance",(decimal)scope.InstanceId),("@sequence",(decimal)closing[0].Sequence));
                    await Scalar("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
                }
            }
            Check(await Snapshot()==beforeClose,"post-wait source rejection publishes no partial effects");
            var barrier="fc2-publish-before-"+Guid.NewGuid().ToString("N");
            await Scalar($"CREATE TRIGGER activity.Fixture_PublicationCommit ON activity.ClosedPeriod AFTER INSERT AS BEGIN DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'{barrier}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=30000; IF @r<0 THROW 51098,'Fixture gate unavailable.',1; END");
            Child blocked;
            try
            {
                var (gate,transaction)=await Gate(barrier);await using(gate)await using(transaction)
                {
                    blocked=await Start(close);await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode='S'",("@app",blocked.App)))==1,"period insertion before atomic commit");
                    await Kill(blocked);await transaction.RollbackAsync();
                }
                await Drain(blocked);Check(await Snapshot()==beforeClose,"pre-commit process death rolls back checkpoint proof receipts and periods together");
            }
            finally {await Scalar("DROP TRIGGER IF EXISTS activity.Fixture_PublicationCommit");}
            var committedNoResponse=await Start(close,pause:true);
            await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM activity.ProcessorOperation WHERE OperationId=@op",("@op",close.OperationId)))==1,"cross-batch commit before response");
            var closed=await store.ReadCheckpointAsync(identity);await Kill(committedNoResponse);await Drain(committedNoResponse);
            var result=await Committed(await Start(close));
            var exact=new ClosedActivityPeriod(scope.MachineId,scope.StreamKey,scope.MappingId,ActivityState.Running,opening[0].ObservedAt,closing[^1].ObservedAt,
                closing[^1].ObservedAt.UtcTicks-opening[0].ObservedAt.UtcTicks,scope.InstanceId,opening[0].Sequence,closing[^1].Sequence,"ACTIVE","READY");
            Check(result.Replayed && result.Checkpoint==closed && result.NewPeriods.Count==1 && result.NewPeriods[0]==exact &&
                (await store.ReadPeriodPageAsync(identity)).Rows.Count==1,"verified state spans three acquisition batches and process lifetimes as exactly one durable period");
            await Reject(()=>store.ApplyAcceptedWorkAsync(close with{ThroughSequence=closing[0].Sequence}),"changed request cannot reuse committed operation identity");
            await Reject(()=>store.ApplyAcceptedWorkAsync(close with{OperationId=Guid.NewGuid()}),"different operation cannot publish an already-consumed generation");
            var beforeReplay=await Snapshot();
            await using(var replayHolder=new SqlConnection(target.ConnectionString))
            {
                await replayHolder.OpenAsync();await using var replayTx=(SqlTransaction)await replayHolder.BeginTransactionAsync();
                int session;await using(var q=new SqlCommand("SELECT @@SPID; SELECT RequestHash FROM activity.ProcessorOperation WITH(ROWLOCK,XLOCK,HOLDLOCK) WHERE OperationId=@op",replayHolder,replayTx))
                {q.Parameters.AddWithValue("@op",close.OperationId);await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();session=r.GetInt16(0);await r.NextResultAsync();await r.ReadAsync();}
                var retry=await Start(close);
                await Wait(async()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) text WHERE s.program_name=@app AND r.blocking_session_id=@holder AND r.wait_type LIKE 'LCK%' AND text.text LIKE '%SELECT RequestHash,RequestBytes,ResultBytes FROM activity.ProcessorOperation%'",("@app",retry.App),("@holder",session)))==1,"exact replay receipt retrieval is genuinely waiting");
                await Kill(retry);await replayTx.RollbackAsync();await Drain(retry);
            }
            Check(await Snapshot()==beforeReplay,"termination during exact receipt replay leaves durable history unchanged");
            var later=await ingest(40,false,false);var laterWork=await Work();
            var (raceHolder,raceTx)=await Gate(machineGate);Child contenderOne;Child contenderTwo;
            await using(raceHolder)await using(raceTx)
            {
                contenderOne=await Start(laterWork,pause:true);contenderTwo=await Start(laterWork);
                await WaitProcessor(contenderOne);await WaitProcessor(contenderTwo);await raceTx.RollbackAsync();
            }
            await Committed(contenderTwo);await Kill(contenderOne);await Drain(contenderOne);
            var laterReplay=await Committed(await Start(laterWork));
            Check(laterReplay.Replayed && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM activity.ProcessorOperation WHERE OperationId=@op",("@op",laterWork.OperationId)))==1,
                "two actual concurrent writer processes serialize to one operation and restart replays after later-batch termination");
            var latest=await store.ReadCheckpointAsync(identity);var afterLater=await Snapshot();var old=await Committed(await Start(close));
            Check(old.Replayed && old.Checkpoint==closed && await Snapshot()==afterLater,"historical cross-batch replay after later input returns original receipt without rewinding current checkpoint");
            await Reject(()=>store.DiscoverAcceptedWorkAsync(scope with{PublicationProfile=ActivityPublicationPolicy.Legacy}),"legacy worker cannot adopt new-policy checkpoint");
            var countBefore=(await store.ReadPeriodPageAsync(identity)).Rows.Count;
            for(var i=0;i<101;i++)await ingest(0,false,true);
            // The worker API itself caps a discovery cut at 100. A request for
            // 101 is invalid; never treat this as a genuine 101-witness operation.
            try
            {
                await Work(101);
                throw new InvalidOperationException("oversized discovery unexpectedly succeeded");
            }
            catch(ArgumentException)
            {
                Check(true,"101-request discovery rejects above the explicit 100-row worker bound");
            }
            var quietOne=await Work();await ActivityHostAcceptance.DrainPublication(target,scope,2);var quietResult=await store.ApplyAcceptedWorkAsync(quietOne);
            // Inspect genuine immutable witness rows produced by the normal publisher,
            // rather than treating the separate receipt-selection bound as witness proof.
            await using(var witnessCounts=new SqlCommand("""
                SELECT p.OperationId,COUNT_BIG(w.BatchId) AS WitnessCount
                FROM activity.ProcessorOperation p
                JOIN activity.PublicationSourceWitness w ON w.OperationId=p.OperationId
                WHERE p.MachineId=@machine AND p.MappingId=@mapping
                GROUP BY p.OperationId
                ORDER BY WitnessCount DESC
                """,c))
            {
                witnessCounts.Parameters.AddWithValue("@machine",scope.MachineId);
                witnessCounts.Parameters.AddWithValue("@mapping",scope.MappingId);
                var maximum=0L;
                await using var reader=await witnessCounts.ExecuteReaderAsync();
                while(await reader.ReadAsync())
                {
                    var count=reader.GetInt64(1);
                    maximum=Math.Max(maximum,count);
                    Check(count<=100,"normal publication respects the 100-witness worker cut bound");
                }
                Console.WriteLine($"MEASURE: publication maximum retained source witnesses per operation={maximum}");
            }
            var quietCheckpoint=await store.ReadCheckpointAsync(identity);
            Check(quietOne.IsEmpty && quietResult.Replayed && quietResult.NewPeriods.Count==0 && quietCheckpoint.OpenAnchor is null &&
                (await store.ReadPeriodPageAsync(identity)).Rows.Count==countBefore && (await store.DiscoverAcceptedWorkAsync(scope)).Candidate is null,
                "101 verified quiet batches advance two bounded proof-bearing cuts without fabricated periods or retained anchor");
            var afterQuiet=await ingest(60,false,false);await Committed(await Start(await Work()));
            var page=await store.ReadPeriodPageAsync(identity);Check(!page.Rows.Any(x=>x.Period.StartSequence==later[^1].Sequence && x.Period.EndSequence==afterQuiet[0].Sequence),"quiet history never silently bridges old and new state observations");
            ClosedActivityPeriod Expected(ActivityState state,SqlAcceptedActivityEvidenceReader.AcceptedRow from,SqlAcceptedActivityEvidenceReader.AcceptedRow to)
                =>new(scope.MachineId,scope.StreamKey,scope.MappingId,state,from.ObservedAt,to.ObservedAt,to.ObservedAt.UtcTicks-from.ObservedAt.UtcTicks,
                    scope.InstanceId,from.Sequence,to.Sequence,from.RawValue,to.RawValue);
            var oracle=new[]{exact,Expected(ActivityState.Idle,closing[^1],later[0]),Expected(ActivityState.Running,later[0],later[^1]),
                Expected(ActivityState.Running,afterQuiet[0],afterQuiet[^1])};
            Check(page.Rows.Select(x=>x.Period).SequenceEqual(oracle) && page.Rows.All(x=>x.Qualification==identity),
                "complete durable history equals the exact four-period oracle after crashes concurrent writers replay and quiet cuts");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('activity.PublicationReceiptAncestry') AND is_disabled=0 AND is_not_trusted=0"))==2,
                "complete ancestry links use enabled trusted SQL foreign keys");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('activity.PublicationSourceWitness') AND is_disabled=0 AND is_not_trusted=0"))==3,
                "all retained source generations have trusted receipt document and scope foreign keys");
            var beforeAncestry=await Snapshot();
            await Scalar("DISABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation; DISABLE TRIGGER activity.TR_PublicationReceiptAncestryImmutable ON activity.PublicationReceiptAncestry");
            try
            {
                async Task ConstraintReject(string sql,string label)
                {
                    try{await Scalar(sql,("@op",first.OperationId));}
                    catch(SqlException e)when(e.Number==547){Check(true,label);return;}
                    throw new InvalidOperationException("Earlier-generation ancestry substitution was not rejected.");
                }
                await ConstraintReject("UPDATE activity.ProcessorOperation SET RequestBytes=RequestBytes+0x20,RequestHash=HASHBYTES('SHA2_256',RequestBytes+0x20) WHERE OperationId=@op",
                    "earliest-generation checksum-consistent request substitution is rejected by receipt ancestry FK");
                await ConstraintReject("UPDATE activity.ProcessorOperation SET ResultBytes=ResultBytes+0x20,ResultHash=HASHBYTES('SHA2_256',ResultBytes+0x20) WHERE OperationId=@op",
                    "earliest-generation checksum-consistent result substitution is rejected by receipt ancestry FK");
                await ConstraintReject("DELETE activity.ProcessorOperation WHERE OperationId=@op","earliest retained operation cannot disappear from the complete ancestry chain");
                await ConstraintReject("DELETE activity.PublicationReceiptAncestry WHERE OperationId=@op","earliest ancestry node cannot be removed while later generations exist");
                await ConstraintReject("UPDATE activity.PublicationReceiptAncestry SET ProofHash=CONVERT(binary(32),0x00) WHERE OperationId=@op",
                    "earlier proof identity cannot be rebound underneath later ancestry generations");
            }
            finally {await Scalar("ENABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation; ENABLE TRIGGER activity.TR_PublicationReceiptAncestryImmutable ON activity.PublicationReceiptAncestry");}
            await Scalar("DISABLE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation; DISABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");
            try
            {
                async Task SourceConstraintReject(string sql,string label)
                {
                    try{await Scalar(sql,("@batch",opening[0].BatchId));}
                    catch(SqlException e)when(e.Number==547){Check(true,label);return;}
                    throw new InvalidOperationException("Earlier retained source substitution was not rejected.");
                }
                await SourceConstraintReject("UPDATE security.SecurityOperation SET RequestBytes=RequestBytes+0x20,RequestHash=HASHBYTES('SHA2_256',RequestBytes+0x20) WHERE OperationId=@batch",
                    "earliest ingestion receipt cannot be checksum-consistently substituted after its anchor is closed");
                await SourceConstraintReject("UPDATE acquisition.MtConnectBatch SET RawDocument=RawDocument+0x20,DocumentHash=HASHBYTES('SHA2_256',RawDocument+0x20) WHERE BatchId=@batch",
                    "earliest original source document cannot be checksum-consistently substituted beneath later publication");
            }
            finally {await Scalar("ENABLE TRIGGER security.TR_Operation_Immutable ON security.SecurityOperation; ENABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");}
            Check(await Snapshot()==beforeAncestry && (await store.DiscoverAcceptedWorkAsync(scope)).Candidate is null,
                "all earlier-generation attacks preserve complete history and safe bounded resume");
            // Exercise the exact FC2-040 read guard against transaction-local corruption.
            // Every attack is rolled back before the next fixture; no production SQL is touched.
            var verifyRead=typeof(FactoryConnect.Sql.Security.SqlSecurityAuthority).GetMethod(
                "VerifyActivityPublicationReceiptAsync",BindingFlags.NonPublic|BindingFlags.Static)
                ?? throw new InvalidOperationException("Activity publication read verifier missing.");
            // Exercise the actual read verifier against a genuine 100-witness
            // publication, not only against the writer's witness-count query.
            var hundredWitnessOperation=await Scalar("""
                SELECT TOP(1) w.OperationId
                FROM activity.PublicationSourceWitness w
                JOIN activity.ProcessorOperation p ON p.OperationId=w.OperationId
                WHERE p.MachineId=@m AND p.MappingId=@s
                GROUP BY w.OperationId
                HAVING COUNT_BIG(*)=100
                ORDER BY w.OperationId
                """,("@m",scope.MachineId),("@s",scope.MappingId));
            Check(hundredWitnessOperation is Guid,
                "normal publisher produces a genuine exactly-100-witness operation");
            await using(var verifiedConnection=new SqlConnection(target.ConnectionString))
            {
                await verifiedConnection.OpenAsync();
                await using var verifiedTx=(SqlTransaction)await verifiedConnection.BeginTransactionAsync(IsolationLevel.Serializable);
                await ((Task)verifyRead.Invoke(null,new object[]{
                    verifiedConnection,verifiedTx,(Guid)hundredWitnessOperation!,identity,CancellationToken.None})!);
                await verifiedTx.RollbackAsync();
                Check(true,"FC2-040 reader verifies a genuine 100-witness publication");
            }
            // Corruption-only boundary: two otherwise retained source witnesses are
            // temporarily rebound to the genuine 100-witness operation. This is
            // deliberately NOT presented as a legitimate publisher output.
            // The SQL guard must reject 102 rows before materializing receipt bytes.
            await using(var overflowConnection=new SqlConnection(target.ConnectionString))
            {
                await overflowConnection.OpenAsync();
                await using var overflowTx=(SqlTransaction)await overflowConnection.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    await using(var mutate=new SqlCommand("""
                        ALTER TABLE activity.PublicationSourceWitness NOCHECK CONSTRAINT FK_PublicationWitness_Owner;
                        DISABLE TRIGGER activity.TR_PublicationSourceWitnessImmutable ON activity.PublicationSourceWitness;
                        ;WITH extra AS (
                            SELECT TOP(2) OperationId
                            FROM activity.PublicationSourceWitness
                            WHERE MachineId=@machine AND OperationId<>@op
                            ORDER BY OperationId,BatchId
                        )
                        SELECT COUNT_BIG(*) FROM extra;
                        """,overflowConnection,overflowTx))
                    {
                        mutate.Parameters.AddWithValue("@machine",scope.MachineId);
                        mutate.Parameters.AddWithValue("@op",(Guid)hundredWitnessOperation!);
                        var available=Convert.ToInt64(await mutate.ExecuteScalarAsync());
                        Check(available==2,"two distinct retained witnesses available for defensive overflow fixture");
                    }
                    await using(var rebind=new SqlCommand("""
                        ;WITH extra AS (
                            SELECT TOP(2) OperationId,BatchId
                            FROM activity.PublicationSourceWitness
                            WHERE MachineId=@machine AND OperationId<>@op
                            ORDER BY OperationId,BatchId
                        )
                        UPDATE w SET OperationId=@op
                        FROM activity.PublicationSourceWitness w
                        JOIN extra x ON x.OperationId=w.OperationId AND x.BatchId=w.BatchId;
                        SELECT COUNT_BIG(*) FROM activity.PublicationSourceWitness WHERE OperationId=@op;
                        """,overflowConnection,overflowTx))
                    {
                        rebind.Parameters.AddWithValue("@machine",scope.MachineId);
                        rebind.Parameters.AddWithValue("@op",(Guid)hundredWitnessOperation!);
                        var count=Convert.ToInt64(await rebind.ExecuteScalarAsync());
                        Check(count==102,"corruption fixture contains exactly 102 source witnesses in one receipt");
                    }
                    try
                    {
                        await ((Task)verifyRead.Invoke(null,new object[]{
                            overflowConnection,overflowTx,(Guid)hundredWitnessOperation!,identity,CancellationToken.None})!);
                        throw new InvalidOperationException("102-witness SQL pre-read guard accepted corrupted receipt.");
                    }
                    catch(SqlException e)when(e.Number==51091)
                    {
                        Check(true,"FC2-040 SQL pre-read rejects 102 witnesses with error 51091");
                    }
                }
                finally {await overflowTx.RollbackAsync();}
            }
            var latestOperation=(Guid)(await Scalar(
                "SELECT TOP(1) OperationId FROM activity.PublicationReceiptAncestry WHERE MachineId=@m AND MappingId=@s ORDER BY Generation DESC",
                ("@m",scope.MachineId),("@s",scope.MappingId)) ?? throw new InvalidOperationException("No publication ancestry."));
            async Task RejectCorruptRead(string mutation,string label,Guid? operation=null)
            {
                await using var tamper=new SqlConnection(target.ConnectionString);
                await tamper.OpenAsync();
                await using var tx=(SqlTransaction)await tamper.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    await using(var command=new SqlCommand(mutation,tamper,tx))
                    {
                        command.Parameters.AddWithValue("@op",operation??latestOperation);
                        await command.ExecuteNonQueryAsync();
                    }
                    try
                    {
                        await ((Task)verifyRead.Invoke(null,new object[]{tamper,tx,operation??latestOperation,identity,CancellationToken.None})!);
                    }
                    catch(InvalidDataException)
                    {
                        Check(true,label);return;
                    }
                    throw new InvalidOperationException("Corrupt publication was accepted: "+label);
                }
                finally {await tx.RollbackAsync();}
            }
            await RejectCorruptRead(
                "DISABLE TRIGGER activity.TR_PublicationSourceWitnessImmutable ON activity.PublicationSourceWitness; "+
                "DELETE TOP(1) FROM activity.PublicationSourceWitness WHERE OperationId=@op",
                "FC2-040 read rejects a missing retained source witness");
            await RejectCorruptRead(
                "DISABLE TRIGGER activity.TR_PublicationSourceWitnessImmutable ON activity.PublicationSourceWitness; "+
                "DELETE TOP(1) FROM activity.PublicationSourceWitness WHERE OperationId=@op",
                "FC2-040 reader rejects one missing witness from genuine 100-witness operation",
                (Guid)hundredWitnessOperation!);
            await RejectCorruptRead(
                "ALTER TABLE activity.PublicationSourceWitness NOCHECK CONSTRAINT FK_PublicationWitness_Owner; "+
                "DISABLE TRIGGER activity.TR_PublicationReceiptAncestryImmutable ON activity.PublicationReceiptAncestry; "+
                "DELETE FROM activity.PublicationReceiptAncestry WHERE OperationId=@op",
                "FC2-040 read rejects missing latest publication ancestry");
            await RejectCorruptRead(
                "ALTER TABLE activity.ProcessorOperation NOCHECK CONSTRAINT CK_ActivityOperationRequestHash; "+
                "DISABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation; "+
                "UPDATE activity.ProcessorOperation SET RequestBytes=RequestBytes+0x20 WHERE OperationId=@op",
                "FC2-040 read rejects publication request bytes inconsistent with stored request hash");
            await RejectCorruptRead(
                "ALTER TABLE activity.PublicationSourceWitness NOCHECK CONSTRAINT FK_PublicationWitness_Document; "+
                "DISABLE TRIGGER activity.TR_PublicationSourceWitnessImmutable ON activity.PublicationSourceWitness; "+
                "UPDATE activity.PublicationSourceWitness SET DocumentHash=0x0000000000000000000000000000000000000000000000000000000000000000 "+
                "WHERE OperationId=@op AND BatchId=(SELECT TOP(1) BatchId FROM activity.PublicationSourceWitness WHERE OperationId=@op ORDER BY BatchId)",
                "FC2-040 read rejects retained source document witness hash substitution");
            // An oversized request must be rejected by the SQL pre-read guard, not by
            // a later JSON/hash check after materializing the oversized payload.
            await using(var oversized=new SqlConnection(target.ConnectionString))
            {
                await oversized.OpenAsync();
                await using var oversizedTx=(SqlTransaction)await oversized.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    await using(var mutate=new SqlCommand(
                        "ALTER TABLE activity.ProcessorOperation NOCHECK CONSTRAINT CK_ActivityOperationRequestHash; "+
                        "DISABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation; "+
                        "UPDATE activity.ProcessorOperation SET RequestBytes=CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'X'),8388609)) WHERE OperationId=@op",
                        oversized,oversizedTx))
                    {
                        mutate.Parameters.AddWithValue("@op",latestOperation);
                        await mutate.ExecuteNonQueryAsync();
                    }
                    try
                    {
                        await ((Task)verifyRead.Invoke(null,new object[]{oversized,oversizedTx,latestOperation,identity,CancellationToken.None})!);
                        throw new InvalidOperationException("Oversized publication request was accepted.");
                    }
                    catch(SqlException e)when(e.Number==51091)
                    {
                        Check(true,"FC2-040 SQL pre-read guard rejects oversized publication request with 51091");
                    }
                }
                finally {await oversizedTx.RollbackAsync();}
            }
            // Gate 5 negative fixture: two individually legal receipts exceed the
            // aggregate budget before the verifier can materialize their payloads.
            await using(var aggregate=new SqlConnection(target.ConnectionString))
            {
                await aggregate.OpenAsync();
                await using var aggregateTx=(SqlTransaction)await aggregate.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    var operationIds=new List<Guid>();
                    await using(var ids=new SqlCommand("""
                        SELECT TOP (2) OperationId
                        FROM activity.ProcessorOperation
                        WHERE MachineId=@machine AND MappingId=@mapping
                        ORDER BY AppliedGeneration DESC
                        """,aggregate,aggregateTx))
                    {
                        ids.Parameters.AddWithValue("@machine",scope.MachineId);
                        ids.Parameters.AddWithValue("@mapping",scope.MappingId);
                        await using var reader=await ids.ExecuteReaderAsync();
                        while(await reader.ReadAsync())operationIds.Add(reader.GetGuid(0));
                    }
                    Check(operationIds.Count==2,"Gate 5 aggregate fixture has two distinct published operations");
                    await using(var mutate=new SqlCommand("""
                        ALTER TABLE activity.ProcessorOperation NOCHECK CONSTRAINT CK_ActivityOperationRequestHash;
                        DISABLE TRIGGER activity.TR_ProcessorOperationImmutable ON activity.ProcessorOperation;
                        UPDATE activity.ProcessorOperation
                        SET RequestBytes=CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'X'),5000000))
                        WHERE OperationId IN (@first,@second);
                        """,aggregate,aggregateTx))
                    {
                        mutate.Parameters.AddWithValue("@first",operationIds[0]);
                        mutate.Parameters.AddWithValue("@second",operationIds[1]);
                        await mutate.ExecuteNonQueryAsync();
                    }
                    var authorityType=typeof(FactoryConnect.Sql.Security.SqlSecurityAuthority);
                    var inputType=authorityType.GetNestedType("PublicationReadInput",BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Batched receipt input type missing.");
                    var batchMethod=authorityType.GetMethod("VerifyActivityPublicationReceiptsAsync",
                        BindingFlags.NonPublic|BindingFlags.Static)
                        ?? throw new InvalidOperationException("Batched receipt verifier missing.");
                    var inputs=Array.CreateInstance(inputType,2);
                    for(var index=0;index<2;index++)
                        inputs.SetValue(Activator.CreateInstance(inputType,
                            operationIds[index],identity,null),index);
                    try
                    {
                        await ((Task)batchMethod.Invoke(null,new object[]{
                            aggregate,aggregateTx,inputs,CancellationToken.None})!);
                        throw new InvalidOperationException("Aggregate publication byte budget was bypassed.");
                    }
                    catch(SqlException e)when(e.Number==51091)
                    {
                        Check(true,"Gate 5 rejects aggregate receipt payload over 8 MiB before materialization");
                    }
                }
                finally {await aggregateTx.RollbackAsync();}
            }
            // Legacy v2/v3 deliberately have no v4 ancestry/witness profile.
            // They must nevertheless reject v4-only generation/result-hash fields.
            var authorityTypeForLegacy=typeof(FactoryConnect.Sql.Security.SqlSecurityAuthority);
            var legacyRowType=authorityTypeForLegacy.GetNestedType("PublicationReadRow",BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Publication read row type missing.");
            var legacyWitnessType=authorityTypeForLegacy.GetNestedType("PublicationWitness",BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Publication witness type missing.");
            var legacyVerifier=authorityTypeForLegacy.GetMethod("VerifyActivityPublicationReceiptInMemory",
                BindingFlags.NonPublic|BindingFlags.Static)
                ?? throw new InvalidOperationException("Production in-memory receipt verifier missing.");
            var legacyOperation=Guid.NewGuid();
            var legacyRequest=JsonSerializer.SerializeToUtf8Bytes(new {version=3});
            var legacyHash=SHA256.HashData(legacyRequest);
            var emptyWitnesses=Array.CreateInstance(legacyWitnessType,0);
            // Historical v2 and v3 receipts have no generation/result hash or
            // source-witness ancestry. Keep their intentional read compatibility.
            foreach(var legacyVersion in new[]{2,3})
            {
                var compatibleRequest=JsonSerializer.SerializeToUtf8Bytes(new {version=legacyVersion});
                var compatibleRow=Activator.CreateInstance(legacyRowType,
                    compatibleRequest,SHA256.HashData(compatibleRequest),Array.Empty<byte>(),
                    null,null,null,null,null,null,null,null)
                    ?? throw new InvalidOperationException("Could not construct compatible legacy receipt.");
                legacyVerifier.Invoke(null,new object[]{legacyOperation,identity,compatibleRow,emptyWitnesses});
                Check(true,"Legacy version-"+legacyVersion+" receipt remains readable without v4 ancestry");
            }
            // Unknown versions must never silently acquire legacy privileges.
            foreach(var unsupportedVersion in new[]{1,5})
            {
                var unsupportedRequest=JsonSerializer.SerializeToUtf8Bytes(new {version=unsupportedVersion});
                var unsupportedRow=Activator.CreateInstance(legacyRowType,
                    unsupportedRequest,SHA256.HashData(unsupportedRequest),Array.Empty<byte>(),
                    null,null,null,null,null,null,null,null)
                    ?? throw new InvalidOperationException("Could not construct unsupported receipt.");
                try
                {
                    legacyVerifier.Invoke(null,new object[]{legacyOperation,identity,unsupportedRow,emptyWitnesses});
                    throw new InvalidOperationException("Unsupported receipt version was accepted.");
                }
                catch(TargetInvocationException e)when(e.InnerException is InvalidDataException)
                {
                    Check(true,"Unsupported publication receipt version "+unsupportedVersion+" fails closed");
                }
            }
            foreach(var caseName in new[]{"generation","result-hash"})
            {
                var legacyRow=Activator.CreateInstance(legacyRowType,
                    legacyRequest,legacyHash,Array.Empty<byte>(),
                    caseName=="result-hash"?SHA256.HashData(Array.Empty<byte>()):null,
                    caseName=="generation"?(long?)1:null,
                    null,null,null,null,null,null)
                    ?? throw new InvalidOperationException("Could not construct legacy receipt fixture.");
                try
                {
                    legacyVerifier.Invoke(null,new object[]{legacyOperation,identity,legacyRow,emptyWitnesses});
                    throw new InvalidOperationException("Legacy receipt accepted v4-only "+caseName);
                }
                catch(TargetInvocationException e)when(e.InnerException is InvalidDataException)
                {
                    Check(true,"Legacy version-3 receipt rejects v4-only "+caseName);
                }
            }
            // Gate 5: malformed batch inputs must fail before any SQL query executes.
            var batchedVerifier=typeof(FactoryConnect.Sql.Security.SqlSecurityAuthority).GetMethod(
                "VerifyActivityPublicationReceiptsAsync",BindingFlags.NonPublic|BindingFlags.Static)
                ?? throw new InvalidOperationException("Batched publication verifier missing.");
            var batchedInputType=typeof(FactoryConnect.Sql.Security.SqlSecurityAuthority).GetNestedType(
                "PublicationReadInput",BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Batched publication input type missing.");
            var repeatedInputs=Array.CreateInstance(batchedInputType,2);
            for(var index=0;index<2;index++)
                repeatedInputs.SetValue(Activator.CreateInstance(batchedInputType,
                    latestOperation,identity,null),index);
            // Closed connection proves selection validation happens before SQL.
            await using(var duplicateConnection=new SqlConnection(target.ConnectionString))
                await Reject(async()=>await ((Task)batchedVerifier.Invoke(null,new object?[]{
                    duplicateConnection,null,repeatedInputs,CancellationToken.None})!),
                    "Bounded selection rejects duplicate operation IDs before SQL");
            // Cross-stream selections must be rejected before any SQL is opened,
            // even when both operation IDs are individually well formed.
            var foreignStreamIdentity=identity with { StreamKey=identity.StreamKey+"-foreign" };
            var mixedStreamInputs=Array.CreateInstance(batchedInputType,2);
            mixedStreamInputs.SetValue(Activator.CreateInstance(batchedInputType,
                latestOperation,identity,null),0);
            mixedStreamInputs.SetValue(Activator.CreateInstance(batchedInputType,
                Guid.NewGuid(),foreignStreamIdentity,null),1);
            await using(var closedStreamConnection=new SqlConnection(target.ConnectionString))
                await Reject(async()=>await ((Task)batchedVerifier.Invoke(null,new object?[]{
                    closedStreamConnection,null,mixedStreamInputs,CancellationToken.None})!),
                    "Batched publication verifier rejects cross-stream operation selection before SQL");
            // Invalid previously loaded receipt bytes are rejected before SQL.
            var oversizedReuseInputs=Array.CreateInstance(batchedInputType,1);
            oversizedReuseInputs.SetValue(Activator.CreateInstance(batchedInputType,
                latestOperation,identity,new byte[8388609]),0);
            await using(var closedReuseConnection=new SqlConnection(target.ConnectionString))
                await Reject(async()=>await ((Task)batchedVerifier.Invoke(null,new object?[]{
                    closedReuseConnection,null,oversizedReuseInputs,CancellationToken.None})!),
                    "Batched publication verifier rejects oversized reused result before SQL");
            // Empty operation and scope identifiers must fail before opening SQL.
            foreach(var invalidCase in new[]{
                (OperationId:Guid.Empty,Identity:identity,Label:"empty operation ID"),
                (OperationId:Guid.NewGuid(),Identity:identity with { MachineId=Guid.Empty },Label:"empty machine ID"),
                (OperationId:Guid.NewGuid(),Identity:identity with { MappingId=Guid.Empty },Label:"empty mapping ID")
            })
            {
                var invalidInputs=Array.CreateInstance(batchedInputType,1);
                invalidInputs.SetValue(Activator.CreateInstance(batchedInputType,
                    invalidCase.OperationId,invalidCase.Identity,null),0);
                await using var invalidConnection=new SqlConnection(target.ConnectionString);
                try
                {
                    await ((Task)batchedVerifier.Invoke(null,new object?[]{
                        invalidConnection,null,invalidInputs,CancellationToken.None})!);
                    throw new InvalidOperationException("Invalid publication selection was accepted.");
                }
                catch(InvalidDataException)
                {
                    Check(true,"Batched publication verifier rejects "+invalidCase.Label+" before SQL");
                }
            }
            // Gate 2: malformed, foreign-scope and oversized cursors fail closed.
            var cursorDecoder=typeof(FactoryConnect.Sql.Security.SqlSecurityAuthority).GetMethod(
                "DecodeCursor",BindingFlags.NonPublic|BindingFlags.Static)
                ?? throw new InvalidOperationException("Activity history cursor decoder missing.");
            void RejectCursor(string value,Guid machine,Guid mapping,string label)
            {
                try
                {
                    cursorDecoder.Invoke(null,new object?[]{value,machine,identity.StreamKey,mapping,
                        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)))});
                }
                catch(TargetInvocationException e)when(e.InnerException is ArgumentException)
                {
                    Check(true,label);
                    return;
                }
                throw new InvalidOperationException("Invalid history cursor was accepted: "+label);
            }
            RejectCursor("not-a-valid-base64-cursor!",scope.MachineId,scope.MappingId,
                "Gate 2 rejects malformed base64 history cursor");
            var foreignCursor=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
                MachineId=Guid.NewGuid(),StreamKey=identity.StreamKey,MappingId=scope.MappingId,
                Qualification=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity))),
                StartedAt=DateTimeOffset.UtcNow.ToString("O"),InstanceId="1",
                StartSequence="1",EffectId=Guid.NewGuid()}));
            RejectCursor(foreignCursor,scope.MachineId,scope.MappingId,
                "Gate 2 rejects foreign-machine history cursor");
            RejectCursor(foreignCursor,scope.MachineId,Guid.NewGuid(),
                "Gate 2 rejects foreign-mapping history cursor");
            var wrongQualificationCursor=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
                MachineId=scope.MachineId,StreamKey=identity.StreamKey,MappingId=scope.MappingId,
                Qualification=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wrong qualification"))),
                StartedAt=DateTimeOffset.UtcNow.ToString("O"),InstanceId="1",
                StartSequence="1",EffectId=Guid.NewGuid()}));
            RejectCursor(wrongQualificationCursor,scope.MachineId,scope.MappingId,
                "Gate 2 rejects cursor with incorrect qualification digest");
            RejectCursor(new string('A',2049),scope.MachineId,scope.MappingId,
                "Gate 2 rejects oversized cursor before decoding");
            // Gate 5: an oversized selection is rejected before SQL materialization.
            var tooManyInputs=Array.CreateInstance(batchedInputType,102);
            for(var index=0;index<tooManyInputs.Length;index++)
                tooManyInputs.SetValue(Activator.CreateInstance(batchedInputType,
                    Guid.NewGuid(),identity,null),index);
            await using(var boundedConnection=new SqlConnection(target.ConnectionString))
            {
                try
                {
                    await ((Task)batchedVerifier.Invoke(null,new object?[]{
                        boundedConnection,null,tooManyInputs,CancellationToken.None})!);
                    throw new InvalidOperationException("Oversized publication selection was accepted.");
                }
                catch(InvalidDataException)
                {
                    Check(true,"Bounded selection rejects more than 101 receipt operations before SQL");
                }
            }
            // The upper legal operation-selection boundary must pass preflight.
            // A closed connection is deliberate: reaching SQL produces an
            // InvalidOperationException, while rejecting the selection early
            // would produce InvalidDataException. This does not claim that the
            // separate per-operation source-witness boundary is qualified.
            var maximumInputs=Array.CreateInstance(batchedInputType,101);
            for(var index=0;index<maximumInputs.Length;index++)
                maximumInputs.SetValue(Activator.CreateInstance(batchedInputType,
                    Guid.NewGuid(),identity,null),index);
            await using(var closedMaximumConnection=new SqlConnection(target.ConnectionString))
            {
                try
                {
                    await ((Task)batchedVerifier.Invoke(null,new object?[]{
                        closedMaximumConnection,null,maximumInputs,CancellationToken.None})!);
                    throw new InvalidOperationException("Closed SQL connection unexpectedly executed.");
                }
                catch(InvalidOperationException e)when(e.Message!="Closed SQL connection unexpectedly executed.")
                {
                    Check(true,"Exactly 101 distinct receipt operations pass bounded preflight and reach SQL");
                }
            }
            // A syntactically valid but nonexistent receipt must never be treated
            // as a successful partial batch. The SQL result must match selection.
            var missingInputs=Array.CreateInstance(batchedInputType,1);
            missingInputs.SetValue(Activator.CreateInstance(batchedInputType,
                Guid.NewGuid(),identity,null),0);
            await using(var missingConnection=new SqlConnection(target.ConnectionString))
            {
                await missingConnection.OpenAsync();
                await using var missingTx=(SqlTransaction)await missingConnection.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    try
                    {
                        await ((Task)batchedVerifier.Invoke(null,new object[]{
                            missingConnection,missingTx,missingInputs,CancellationToken.None})!);
                        throw new InvalidOperationException("Missing publication receipt was accepted.");
                    }
                    catch(InvalidDataException)
                    {
                        Check(true,"Gate 5 rejects a missing receipt instead of accepting a partial batch");
                    }
                }
                finally {await missingTx.RollbackAsync();}
            }
            // Gate 5: a valid receipt must not mask a missing receipt in the
            // same batch. No partial verification success is permitted.
            var mixedInputs=Array.CreateInstance(batchedInputType,2);
            mixedInputs.SetValue(Activator.CreateInstance(batchedInputType,
                latestOperation,identity,null),0);
            mixedInputs.SetValue(Activator.CreateInstance(batchedInputType,
                Guid.NewGuid(),identity,null),1);
            await using(var mixedConnection=new SqlConnection(target.ConnectionString))
            {
                await mixedConnection.OpenAsync();
                await using var mixedTx=(SqlTransaction)await mixedConnection.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    try
                    {
                        await ((Task)batchedVerifier.Invoke(null,new object[]{
                            mixedConnection,mixedTx,mixedInputs,CancellationToken.None})!);
                        throw new InvalidOperationException("Partially populated publication batch was accepted.");
                    }
                    catch(InvalidDataException)
                    {
                        Check(true,"Gate 5 rejects mixed valid and missing receipt batch atomically");
                    }
                }
                finally {await mixedTx.RollbackAsync();}
            }
            Check(await Snapshot()==beforeAncestry,"read-corruption fixtures rollback without changing durable publication history");
            var pending=await ingest(70,false,false);var denied=await Work();var beforeRevocation=await Snapshot();await revoke();
            await Reject(()=>store.ApplyAcceptedWorkAsync(denied),"revoked mapping eligibility rejects new publication");
            Check(await Snapshot()==beforeRevocation,"revocation denial advances no checkpoint receipt or period");
            Check(children.Select(x=>x.Process.Id).Distinct().Count()==children.Count && children.Count>=10,"restart qualification uses distinct actual OS process lifetimes");
            Directory.CreateDirectory("artifacts/ingestion");await File.WriteAllTextAsync("artifacts/ingestion/activity-publication.json",JsonSerializer.Serialize(new{
                profile=scope.PublicationProfile,checks,processIds=children.Select(x=>x.Process.Id),events,exactPeriod=exact,physicalCompleteness=false},new JsonSerializerOptions{WriteIndented=true}));
            return checks;
        }
        finally
        {foreach(var child in children){if(!child.Process.HasExited){child.Process.Kill(true);await child.Process.WaitForExitAsync();}await child.Errors;child.Process.Dispose();}}
    }
    private static void RequireDisposable(string name)
    {if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^FactoryConnect_V2_Test_[a-fA-F0-9]{32}$"))throw new InvalidOperationException("Generated disposable database required.");}
}
