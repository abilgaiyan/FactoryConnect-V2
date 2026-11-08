using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FactoryConnect.Sql.Security;

internal sealed class RuntimeMtlsFixture:IAsyncDisposable
{
    internal readonly RuntimeCertificateAcceptance.Fixture Credentials;
    internal readonly byte[] Certificate;
    internal readonly X509Certificate2 ClientCertificate,ServerCertificate;
    internal readonly RuntimeMtlsServer Policy;
    internal readonly Uri CrlUri;
    private readonly RSA serverKey=RSA.Create(3072);
    private readonly TcpListener crlListener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource stopping=new();
    private readonly Task serving;
    private readonly List<Task> connections=[];
    private readonly byte[] crl;
    private readonly string status;
    internal RuntimeMtlsFixture(Guid installation,Guid actor,DateTimeOffset now,long epoch,string status="good")
    {
        this.status=status;Credentials=new(installation,actor,now,epoch);
        crlListener.Start();CrlUri=new Uri($"http://127.0.0.1:{((IPEndPoint)crlListener.LocalEndpoint).Port}/crl/{Guid.NewGuid():N}");
        Certificate=Credentials.Leaf(Credentials.Runtime,now,crlUri:CrlUri);
        using(var publicCertificate=X509CertificateLoader.LoadCertificate(Certificate))ClientCertificate=publicCertificate.CopyWithPrivateKey(Credentials.Runtime);
        var serverRequest=new CertificateRequest("CN=localhost",serverKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},false));
        var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");serverRequest.CertificateExtensions.Add(names.Build());
        using(var publicServer=serverRequest.Create(Credentials.Root,now.AddMinutes(-1),now.AddDays(30),RandomNumberGenerator.GetBytes(16)))ServerCertificate=publicServer.CopyWithPrivateKey(serverKey);
        var builder=new CertificateRevocationListBuilder();
        if(status=="revoked")builder.AddEntry(ClientCertificate,now.AddSeconds(-1),X509RevocationReason.KeyCompromise);
        crl=builder.Build(Credentials.Root,BigInteger.One,status=="expired"?now.AddDays(-1):now.AddDays(1),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1,status=="expired"?now.AddDays(-2):now.AddMinutes(-1));
        Policy=new(Credentials.Root.RawData,CrlUri);serving=Serve();
    }
    private async Task Serve()
    {
        try{while(!stopping.IsCancellationRequested){var client=await crlListener.AcceptTcpClientAsync(stopping.Token);connections.Add(Respond(client));}}
        catch(OperationCanceledException)when(stopping.IsCancellationRequested){}
    }
    private async Task Respond(TcpClient client)
    {
        using(client)
        try
        {
            var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
            var line=await reader.ReadLineAsync(stopping.Token);if(line is null||line.Length>4096)return;
            for(var i=0;i<32;i++){var header=await reader.ReadLineAsync(stopping.Token);if(string.IsNullOrEmpty(header))break;if(header.Length>4096)return;}
            if(status=="timeout"){await Task.Delay(TimeSpan.FromSeconds(8),stopping.Token);return;}
            var found=status!="missing"&&line.StartsWith("GET "+CrlUri.PathAndQuery+" ",StringComparison.Ordinal);
            var body=found?crl:[];var headerBytes=Encoding.ASCII.GetBytes($"HTTP/1.1 {(found?"200 OK":"404 Not Found")}\r\nContent-Type: application/pkix-crl\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headerBytes,stopping.Token);await stream.WriteAsync(body,stopping.Token);
        }
        catch(Exception error)when(error is IOException or OperationCanceledException or SocketException){}
    }
    internal Task<Pair> Connect(SslProtocols? protocol=null,X509Certificate2? certificate=null,RuntimeMtlsServer? policy=null,bool noCertificate=false)
        =>Pair.Open(policy??Policy,ServerCertificate,noCertificate?null:certificate??ClientCertificate,protocol??(OperatingSystem.IsWindows()?SslProtocols.Tls12:SslProtocols.Tls13));
    internal sealed class Pair:IAsyncDisposable
    {
        internal readonly RuntimeMtlsChannel Channel;
        internal readonly SslStream Client;
        private readonly TcpClient serverSocket,clientSocket;
        private Pair(RuntimeMtlsChannel channel,SslStream client,TcpClient serverSocket,TcpClient clientSocket)
        {Channel=channel;Client=client;this.serverSocket=serverSocket;this.clientSocket=clientSocket;}
        internal static async Task<Pair> Open(RuntimeMtlsServer policy,X509Certificate2 serverCertificate,X509Certificate2? clientCertificate,SslProtocols protocol)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using var listenerStop=new ListenerStop(listener);var clientSocket=new TcpClient();TcpClient? serverSocket=null;SslStream? client=null;RuntimeMtlsChannel? channel=null;
            try
            {
                await clientSocket.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port,timeout.Token);
                serverSocket=await listener.AcceptTcpClientAsync(timeout.Token);listener.Stop();client=new SslStream(clientSocket.GetStream(),false);
                var serverHandshake=policy.AuthenticateAsync(serverSocket.GetStream(),serverCertificate,timeout.Token);
                var clientHandshake=AuthenticateClient();
                await Task.WhenAll(serverHandshake,clientHandshake);channel=await serverHandshake;
                return new(channel,client,serverSocket,clientSocket);
                async Task AuthenticateClient()
                {
                    try
                    {
                        await client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                        {
                            TargetHost="localhost",ClientCertificates=clientCertificate is null?[]:new X509CertificateCollection{clientCertificate},
                            EnabledSslProtocols=protocol,AllowTlsResume=false,AllowRenegotiation=false,
                            // Fixture-only explicit server DER pin; no application verifier can use this callback.
                            RemoteCertificateValidationCallback=(_,cert,_,_)=>cert is not null&&cert.GetRawCertData().AsSpan().SequenceEqual(serverCertificate.RawData)
                        },timeout.Token);
                    }
                    catch{await client.DisposeAsync();throw;}
                }
            }
            catch{if(channel is not null)await channel.DisposeAsync();if(client is not null)await client.DisposeAsync();serverSocket?.Dispose();clientSocket.Dispose();throw;}
        }
        public async ValueTask DisposeAsync(){await Channel.DisposeAsync();await Client.DisposeAsync();serverSocket.Dispose();clientSocket.Dispose();}
        private sealed class ListenerStop(TcpListener listener):IDisposable{public void Dispose()=>listener.Stop();}
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();crlListener.Stop();await serving;await Task.WhenAll(connections);stopping.Dispose();
        ClientCertificate.Dispose();ServerCertificate.Dispose();serverKey.Dispose();Credentials.Dispose();
    }
}
