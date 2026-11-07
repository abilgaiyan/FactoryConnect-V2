using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FactoryConnect.Sql.Security;
namespace FactoryConnect.AcquisitionHost;

// The trusted acquisition host owns SQL and runtime credentials. Its carrier is loopback-only,
// accepts exactly its own client, then closes the listener. It exposes no remote upload service.
internal sealed class LocalRuntimeTransport:IAsyncDisposable
{
    private readonly TcpClient clientSocket,serverSocket;
    private readonly X509Certificate2 serverCertificate,runtimeCertificate;
    internal SslStream Client{get;}
    internal RuntimeMtlsChannel Channel{get;}
    private LocalRuntimeTransport(TcpClient clientSocket,TcpClient serverSocket,SslStream client,RuntimeMtlsChannel channel,X509Certificate2 server,X509Certificate2 runtime)
    {this.clientSocket=clientSocket;this.serverSocket=serverSocket;Client=client;Channel=channel;serverCertificate=server;runtimeCertificate=runtime;}
    internal static async Task<LocalRuntimeTransport> Open(HostOptions options,CancellationToken ct)
    {
        var server=X509Certificate2.CreateFromPemFile(options.ServerCertificatePath,options.ServerKeyPath);var runtime=X509Certificate2.CreateFromPemFile(options.RuntimeCertificatePath,options.RuntimeKeyPath);
        var now=DateTimeOffset.UtcNow;
        if(!server.HasPrivateKey||!runtime.HasPrivateKey||server.NotBefore.ToUniversalTime()>now.UtcDateTime||server.NotAfter.ToUniversalTime()<=now.UtcDateTime||!server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(x=>x.EnhancedKeyUsages.Cast<Oid>().Any(o=>o.Value=="1.3.6.1.5.5.7.3.1")))throw new CryptographicException("Provisioned server-auth certificate required.");
        var constraints=server.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();var usage=server.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        using var serverKey=server.GetRSAPublicKey();using var runtimeKey=runtime.GetRSAPublicKey();
        if(constraints is null||constraints.CertificateAuthority||usage is null||!usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)||(usage.KeyUsages&(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign))!=0||serverKey is null||serverKey.KeySize<3072||runtimeKey is null||serverKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(runtimeKey.ExportSubjectPublicKeyInfo()))throw new CryptographicException("Separate RSA server leaf key required.");
        var anchor=File.ReadAllBytes(options.TrustAnchorPath);using var publicAnchor=X509CertificateLoader.LoadCertificate(anchor);using var anchorKey=publicAnchor.GetRSAPublicKey();if(anchorKey is null||anchorKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(serverKey.ExportSubjectPublicKeyInfo()))throw new CryptographicException("CA key cannot be used by the online server.");var policy=new RuntimeMtlsServer(anchor,new Uri(options.AllowedCrlUri,UriKind.Absolute));var serverHash=SHA256.HashData(server.RawData);
        var listener=new TcpListener(IPAddress.Loopback,0);var clientSocket=new TcpClient();TcpClient? serverSocket=null;SslStream? client=null;RuntimeMtlsChannel? channel=null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            listener.Start(1);await clientSocket.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port,timeout.Token);serverSocket=await listener.AcceptTcpClientAsync(timeout.Token);listener.Stop();
            client=new SslStream(clientSocket.GetStream(),false);var serverHandshake=policy.AuthenticateAsync(serverSocket.GetStream(),server,timeout.Token);
            var clientHandshake=client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="localhost",ClientCertificates=new X509CertificateCollection{runtime},EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,AllowRenegotiation=false,AllowTlsResume=false,EncryptionPolicy=EncryptionPolicy.RequireEncryption,
                RemoteCertificateValidationCallback=(_,certificate,_,_)=>certificate is not null&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()),serverHash)},timeout.Token);
            await Task.WhenAll(serverHandshake,clientHandshake);channel=await serverHandshake;return new(clientSocket,serverSocket,client,channel,server,runtime);
        }
        catch{if(channel is not null)await channel.DisposeAsync();if(client is not null)await client.DisposeAsync();serverSocket?.Dispose();clientSocket.Dispose();server.Dispose();runtime.Dispose();throw;}
        finally{listener.Stop();}
    }
    public async ValueTask DisposeAsync(){await Channel.DisposeAsync();await Client.DisposeAsync();serverSocket.Dispose();clientSocket.Dispose();serverCertificate.Dispose();runtimeCertificate.Dispose();}
}
