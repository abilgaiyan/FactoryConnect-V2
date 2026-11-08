using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FactoryConnect.Sql.Security;

internal static class RuntimeMtlsTransportAcceptance
{
    internal static async Task<int> Run()
    {
        var count=0;var now=DateTimeOffset.UtcNow;
        await using(var fixture=new RuntimeMtlsFixture(Guid.NewGuid(),Guid.NewGuid(),now,3))
        {
            // Windows 10 Schannel has no supported TLS 1.3; Linux CI still exercises both protocols.
            var protocols=OperatingSystem.IsWindows()?[SslProtocols.Tls12]:new[]{SslProtocols.Tls12,SslProtocols.Tls13};
            foreach(var protocol in protocols)
            {
                await using var pair=await fixture.Connect(protocol);await pair.Channel.WriteAsync(new byte[]{90});
                var received=new byte[1];Check(await pair.Client.ReadAsync(received)==1&&received[0]==90,"real mutual "+protocol+" handshake and encrypted byte exchange");
            }
            await Reject(()=>fixture.Connect(noCertificate:true),"TLS refuses an absent client certificate");
            using var publicOnly=X509CertificateLoader.LoadCertificate(fixture.Certificate);
            await Reject(()=>fixture.Connect(certificate:publicOnly),"TLS requires actual client certificate private key possession");
            using var wrongRoot=RuntimeCertificateAcceptance.Fixture.CreateRoot(fixture.Credentials.Other,now);
            var wrongPolicy=new RuntimeMtlsServer(wrongRoot.RawData,fixture.CrlUri);
            await Reject(()=>fixture.Connect(policy:wrongPolicy),"TLS rejects a chain outside its explicit pinned CA");
            var wrongEndpoint=new RuntimeMtlsServer(fixture.Credentials.Root.RawData,new Uri(fixture.CrlUri.AbsoluteUri+"-other"));
            await Reject(()=>fixture.Connect(policy:wrongEndpoint),"TLS rejects a certificate with an unapproved encoded CRL endpoint");
        }
        foreach(var status in new[]{"revoked","expired","missing","timeout"})
        {
            await using var fixture=new RuntimeMtlsFixture(Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow,3,status);
            var watch=Stopwatch.StartNew();await Reject(()=>fixture.Connect(),"TLS fails closed for "+status+" CA revocation evidence");
            if(status=="timeout")Check(watch.Elapsed<TimeSpan.FromSeconds(5),"CA outage handshake stays within bounded retrieval budget");
        }
        return count;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Reject(Func<Task<RuntimeMtlsFixture.Pair>> action,string name)
        {
            try{await using var pair=await action();}
            catch(Exception error)when(error is AuthenticationException or IOException or SecurityDeniedException){Check(true,name);return;}
            throw new InvalidOperationException(name);
        }
    }
}
