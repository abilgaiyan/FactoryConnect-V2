using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record RuntimeCertificateRegistration(byte[] CertificateDer,byte[] TrustAnchorDer);
public sealed record SignedRuntimeCertificate(RuntimeCredentialPayload Payload,byte[] CertificateDer,byte[] TrustAnchorDer,
    IReadOnlyList<BootstrapSignature> Signatures,byte[] PossessionSignature);

/// <summary>Offline registration validation only. Never use it as a TLS authentication or CA revocation verdict.</summary>
public static class RuntimeCertificateProfile
{
    private const string ClientAuthentication="1.3.6.1.5.5.7.3.2";
    public static byte[] Canonicalize(RuntimeCredentialPayload payload,RuntimeCertificateRegistration certificate)
    {
        if(payload.Action is not ("enroll" or "rotate")||certificate.CertificateDer.Length is 0 or >8192||certificate.TrustAnchorDer.Length is 0 or >8192)
            throw new ArgumentException("Bounded certificate registration requires new credential enrollment or rotation.");
        var authority=RuntimeCredentialProof.Canonicalize(payload);
        using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();writer.WriteString("domain","FactoryConnect-V2:runtime-certificate-registration:v1");
            writer.WriteBase64String("authority",authority);writer.WriteBase64String("certificateDer",certificate.CertificateDer);
            writer.WriteBase64String("trustAnchorDer",certificate.TrustAnchorDer);writer.WriteEndObject();
        }
        var bytes=stream.ToArray();if(bytes.Length>16384)throw new ArgumentException("Certificate ceremony exceeds operation storage limit.");return bytes;
    }

    public static void ValidateRegistration(RuntimeCredentialPayload payload,RuntimeCertificateRegistration registration,DateTimeOffset now)
    {
        _=Canonicalize(payload,registration);
        using var certificate=LoadExact(registration.CertificateDer);using var anchor=LoadExact(registration.TrustAnchorDer);
        ValidateShape(certificate,anchor,now);
        using(var key=certificate.GetRSAPublicKey())
            if(key is null||!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(payload.PublicKeySpki))throw new SecurityDeniedException();
        if(new DateTimeOffset(certificate.NotBefore.ToUniversalTime())<payload.ValidFrom||new DateTimeOffset(certificate.NotAfter.ToUniversalTime())>payload.CredentialExpiresAt)
            throw new SecurityDeniedException();
        using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(anchor);chain.ChainPolicy.DisableCertificateDownloads=true;
        // This command registers signed public material, not an authenticated channel. No network work belongs under its SQL gate.
        chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags=X509VerificationFlags.NoFlag;chain.ChainPolicy.VerificationTime=now.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthentication));
        if(!chain.Build(certificate)||chain.ChainElements.Count!=2||
            !chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(registration.TrustAnchorDer))throw new SecurityDeniedException();
    }
    internal static void ValidateShape(X509Certificate2 certificate,X509Certificate2 anchor,DateTimeOffset now)
    {
        using var publicKey=certificate.GetRSAPublicKey()??throw new SecurityDeniedException();
        using var anchorKey=anchor.GetRSAPublicKey()??throw new SecurityDeniedException();
        RuntimeCredentialProof.ValidateKey(publicKey.ExportSubjectPublicKeyInfo());RuntimeCredentialProof.ValidateKey(anchorKey.ExportSubjectPublicKeyInfo());
        if(anchorKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo())||
            !anchor.SubjectName.RawData.AsSpan().SequenceEqual(anchor.IssuerName.RawData)||
            certificate.RawData.AsSpan().SequenceEqual(anchor.RawData)||
            certificate.SignatureAlgorithm.Value!="1.2.840.113549.1.1.11"||anchor.SignatureAlgorithm.Value!="1.2.840.113549.1.1.11")throw new SecurityDeniedException();
        var leafConstraints=Extension<X509BasicConstraintsExtension>(certificate,"2.5.29.19");
        var caConstraints=Extension<X509BasicConstraintsExtension>(anchor,"2.5.29.19");
        var leafUsage=Extension<X509KeyUsageExtension>(certificate,"2.5.29.15");
        var caUsage=Extension<X509KeyUsageExtension>(anchor,"2.5.29.15");
        var eku=Extension<X509EnhancedKeyUsageExtension>(certificate,"2.5.29.37");
        if(!leafConstraints.Critical||leafConstraints.CertificateAuthority||!caConstraints.Critical||!caConstraints.CertificateAuthority||
            !leafUsage.Critical||leafUsage.KeyUsages!=X509KeyUsageFlags.DigitalSignature||
            !caUsage.Critical||!caUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)||!caUsage.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign)||
            eku.EnhancedKeyUsages.Count!=1||eku.EnhancedKeyUsages[0].Value!=ClientAuthentication)
            throw new SecurityDeniedException();
        var from=new DateTimeOffset(certificate.NotBefore.ToUniversalTime());var until=new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        if(from>now||until<=now||
            anchor.NotBefore.ToUniversalTime()>now.UtcDateTime||anchor.NotAfter.ToUniversalTime()<=now.UtcDateTime)throw new SecurityDeniedException();
    }
    internal static byte[] AnchorKeyHash(RuntimeCertificateRegistration registration)
    {using var anchor=LoadExact(registration.TrustAnchorDer);using var key=anchor.GetRSAPublicKey()??throw new SecurityDeniedException();return SHA256.HashData(key.ExportSubjectPublicKeyInfo());}
    internal static (DateTimeOffset From,DateTimeOffset Until) CertificateInterval(RuntimeCertificateRegistration registration)
    {using var certificate=LoadExact(registration.CertificateDer);return(new(certificate.NotBefore.ToUniversalTime()),new(certificate.NotAfter.ToUniversalTime()));}
    internal static X509Certificate2 LoadExact(byte[] bytes)
    {
        var certificate=X509CertificateLoader.LoadCertificate(bytes);
        if(!certificate.RawData.AsSpan().SequenceEqual(bytes)){certificate.Dispose();throw new SecurityDeniedException();}return certificate;
    }
    private static T Extension<T>(X509Certificate2 certificate,string oid) where T:X509Extension
    {
        var extensions=certificate.Extensions.Cast<X509Extension>().Where(e=>e.Oid?.Value==oid).ToArray();
        if(extensions.Length!=1||extensions[0] is not T result)throw new SecurityDeniedException();return result;
    }
}
