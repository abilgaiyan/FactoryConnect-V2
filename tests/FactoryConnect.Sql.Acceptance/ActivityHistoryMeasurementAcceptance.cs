using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

internal static class ActivityHistoryMeasurementAcceptance
{
    internal sealed record Input(IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> Rows,
        ulong Through,int RawObservationCount);
    internal sealed record Span(string Name,DateTimeOffset Start,TimeSpan Duration,IReadOnlyDictionary<string,object?> Tags);
    private sealed record Trial(int Depth,int Sample,bool Profiled,double ElapsedMs,long AllocatedBytes,
        long WorkingSetBefore,long WorkingSetAfter,long ManagedBefore,long ManagedAfter,
        IReadOnlyList<Span> Spans);
    private sealed record Level(int AddedBatches,long RetainedObservations,long RetainedBatches,
        IReadOnlyList<Trial> Trials,ActivityHistorySqlProfile.Profile Profile,ActivityHistorySqlProfile.Profile RefreshedProfile);
    private sealed record Snapshot(string Checkpoint,long Periods,long Receipts);

    internal static async Task<int> RunAsync(InstallationTarget target,Guid site,
        ApprovedActivitySignal approved,ActivityQualificationIdentity identity,
        IReadOnlyList<SqlAcceptedActivityEvidenceReader.AcceptedRow> pending,
        Func<Task<Input>> ingest)
    {
        const string prefix="FactoryConnect_V2_Test_";
        if(!target.Database.StartsWith(prefix,StringComparison.Ordinal) ||
           !Guid.TryParseExact(target.Database[prefix.Length..],"N",out _))
            throw new InvalidDataException("History measurement requires a generated disposable database.");
        var checks=0;
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var store=new SqlActivityProcessorStore(target.ConnectionString);
        var checkpoint=await store.ReadCheckpointAsync(identity);
        using var expectedHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ActivityDurableResult? lastResult=null;
        Guid lastOperation=Guid.Empty;
        ulong lastAfter=0,lastThrough=0,lastGeneration=0;
        await Apply(store,new Input(pending,pending[^1].Sequence,4)); // Consume the restart fixture's intentionally pending cut.
        expectedHash.GetHashAndReset(); // Pending restart cut belongs to the baseline, not the new history oracle.
        var baseline=await SnapshotState();
        var baselinePage=await store.ReadPeriodPageAsync(identity,100);
        Check(baselinePage.Next is null && baselinePage.Rows.Count>0,"baseline history has an exact bounded period cursor");
        var baselineCursor=baselinePage.Rows[^1].Cursor;
        var levels=new List<Level>();
        var added=0;
        Directory.CreateDirectory("artifacts/performance");
        foreach(var depth in new[]{16,128,1024})
        {
            while(added<depth)
            {
                await Apply(store,await ingest());added++;
                if(added%128==0)Console.WriteLine($"MEASURE: activity history setup batches={added} observationsAdded={100L*added}");
            }
            var trials=new List<Trial>();
            var application="fc2-activity-history-"+Guid.NewGuid().ToString("N");
            var connection=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=application}.ConnectionString;
            // Warm the pool outside trials. Query plans are warmed by prefix processing.
            await using(var warm=new SqlConnection(connection)){await warm.OpenAsync();}
            for(var sample=0;sample<5;sample++)
            {
                var rows=await ingest();added++;
                trials.Add(await Measure(connection,rows,depth,sample,profiled:false));
            }
            var profileApplication="fc2-activity-history-"+Guid.NewGuid().ToString("N");
            var profileConnection=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=profileApplication}.ConnectionString;
            await using(var warm=new SqlConnection(profileConnection)){await warm.OpenAsync();}
            var profileRows=await ingest();added++;
            var profile=await ActivityHistorySqlProfile.CaptureAsync(c,profileApplication,async()=> {
                var trial=await Measure(profileConnection,profileRows,depth,5,profiled:true);
                trials.Add(trial);return trial.Spans;
            },$"artifacts/performance/activity-history-profile-{depth}.json");
            // Requalify after statistics invalidates/recompiles cached choices.
            await Exec("UPDATE STATISTICS acquisition.MtConnectBatch WITH FULLSCAN");
            var refreshedRows=await ingest();added++;
            var refreshed=await ActivityHistorySqlProfile.CaptureAsync(c,profileApplication,async()=> {
                var trial=await Measure(profileConnection,refreshedRows,depth,6,profiled:true);
                trials.Add(trial);return trial.Spans;
            },$"artifacts/performance/activity-history-profile-{depth}-refreshed.json");
            foreach(var plan in new[]{profile,refreshed})
            {
                var batchAccess=plan.Accesses.Where(x=>x.Table=="MtConnectBatch").ToArray();
                Check(batchAccess.Length>0 && batchAccess.All(x=>x.ActualRowsRead.HasValue &&
                        x.PhysicalOperator.Contains("Seek",StringComparison.Ordinal)) &&
                    batchAccess.Sum(x=>x.ActualRowsRead!.Value)==12,
                    "depth "+depth+" uses exactly bounded batch seeks in cached and refreshed-statistics plans");
            }
            var retained=await Count("acquisition.MtConnectObservation");
            var batches=await Count("acquisition.MtConnectBatch");
            levels.Add(new(depth,retained,batches,trials,profile,refreshed));
            Check(profile.DroppedEvents==0 && !profile.Truncated && profile.PlanEvents>0,
                "depth "+depth+" captures complete SQL logical reads and actual access plans");
            Check(lastResult is not null && (await store.ReadCheckpointAsync(identity))==checkpoint,
                "depth "+depth+" preserves exact checkpoint and accepted provenance");
            var beforeReplay=await SnapshotState();
            var replay=await store.ApplyAcceptedAsync(lastOperation,target.InstallationId,site,identity.MachineId,
                identity.MappingId,identity.StreamKey,profileRows.Rows[0].InstanceId,lastAfter,lastThrough,lastGeneration);
            Check(replay.Replayed && replay.Checkpoint==lastResult!.Checkpoint &&
                replay.NewPeriods.SequenceEqual(lastResult.NewPeriods) && await SnapshotState()==beforeReplay,
                "depth "+depth+" exact receipt replay adds no effects");
            var ordinary=trials.Where(x=>!x.Profiled).ToArray();
            Console.WriteLine($"MEASURE: activity depth={depth} retainedRows={retained} batches={batches} "+
                $"medianMs={ordinary.Select(x=>x.ElapsedMs).Order().ElementAt(2):F2} "+
                $"statementCount={profile.Statements.Count} logicalReads={profile.Commands.Sum(x=>x.LogicalReads)} "+
                $"observationRowsRead={profile.Accesses.Where(x=>x.Table=="MtConnectObservation").Sum(x=>x.ActualRowsRead??0)}");
        }
        var ordinaryTrials=levels.SelectMany(x=>x.Trials).Where(x=>!x.Profiled).ToArray();
        long Trips(Trial trial,string operation,string tag)=>Convert.ToInt64(trial.Spans.Single(x=>x.Name==operation).Tags[tag]);
        Check(levels.SelectMany(x=>new[]{x.Profile,x.RefreshedProfile}).Select(x=>x.Commands.Count).Distinct().Count()==1,
            "profiled SQL command count stays constant across increasing retained depths");
        Check(levels.SelectMany(x=>new[]{x.Profile,x.RefreshedProfile}).Select(x=>x.Statements.Count).Distinct().Count()==1,
            "profiled SQL statement count stays constant across increasing retained depths");
        Check(ordinaryTrials.Select(x=>Trips(x,"activity.sql.apply","activity.sql.roundtrips")).Distinct().Count()==1 &&
            ordinaryTrials.Select(x=>Trips(x,"activity.accepted-source.read","accepted-source.sql.roundtrips")).Distinct().Count()==1,
            "reader and publication roundtrips remain constant across increasing retained depths");
        Check(ordinaryTrials.All(x=>Trips(x,"activity.accepted-source.read","accepted-source.observations")==2 &&
            Trips(x,"activity.accepted-source.read","accepted-source.distinct-batches")==1 &&
            Trips(x,"activity.accepted-source.read","accepted-source.batch-parses")==1 &&
            Trips(x,"activity.accepted-source.read","accepted-source.aggregate-bytes")<=8L*1024*1024),
            "every ordinary tail cut reads two states and one bounded document regardless of history");
        using var actualHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pageCursor=baselineCursor;
        var periodCount=0;
        while(true)
        {
            var page=await store.ReadPeriodPageAsync(identity,100,pageCursor);
            foreach(var row in page.Rows){Hash(actualHash,row.Period);periodCount++;}
            if(page.Next is null)break;
            pageCursor=page.Next;
        }
        var expectedDigest=Convert.ToHexString(expectedHash.GetHashAndReset());
        var actualDigest=Convert.ToHexString(actualHash.GetHashAndReset());
        Check(periodCount==added && expectedDigest==actualDigest &&
            (await SnapshotState()).Periods==baseline.Periods+added &&
            (await SnapshotState()).Receipts==baseline.Receipts+added,
            "all historical outputs equal the exact per-cut oracle through bounded pages and one receipt per cut");

        // Failure acceptance at the deepest history, with a valid new cut.
        var next=await ingest();
        var stable=await SnapshotState();
        var prior=checkpoint.AcceptedSource!.LastObservation;
        await Exec("DISABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation");
        try
        {
            await ChangeAddress(prior.Sequence,Encoding.UTF8.GetBytes("MISSING"));
            try { await Reject(next,"retained predecessor",sqlNumber:51078); }
            finally { await ChangeAddress(prior.Sequence,Encoding.UTF8.GetBytes(identity.SourceAddress)); }
            await ChangeAddress(next.Rows[0].Sequence,Encoding.UTF8.GetBytes("MISSING"));
            try { await Reject(next,"selected document observation",expected:"omits or duplicates"); }
            finally { await ChangeAddress(next.Rows[0].Sequence,Encoding.UTF8.GetBytes(identity.SourceAddress)); }
        }
        finally { await Exec("ENABLE TRIGGER acquisition.TR_MtConnectObservation_Immutable ON acquisition.MtConnectObservation"); }
        var original=(byte[])(await Scalar("SELECT RawDocument FROM acquisition.MtConnectBatch WHERE BatchId=@batch",("@batch",prior.BatchId)))!;
        await Exec("DISABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");
        try
        {
            await Exec("UPDATE acquisition.MtConnectBatch SET RawDocument=0x58,DocumentHash=HASHBYTES('SHA2_256',0x58) WHERE BatchId=@batch",("@batch",prior.BatchId));
            await Reject(next,"corrupt predecessor document",sqlNumber:51078);
        }
        finally
        {
            await Exec("UPDATE acquisition.MtConnectBatch SET RawDocument=@bytes,DocumentHash=HASHBYTES('SHA2_256',@bytes) WHERE BatchId=@batch",
                ("@bytes",original),("@batch",prior.BatchId));
            await Exec("ENABLE TRIGGER acquisition.TR_MtConnectBatch_Immutable ON acquisition.MtConnectBatch");
        }
        Check(await SnapshotState()==stable,"deep-history negative fixtures restore evidence and preserve durable effects");
        // Row-access coverage is reported separately from returned rows. No invented
        // production latency/memory threshold or physical-completeness claim.
        var bounds=levels.Select(level=>new {
            level.AddedBatches,level.RetainedObservations,level.RetainedBatches,
            statementCount=level.Profile.Statements.Count,commandCount=level.Profile.Commands.Count,
            logicalReads=level.Profile.Commands.Sum(x=>x.LogicalReads),
            accesses=level.Profile.Accesses.GroupBy(x=>x.Table).Select(group=>new {
                table=group.Key,operators=group.Count(),actualRows=group.Sum(x=>x.ActualRows),
                actualRowsRead=group.Sum(x=>x.ActualRowsRead??0),
                rowsReadCoverageComplete=group.All(x=>x.ActualRowsRead.HasValue || x.Executions==0)
            }).ToArray(),
            stages=level.Profile.Commands.GroupBy(x=>x.Phase).Select(group=>new {
                phase=group.Key,statements=group.Count(),logicalReads=group.Sum(x=>x.LogicalReads),
                rowsAffectedOrReturned=group.Sum(x=>x.RowsAffectedOrReturned),serverDurationMs=group.Sum(x=>x.DurationMs)
            }).ToArray(),
            latency=level.Trials.Where(x=>!x.Profiled).Select(x=>x.ElapsedMs).Order().ToArray()
        }).ToArray();
        await File.WriteAllTextAsync("artifacts/performance/activity-history-measurement.json",JsonSerializer.Serialize(new {
            schemaVersion=1,fixture=true,commit=Environment.GetEnvironmentVariable("GITHUB_SHA"),checks,
            scope="One machine, serial accepted-source tail processing, disposable SQL; measured baseline, not production capacity",
            batchesProcessed=added,observationsPerNewBatch=100,periodCount,expectedDigest,actualDigest,levels,bounds,
            limitations=new[]{"Five ordinary latency/allocation trials per depth; separate profiled trial includes XE overhead.",
                "Working set and all-thread allocation include runner/driver overhead; not a retained-heap or fleet capacity proof.",
                "Returned rows are not examined rows; actual plan access counters are reported with missing-counter coverage.",
                "Client/server phase correlation uses UTC spans; unmatched SQL statements remain explicit.",
                "Selected payload cardinality, roundtrips, statement count and exact output are asserted; logical-read, latency and memory envelopes require review of measured evidence.",
                "Cross-batch continuity remains broken and physical source completeness unproven. No production/UI enablement."}
        },new JsonSerializerOptions{WriteIndented=true}));
        return checks;

        async Task<ActivityDurableResult> Apply(SqlActivityProcessorStore writer,
            Input input)
        {
            var rows=input.Rows;
            if(rows.Count!=2 || rows[0].AcknowledgementId!=approved.AcknowledgementId ||
                rows[0].RawValue!="ACTIVE" || rows[1].RawValue!="READY")
                throw new InvalidDataException("History cut must contain exact independently ingested ACTIVE/READY rows.");
            lastOperation=Guid.NewGuid();lastAfter=checkpoint.AcceptedSource!.ThroughSequence;
            lastThrough=input.Through;lastGeneration=checkpoint.NextGeneration;
            var result=await writer.ApplyAcceptedAsync(lastOperation,target.InstallationId,site,
                identity.MachineId,identity.MappingId,identity.StreamKey,rows[0].InstanceId,
                lastAfter,lastThrough,lastGeneration);
            var expected=new ClosedActivityPeriod(identity.MachineId,identity.StreamKey,identity.MappingId,
                ActivityState.Running,rows[0].ObservedAt,rows[1].ObservedAt,
                rows[1].ObservedAt.UtcTicks-rows[0].ObservedAt.UtcTicks,rows[0].InstanceId,
                rows[0].Sequence,rows[1].Sequence,rows[0].RawValue,rows[1].RawValue);
            if(result.Replayed || result.NewPeriods.Count!=1 || result.NewPeriods[0]!=expected ||
                result.Checkpoint.NextGeneration!=checkpoint.NextGeneration+1 ||
                result.Checkpoint.Qualification!=identity || result.Checkpoint.AcceptedSource?.LastObservation!=rows[1] ||
                result.Checkpoint.AcceptedSource.OpenAnchorObservation!=rows[1] ||
                result.Checkpoint.AcceptedSource.OperationId!=lastOperation)
                throw new InvalidDataException("History processing changed exact period/checkpoint/provenance output.");
            checkpoint=result.Checkpoint;lastResult=result;
            if(expectedHash is not null)Hash(expectedHash,expected);
            return result;
        }
        async Task<Trial> Measure(string connection,Input rows,
            int depth,int sample,bool profiled)
        {
            if(rows.RawObservationCount!=100 || rows.Through<rows.Rows[^1].Sequence)
                throw new InvalidDataException("Measured history cut must cover its complete accepted 100-observation frontier.");
            var spans=new List<Span>();
            using var listener=new ActivityListener {
                ShouldListenTo=source=>source.Name is "FactoryConnect.V2.Activity" or "FactoryConnect.V2.Activity.AcceptedEvidence",
                Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped=activity=>spans.Add(new(activity.OperationName,new DateTimeOffset(activity.StartTimeUtc),
                    activity.Duration,activity.TagObjects.ToDictionary(x=>x.Key,x=>x.Value)))
            };
            ActivitySource.AddActivityListener(listener);
            var managedBefore=GC.GetTotalMemory(forceFullCollection:true);
            var workingBefore=Environment.WorkingSet;
            var allocated=GC.GetTotalAllocatedBytes(precise:true);
            var clock=Stopwatch.StartNew();
            await Apply(new SqlActivityProcessorStore(connection),rows);
            clock.Stop();
            var allocatedBytes=GC.GetTotalAllocatedBytes(precise:true)-allocated;
            var workingAfter=Environment.WorkingSet;
            var managedAfter=GC.GetTotalMemory(forceFullCollection:true);
            return new(depth,sample,profiled,clock.Elapsed.TotalMilliseconds,allocatedBytes,
                workingBefore,workingAfter,managedBefore,managedAfter,spans.ToArray());
        }
        async Task Reject(Input input,string label,
            string? expected=null,int? sqlNumber=null)
        {
            var rows=input.Rows;
            var clock=Stopwatch.StartNew();
            try
            {
                await store.ApplyAcceptedAsync(Guid.NewGuid(),target.InstallationId,site,identity.MachineId,
                    identity.MappingId,identity.StreamKey,rows[0].InstanceId,checkpoint.AcceptedSource!.ThroughSequence,
                    input.Through,checkpoint.NextGeneration);
            }
            catch(Exception error)when((error is SqlException sql && sql.Number==sqlNumber) ||
                (error is InvalidDataException && expected is not null && error.Message.Contains(expected,StringComparison.Ordinal)))
            {
                Check(await SnapshotState()==stable,"deep-history "+label+" fails closed without partial publication");
                Console.WriteLine($"MEASURE: activity negative={label} elapsedMs={clock.Elapsed.TotalMilliseconds:F2}");
                return;
            }
            throw new InvalidOperationException("History failure fixture accepted "+label);
        }
        async Task ChangeAddress(ulong sequence,byte[] address)=>await Exec("""
            UPDATE acquisition.MtConnectObservation SET AddressBytes=@address
            WHERE MachineId=@machine AND StreamKeyBytes=@stream AND InstanceId=@instance AND Sequence=@sequence
            """,("@address",address),("@machine",identity.MachineId),("@stream",Encoding.UTF8.GetBytes(identity.StreamKey)),
            ("@instance",(decimal)checkpoint.AcceptedSource!.InstanceId),("@sequence",(decimal)sequence));
        async Task<Snapshot> SnapshotState()
        {
            await using var q=new SqlCommand("""
                SELECT CheckpointJson,
                    (SELECT COUNT_BIG(*) FROM activity.ClosedPeriod WHERE MachineId=@machine AND MappingId=@mapping),
                    (SELECT COUNT_BIG(*) FROM activity.ProcessorOperation WHERE MachineId=@machine AND MappingId=@mapping)
                FROM activity.ProcessorCheckpoint WHERE MachineId=@machine AND MappingId=@mapping
                """,c);Bind(q,("@machine",identity.MachineId),("@mapping",identity.MappingId));
            await using var r=await q.ExecuteReaderAsync();if(!await r.ReadAsync())throw new InvalidDataException("History checkpoint missing.");
            return new(r.GetString(0),r.GetInt64(1),r.GetInt64(2));
        }
        async Task<long> Count(string table)=>Convert.ToInt64(await Scalar("SELECT COUNT_BIG(*) FROM "+table+" WHERE MachineId=@machine",("@machine",identity.MachineId)));
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Bind(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Bind(q,args);await q.ExecuteNonQueryAsync();}
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("Activity history: "+label);checks++;Console.WriteLine("PASS: activity history "+label);}
    }
    private static void Hash(IncrementalHash hash,ClosedActivityPeriod period)=>hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(period)+"\n"));
    private static void Bind(SqlCommand command,params (string Name,object Value)[] args)
    {
        foreach(var(name,value)in args)if(value is byte[] bytes)command.Parameters.Add(name,System.Data.SqlDbType.VarBinary,-1).Value=bytes;
        else command.Parameters.AddWithValue(name,value);
    }
}
