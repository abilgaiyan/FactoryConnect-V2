using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FactoryConnect.Protocols.MTConnect;

// Controlled source speaks real HTTP. It is never registered by a product host.
internal sealed class MtConnectAgentFixture:IAsyncDisposable
{
    private readonly TcpListener listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource stopped=new();
    private readonly Task serving;private readonly List<Task> clients=[];
    internal Func<ulong,int,(int Status,byte[] Body,long? DeclaredLength)> Reply {get;set;}=(_,_)=>throw new InvalidOperationException("Fixture reply required");
    internal MtConnectEndpoint Endpoint{get;}
    internal MtConnectAgentFixture(){listener.Start();Endpoint=new(new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/machine/"));serving=Serve();}
    private async Task Serve()
    {
        try{while(!stopped.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stopped.Token);var task=Handle(client);lock(clients)clients.Add(task);}}
        catch(Exception e)when(stopped.IsCancellationRequested&&e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private async Task Handle(TcpClient client)
    {
        using(client)
        {
            try
            {
                await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);var first=await reader.ReadLineAsync(stopped.Token)??throw new IOException();
                string? line;do{line=await reader.ReadLineAsync(stopped.Token);}while(!string.IsNullOrEmpty(line));
                var uri=new Uri("http://fixture"+first.Split(' ')[1]);var query=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=')).ToDictionary(x=>x[0],x=>x[1],StringComparer.Ordinal);
                var from=ulong.Parse(query["from"],CultureInfo.InvariantCulture);var count=int.Parse(query["count"],CultureInfo.InvariantCulture);var reply=Reply(from,count);
                var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} Fixture\r\nContent-Type: application/xml; charset=utf-8\r\nContent-Length: {reply.DeclaredLength??reply.Body.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(header,stopped.Token);await stream.WriteAsync(reply.Body,stopped.Token);
            }
            catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){}
        }
    }
    public async ValueTask DisposeAsync(){stopped.Cancel();listener.Stop();await serving;Task[] pending;lock(clients)pending=clients.ToArray();await Task.WhenAll(pending);stopped.Dispose();}
}
