using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FactoryConnect.Sql.Security;

/// <summary>Explicit direct-CA/CRL transport policy. Constructing it does not register a listener.</summary>
public sealed class RuntimeMtlsServer
{
    private readonly byte[] anchor;
    private readonly byte[] distributionPoint;
    private readonly DateTimeOffset rootFrom,rootUntil;
    private static readonly ActivitySource Diagnostics=new("FactoryConnect.V2.RuntimeTransport");
    public RuntimeMtlsServer(byte[] trustAnchorDer,Uri allowedCrlUri)
    {
        if(!allowedCrlUri.IsAbsoluteUri||allowedCrlUri.Scheme is not ("http" or "https")||allowedCrlUri.UserInfo.Length!=0||
            allowedCrlUri.Fragment.Length!=0||allowedCrlUri.AbsoluteUri.Length>2048)throw new ArgumentException("Explicit bounded CRL URI required.");
        anchor=trustAnchorDer.ToArray();if(anchor.Length is 0 or >8192)throw new ArgumentException("Explicit bounded public CA certificate required.");
        using var root=RuntimeCertificateProfile.LoadExact(anchor);
        rootFrom=new(root.NotBefore.ToUniversalTime());rootUntil=new(root.NotAfter.ToUniversalTime());
        distributionPoint=CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([allowedCrlUri.AbsoluteUri],false).RawData;
    }
    public async Task<RuntimeMtlsChannel> AuthenticateAsync(Stream transport,X509Certificate2 serverCertificate,CancellationToken ct=default)
    {
        if(!serverCertificate.HasPrivateKey)throw new ArgumentException("Server certificate private key must be provisioned outside SQL.");
        var ssl=new SslStream(transport,false);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate=serverCertificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                AllowRenegotiation=false,AllowTlsResume=false,EncryptionPolicy=EncryptionPolicy.RequireEncryption,
                RemoteCertificateValidationCallback=(_,certificate,_,_)=>
                {
                    if(certificate is null)return false;
                    try{ValidateStatus(certificate.GetRawCertData());return true;}
                    catch(Exception error)when(error is SecurityDeniedException or CryptographicException or ArgumentException){return false;}
                }
            },timeout.Token);
            if(!ssl.IsAuthenticated||!ssl.IsMutuallyAuthenticated||!ssl.IsServer||!ssl.IsEncrypted||!ssl.IsSigned||ssl.RemoteCertificate is null)
                throw new SecurityDeniedException();
            return new RuntimeMtlsChannel(ssl,this);
        }
        catch{await ssl.DisposeAsync();throw;}
    }
    internal void ValidateStatus(byte[] certificateDer)
    {
        using var activity=Diagnostics.StartActivity("runtime.ca.verify");var started=Stopwatch.GetTimestamp();
        try{ValidateStatusCore(certificateDer);activity?.SetStatus(ActivityStatusCode.Ok);}
        catch{activity?.SetStatus(ActivityStatusCode.Error);throw;}
        finally{activity?.SetTag("runtime.ca.verify.elapsed.ms",Stopwatch.GetElapsedTime(started).TotalMilliseconds);}
    }
    private void ValidateStatusCore(byte[] certificateDer)
    {
        if(certificateDer.Length is 0 or >8192)throw new SecurityDeniedException();
        using var certificate=RuntimeCertificateProfile.LoadExact(certificateDer);using var root=RuntimeCertificateProfile.LoadExact(anchor);
        RuntimeCertificateProfile.ValidateShape(certificate,root,DateTimeOffset.UtcNow);
        var points=certificate.Extensions.Cast<X509Extension>().Where(e=>e.Oid?.Value=="2.5.29.31").ToArray();
        // Single exact allowlisted CDP only. Reject AIA so alternate OCSP/issuer endpoints cannot be fetched.
        if(points.Length!=1||!points[0].RawData.AsSpan().SequenceEqual(distributionPoint)||certificate.Extensions.Cast<X509Extension>().Any(e=>e.Oid?.Value=="1.3.6.1.5.5.7.1.1"))
            throw new SecurityDeniedException();
        using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode=X509RevocationMode.Online;chain.ChainPolicy.RevocationFlag=X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags=X509VerificationFlags.NoFlag;chain.ChainPolicy.DisableCertificateDownloads=true;
        chain.ChainPolicy.UrlRetrievalTimeout=TimeSpan.FromSeconds(1);chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if(!chain.Build(certificate)||chain.ChainElements.Count!=2||!chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(anchor))throw new SecurityDeniedException();
    }
    internal byte[] AnchorHash=>SHA256.HashData(anchor);
    internal (DateTimeOffset From,DateTimeOffset Until) RootInterval=>(rootFrom,rootUntil);
}

/// <summary>Nonserializable transport possession evidence, minted only after this server's actual mutual handshake.</summary>
public sealed class RuntimeMtlsChannel:IAsyncDisposable
{
    private readonly SslStream stream;
    private readonly RuntimeMtlsServer policy;
    private readonly byte[] peerCertificate;
    private int disposed;
    internal RuntimeMtlsChannel(SslStream stream,RuntimeMtlsServer policy)
    {this.stream=stream;this.policy=policy;peerCertificate=stream.RemoteCertificate?.GetRawCertData()??throw new SecurityDeniedException();}
    internal (byte[] Certificate,byte[] AnchorHash,long CheckedAt,DateTimeOffset RootFrom,DateTimeOffset RootUntil) Revalidate(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(Volatile.Read(ref disposed)!=0||!stream.IsAuthenticated||!stream.IsMutuallyAuthenticated||!stream.IsServer||!stream.IsEncrypted||!stream.IsSigned||stream.RemoteCertificate is null)
            throw new SecurityDeniedException();
        var certificate=peerCertificate.ToArray();policy.ValidateStatus(certificate);ct.ThrowIfCancellationRequested();
        return(certificate,policy.AnchorHash,Stopwatch.GetTimestamp(),policy.RootInterval.From,policy.RootInterval.Until);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>stream.ReadAsync(buffer,ct);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)=>stream.WriteAsync(buffer,ct);
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)==0)await stream.DisposeAsync();}
}
