using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Text.Json;
using FactoryConnect.Protocols.MTConnect;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;
namespace FactoryConnect.AcquisitionHost;

internal sealed class AcquisitionWorker(HostOptions options,Action<object> report)
{
    internal async Task<int> Run(CancellationToken stopping)
    {
        long sqlRoundtrips=0;using var listener=new ActivityListener{ShouldListenTo=s=>s.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllData,ActivityStopped=a=>Interlocked.Add(ref sqlRoundtrips,Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")??0))};ActivitySource.AddActivityListener(listener);
        var authority=new SqlSecurityAuthority(options.SqlConnectionString,options.InstallationId);await using var transport=await LocalRuntimeTransport.Open(options,stopping);
        using var http=new HttpClient{Timeout=Timeout.InfiniteTimeSpan};var client=new MtConnectSampleClient(http);var endpoint=new MtConnectEndpoint(new Uri(options.AgentBaseUri));
        var started=Stopwatch.GetTimestamp();long polls=0,batches=0,sourceObservations=0,mappedObservations=0,idle=0;string? lastConfirmed=null;var failures=0;var stage="context";DateTimeOffset? lastSourceResponseAt=null,lastDurableCommitAt=null;
        report(new{status="started",processId=Environment.ProcessId,maxInFlight=1,queueCapacity=0});
        try
        {
            while(!stopping.IsCancellationRequested)
            {
                using var operation=CancellationTokenSource.CreateLinkedTokenSource(stopping);operation.CancelAfter(TimeSpan.FromSeconds(options.OperationTimeoutSeconds));
                try
                {
                    stage="context";var context=await authority.ReadMtConnectAcquisitionContextAsync(transport.Channel,options.SiteId,options.MachineId,options.StreamKey,operation.Token);
                    var from=context.Cursor?.NextSequence??(ulong.TryParse(options.BootstrapFromSequence,NumberStyles.None,CultureInfo.InvariantCulture,out var initial)?initial:throw new InvalidDataException("Explicit retained-window bootstrap sequence required."));
                    stage="fetch";var frame=await client.AcquireAsync(endpoint,context.DeviceUuid,from,options.BatchCount,operation.Token);polls++;lastSourceResponseAt=DateTimeOffset.UtcNow;
                    if(context.Cursor is {} cursor&&(frame.InstanceId!=cursor.InstanceId||frame.NextSequence<cursor.NextSequence))throw new InvalidDataException("Source instance change or regression requires recovery.");
                    if(frame.NextSequence==from&&frame.Observations.Count==0)
                    {
                        idle++;lastConfirmed=context.Cursor?.NextSequence.ToString(CultureInfo.InvariantCulture);report(new{status="idle",observedAt=DateTimeOffset.UtcNow,lastSourceResponseAt,lastDurableCommitAt,polls,batches,sourceObservations,mappedObservations,idle,lastConfirmedNextSequence=lastConfirmed,sqlRoundtrips=Interlocked.Read(ref sqlRoundtrips),managedBytes=GC.GetTotalMemory(false),workingSetBytes=Environment.WorkingSet});
                    }
                    else
                    {
                        stage="commit";var envelope=new MtConnectBatchEnvelope(Guid.NewGuid(),options.SiteId,options.MachineId,context.AcknowledgementId,options.StreamKey,context.Cursor?.BatchId,context.Cursor?.InstanceId,from);
                        var receiving=authority.ReceiveMtConnectAsync(transport.Channel,envelope,operation.Token);
                        async Task Send(){var prefix=new byte[4];BinaryPrimitives.WriteInt32BigEndian(prefix,frame.RawDocument.Length);await transport.Client.WriteAsync(prefix,operation.Token);await transport.Client.WriteAsync(frame.RawDocument,operation.Token);await transport.Client.FlushAsync(operation.Token);}
                        var sending=Send();var first=await Task.WhenAny(receiving,sending);if(first.IsFaulted||first.IsCanceled)operation.Cancel();await Task.WhenAll(receiving,sending); // No queued/prefetched next request; completion includes SQL commit.
                        using var receipt=JsonDocument.Parse(await receiving);batches++;sourceObservations+=frame.Observations.Count;mappedObservations+=receipt.RootElement.GetProperty("mappedObservations").GetInt32();lastConfirmed=frame.NextSequence.ToString(CultureInfo.InvariantCulture);lastDurableCommitAt=DateTimeOffset.UtcNow;
                        report(new{status="committed",observedAt=lastDurableCommitAt,lastSourceResponseAt,lastDurableCommitAt,polls,batches,sourceObservations,mappedObservations,idle,lastConfirmedNextSequence=lastConfirmed,sqlRoundtrips=Interlocked.Read(ref sqlRoundtrips),elapsedMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds,managedBytes=GC.GetTotalMemory(false),workingSetBytes=Environment.WorkingSet});
                    }
                    failures=0;
                }
                catch(OperationCanceledException)when(stopping.IsCancellationRequested){break;}
                catch(Exception error)when(stage=="fetch"&&error is not InvalidDataException&&error is HttpRequestException or IOException or OperationCanceledException)
                {
                    if(++failures>5){report(new{status="held",observedAt=DateTimeOffset.UtcNow,lastSourceResponseAt,lastDurableCommitAt,reason="source-unavailable",stage});return 20;}
                    report(new{status="retrying",observedAt=DateTimeOffset.UtcNow,lastSourceResponseAt,lastDurableCommitAt,reason="source-unavailable",failures,stage});
                    await Task.Delay(Math.Min(2000,options.PollIntervalMilliseconds*(1<<failures)),stopping);continue;
                }
                catch(Exception error)when(error is SecurityDeniedException or SecurityConflictException or InvalidDataException or MtConnectProtocolException or SqlException or OperationCanceledException or XmlException or DecoderFallbackException or CryptographicException or IOException)
                {report(new{status="held",observedAt=DateTimeOffset.UtcNow,lastSourceResponseAt,lastDurableCommitAt,reason=error is SecurityDeniedException or CryptographicException?"runtime-authority-denied":error is SecurityConflictException?"cursor-or-operation-conflict":error is SqlException or OperationCanceledException or IOException?"sql-or-transport-unavailable-or-ambiguous":"unsafe-source-response",stage});return 20;}
                await Task.Delay(options.PollIntervalMilliseconds,stopping);
            }
            return 0;
        }
        catch(OperationCanceledException)when(stopping.IsCancellationRequested){return 0;}
        finally{report(new{status="stopped",observedAt=DateTimeOffset.UtcNow,lastSourceResponseAt,lastDurableCommitAt,polls,batches,sourceObservations,mappedObservations,idle,lastConfirmedNextSequence=lastConfirmed,sqlRoundtrips=Interlocked.Read(ref sqlRoundtrips),elapsedMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds});}
    }
}
