using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FactoryConnect.Sql.ActivityProcessing;
using Microsoft.Data.SqlClient;

using var stopping=new CancellationTokenSource();
void Report(object value)=>Console.WriteLine(JsonSerializer.Serialize(value));
try
{
    byte[] bytes;
    if(args is ["--stdin"])
    {
        // Credentials are never command-line arguments or diagnostic output.
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var input=new List<byte>();var one=new byte[1];var stdin=Console.OpenStandardInput();
        while(await stdin.ReadAsync(one,deadline.Token)>0 && one[0]!='\n')
        {if(input.Count==32768)throw new InvalidDataException("Configuration byte limit exceeded.");input.Add(one[0]);}
        bytes=input.ToArray();
    }
    else if(args is ["--config",var path])
    {
        if(new FileInfo(path).Length>32768)throw new InvalidDataException("Configuration byte limit exceeded.");
        bytes=await File.ReadAllBytesAsync(path,stopping.Token);
    }
    else throw new ArgumentException("Use --config path or --stdin.");
    var options=JsonSerializer.Deserialize<ActivityHostOptions>(bytes,new JsonSerializerOptions {
        PropertyNameCaseInsensitive=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    })??throw new InvalidDataException("Explicit worker configuration required.");
    Array.Clear(bytes);
    var scope=options.Validate();
    Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stopping.Cancel();};
    using var termination=OperatingSystem.IsWindows()?null:PosixSignalRegistration.Create(PosixSignal.SIGTERM,context=>{context.Cancel=true;stopping.Cancel();});
    _=Task.Run(async()=>{try{while(!stopping.IsCancellationRequested){var line=await Console.In.ReadLineAsync(stopping.Token);if(line is null)return;if(line=="stop"){stopping.Cancel();return;}}}catch(OperationCanceledException){}});
    var store=new SqlActivityProcessorStore(options.SqlConnectionString);
    Report(new{status="started",processId=Environment.ProcessId,maxInFlight=1,queueCapacity=0,scope.MachineId,scope.MappingId,scope.StreamKey});
    while(!stopping.IsCancellationRequested)
    {
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(options.OperationTimeoutSeconds));
        var work=await store.DiscoverAcceptedWorkAsync(scope,options.CutSize,operation.Token);
        if(work.Candidate is { } cut)
        {
            var result=await store.ApplyAcceptedAsync(cut.OperationId,scope.InstallationId,scope.SiteId,scope.MachineId,
                scope.MappingId,scope.StreamKey,scope.InstanceId,cut.AfterSequence,cut.ThroughSequence,cut.ExpectedGeneration,ct:operation.Token);
            Report(new{status="committed",processId=Environment.ProcessId,cut.OperationId,
                generation=result.Checkpoint.NextGeneration.ToString(CultureInfo.InvariantCulture),
                throughSequence=result.Checkpoint.AcceptedSource!.ThroughSequence.ToString(CultureInfo.InvariantCulture),
                periods=result.NewPeriods.Count,replayed=result.Replayed});
        }
        else Report(new{status="idle",processId=Environment.ProcessId,generation=work.Generation.ToString(CultureInfo.InvariantCulture),
            afterSequence=work.AfterSequence.ToString(CultureInfo.InvariantCulture),
            acceptedThrough=work.AcceptedThrough?.ToString(CultureInfo.InvariantCulture),notConnectivity=true});
        await Task.Delay(options.PollIntervalMilliseconds,stopping.Token);
    }
    return 0;
}
catch(OperationCanceledException)when(stopping.IsCancellationRequested){return 0;}
catch(Exception error)
{
    Report(new{status="held",processId=Environment.ProcessId,reason="activity-work-unavailable-or-unsafe",errorType=error.GetType().Name});
    return 20; // No automatic retry after ambiguous SQL publication. Restart reconstructs SQL truth.
}
finally { Report(new{status="stopped",processId=Environment.ProcessId}); }

internal sealed record ActivityHostOptions(int Version,Guid InstallationId,Guid SiteId,Guid MachineId,
    Guid MappingId,string StreamKey,string InstanceId,string BootstrapAfterSequence,string SqlConnectionString,
    int CutSize=100,int PollIntervalMilliseconds=250,int OperationTimeoutSeconds=15)
{
    internal ActivityWorkerScope Validate()
    {
        var sql=new SqlConnectionStringBuilder(SqlConnectionString);
        if(Version!=1 || new[]{InstallationId,SiteId,MachineId,MappingId}.Any(x=>x==Guid.Empty) ||
            string.IsNullOrEmpty(StreamKey) || !StreamKey.StartsWith("mtconnect:uuid:",StringComparison.Ordinal) ||
            new UTF8Encoding(false,true).GetByteCount(StreamKey)>512 ||
            string.IsNullOrWhiteSpace(sql.DataSource) || string.IsNullOrWhiteSpace(sql.InitialCatalog) ||
            new[]{"master","model","msdb","tempdb"}.Contains(sql.InitialCatalog,StringComparer.OrdinalIgnoreCase) ||
            CutSize is <1 or >100 || PollIntervalMilliseconds is <50 or >60000 || OperationTimeoutSeconds is <1 or >30)
            throw new InvalidDataException("Explicit bounded activity worker configuration required.");
        ulong Number(string value)
        {
            if(!ulong.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n)||
                n.ToString(CultureInfo.InvariantCulture)!=value)throw new InvalidDataException("Canonical UInt64 string required.");
            return n;
        }
        var after=Number(BootstrapAfterSequence);if(after==ulong.MaxValue)throw new InvalidDataException("Bootstrap frontier cannot advance.");
        return new(InstallationId,SiteId,MachineId,MappingId,StreamKey,Number(InstanceId),after);
    }
}
