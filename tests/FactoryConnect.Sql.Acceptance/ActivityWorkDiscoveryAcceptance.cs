using FactoryConnect.Sql;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

internal static class ActivityWorkDiscoveryAcceptance
{
    internal static async Task<int> Run(InstallationTarget target,ActivityWorkerScope scope,Func<Task<ActivityHistoryMeasurementAcceptance.Input>> ingest)
    {
        var app="fc2-activity-history-worker-discovery-"+Guid.NewGuid().ToString("N");
        var sql=new SqlConnectionStringBuilder(target.ConnectionString){ApplicationName=app}.ConnectionString;
        var store=new SqlActivityProcessorStore(sql);
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        Directory.CreateDirectory("artifacts/performance");var checks=0;
        var profile=await ActivityHistorySqlProfile.CaptureAsync(c,app,async()=> {
            var spans=new List<ActivityHistoryMeasurementAcceptance.Span>();
            var start=DateTimeOffset.UtcNow;
            var idle=await store.DiscoverAcceptedWorkAsync(scope,2);
            Check(idle.Candidate is null,"deep idle frontier has no publication candidate");
            spans.Add(new("idle-discovery",start,DateTimeOffset.UtcNow-start,new Dictionary<string,object?>()));
            var input=await ingest();start=DateTimeOffset.UtcNow;
            var work=await store.DiscoverAcceptedWorkAsync(scope,2);
            Check(work.Candidate is {} cut && cut.AfterSequence==idle.AfterSequence && cut.ExpectedGeneration==idle.Generation &&
                cut.ThroughSequence==input.Rows[^1].Sequence,"deep next-input discovery returns the exact bounded accepted tail");
            Check((await store.DiscoverAcceptedWorkAsync(scope,2)).Candidate==work.Candidate,"repeated discovery pins the same deterministic operation");
            spans.Add(new("pending-discovery",start,DateTimeOffset.UtcNow-start,new Dictionary<string,object?>()));
            return spans;
        },"artifacts/performance/activity-worker-discovery.json");
        var source=profile.Accesses.Where(x=>x.Table=="MtConnectObservation").ToArray();
        Check(source.Length>0 && source.All(x=>x.ActualRowsRead.HasValue&&x.ActualRowsRead<=4) &&
            source.Any(x=>x.Index=="[IX_MtConnectObservation_ActivityNext]"&&x.PhysicalOperator.Contains("Seek",StringComparison.Ordinal)),
            "deep worker discovery uses bounded exact-signal seeks and bounded predecessor checks");
        Console.WriteLine($"MEASURE: worker discovery observation rows read={source.Sum(x=>x.ActualRowsRead)}, command logical reads={profile.Commands.Sum(x=>x.LogicalReads)}");
        return checks;
        void Check(bool result,string label){if(!result)throw new InvalidOperationException("Activity work discovery: "+label);checks++;Console.WriteLine("PASS: "+label);}
    }
}
