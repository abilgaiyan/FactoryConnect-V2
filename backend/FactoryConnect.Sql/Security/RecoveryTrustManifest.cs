using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record RecoveryTrustFile(Guid InstallationId,string AllowedAdministratorIssuer,IReadOnlyList<BootstrapCustodian> Keys);
public sealed record RecoveryTrustProvisionPayload(Guid InstallationId,Guid OperationId,Guid TrustId,Guid ActorPrincipalId,
    string ActorIssuer,string ActorSubject,Guid ApproverKeyId,Guid ExecutorKeyId,DateTimeOffset IssuedAt,DateTimeOffset ExpiresAt,
    long ExpectedEpoch,byte[] Nonce,byte[] BootstrapTrustHash,byte[] RecoveryTrustHash,string Reason);
public sealed record SignedRecoveryTrustProvision(RecoveryTrustProvisionPayload Payload,IReadOnlyList<BootstrapSignature> Signatures,IReadOnlyList<BootstrapSignature> RecoverySignatures);

/// <summary>Public recovery trust, with a separate hash domain and key material from bootstrap.
/// The request cannot establish this authority without current administration and bootstrap custody proof.</summary>
public sealed class RecoveryTrustManifest
{
    private readonly BootstrapTrustManifest validated;
    private readonly byte[] bytes;
    public Guid InstallationId=>validated.InstallationId;
    public string AllowedAdministratorIssuer=>validated.AllowedAdministratorIssuer;
    public byte[] Hash=>SHA256.HashData(bytes);
    public byte[] CanonicalBytes=>bytes.ToArray();
    internal IReadOnlyList<BootstrapCustodian> Keys=>validated.Keys;
    public RecoveryTrustManifest(RecoveryTrustFile file)
    {
        if(file.Keys.Count!=2)throw new ArgumentException("Initial recovery trust requires exactly two custodians.");
        // Reuse the already tested exact identity, distinct-custodian and canonical RSA SPKI profile.
        validated=new(new BootstrapTrustFile(file.InstallationId,file.AllowedAdministratorIssuer,file.Keys));
        using var document=JsonDocument.Parse(validated.CanonicalBytes);
        bytes=JsonBytes(w=>
        {
            w.WriteStartObject();w.WriteString("domain","FactoryConnect-V2:recovery-trust:v1");
            foreach(var p in document.RootElement.EnumerateObject())if(p.Name!="domain")p.WriteTo(w);
            w.WriteEndObject();
        });
    }

    public void VerifyProvision(BootstrapTrustManifest bootstrap,SignedRecoveryTrustProvision request,DateTimeOffset now)
    {
        var p=request.Payload;var canonical=Canonicalize(p);
        if(InstallationId!=bootstrap.InstallationId||p.InstallationId!=InstallationId||AllowedAdministratorIssuer!=bootstrap.AllowedAdministratorIssuer||
            p.ActorIssuer!=bootstrap.AllowedAdministratorIssuer||!CryptographicOperations.FixedTimeEquals(p.BootstrapTrustHash,bootstrap.Hash)||
            !CryptographicOperations.FixedTimeEquals(p.RecoveryTrustHash,Hash)||p.IssuedAt>now||p.ExpiresAt<=now||request.Signatures.Count!=2||request.RecoverySignatures.Count!=2)
            throw new SecurityDeniedException();
        foreach(var key in Keys)
        {
            if(key.ValidFrom>now||key.ExpiresAt<p.ExpiresAt||bootstrap.Keys.Any(b=>b.KeyId==key.KeyId||b.PublicKeySpki.AsSpan().SequenceEqual(key.PublicKeySpki)))
                throw new SecurityDeniedException();
        }
        var first=bootstrap.Keys.SingleOrDefault(k=>k.KeyId==p.ApproverKeyId);
        var second=bootstrap.Keys.SingleOrDefault(k=>k.KeyId==p.ExecutorKeyId);
        var actorIdentity=BootstrapTrustManifest.IdentityBytes(p.ActorIssuer,p.ActorSubject);
        if(first is null||second is null||first.CustodianId==second.CustodianId||
            BootstrapTrustManifest.IdentityBytes(first.Issuer,first.Subject).AsSpan().SequenceEqual(actorIdentity)||
            BootstrapTrustManifest.IdentityBytes(second.Issuer,second.Subject).AsSpan().SequenceEqual(actorIdentity))throw new SecurityDeniedException();
        // New key owners must also prove possession/consent over the exact signed declaration.
        var recoverySeen=new HashSet<Guid>();
        foreach(var proof in request.RecoverySignatures)
        {
            var key=Keys.SingleOrDefault(k=>k.KeyId==proof.KeyId);
            if(key is null||!recoverySeen.Add(proof.KeyId)||
                BootstrapTrustManifest.IdentityBytes(key.Issuer,key.Subject).AsSpan().SequenceEqual(actorIdentity))throw new SecurityDeniedException();
            using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(key.PublicKeySpki,out _);
            if(!rsa.VerifyData(canonical,proof.Signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new SecurityDeniedException();
        }
        var seen=new HashSet<Guid>();
        foreach(var proof in request.Signatures)
        {
            if(!seen.Add(proof.KeyId)||proof.KeyId!=first.KeyId&&proof.KeyId!=second.KeyId)throw new SecurityDeniedException();
            var key=proof.KeyId==first.KeyId?first:second;
            if(key.ValidFrom>p.IssuedAt||key.ExpiresAt<p.ExpiresAt||key.ValidFrom>now||key.ExpiresAt<=now)throw new SecurityDeniedException();
            using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(key.PublicKeySpki,out _);
            if(!rsa.VerifyData(canonical,proof.Signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new SecurityDeniedException();
        }
    }

    public static byte[] Canonicalize(RecoveryTrustProvisionPayload p)
    {
        if(p.InstallationId==Guid.Empty||p.OperationId==Guid.Empty||p.TrustId==Guid.Empty||p.ActorPrincipalId==Guid.Empty||
            p.ApproverKeyId==Guid.Empty||p.ExecutorKeyId==Guid.Empty||p.ApproverKeyId==p.ExecutorKeyId||p.ExpectedEpoch<0||
            p.Nonce.Length!=32||p.BootstrapTrustHash.Length!=32||p.RecoveryTrustHash.Length!=32||p.ExpiresAt<=p.IssuedAt||
            p.ExpiresAt-p.IssuedAt>TimeSpan.FromMinutes(15)||string.IsNullOrWhiteSpace(p.Reason)||p.Reason.Length>500)
            throw new ArgumentException("Invalid bounded recovery-trust provisioning request.");
        _=BootstrapTrustManifest.IdentityBytes(p.ActorIssuer,p.ActorSubject);_=new UTF8Encoding(false,true).GetBytes(p.Reason);
        var bytes=JsonBytes(w=>
        {
            w.WriteStartObject();w.WriteString("domain","FactoryConnect-V2:recovery-trust-provision:v1");
            w.WriteString("installationId",p.InstallationId);w.WriteString("operationId",p.OperationId);w.WriteString("trustId",p.TrustId);
            w.WriteString("actorPrincipalId",p.ActorPrincipalId);w.WriteString("actorIssuer",p.ActorIssuer);w.WriteString("actorSubject",p.ActorSubject);
            w.WriteString("approverKeyId",p.ApproverKeyId);w.WriteString("executorKeyId",p.ExecutorKeyId);
            w.WriteString("issuedAt",Instant(p.IssuedAt));w.WriteString("expiresAt",Instant(p.ExpiresAt));w.WriteNumber("expectedEpoch",p.ExpectedEpoch);
            w.WriteBase64String("nonce",p.Nonce);w.WriteString("bootstrapTrustHash",Convert.ToHexString(p.BootstrapTrustHash).ToLowerInvariant());
            w.WriteString("recoveryTrustHash",Convert.ToHexString(p.RecoveryTrustHash).ToLowerInvariant());w.WriteString("reason",p.Reason);w.WriteEndObject();
        });
        if(bytes.Length>16384)throw new ArgumentException("Encoded recovery provisioning exceeds the operation limit.");
        return bytes;
    }
    private static string Instant(DateTimeOffset time)=>time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",CultureInfo.InvariantCulture);
    private static byte[] JsonBytes(Action<Utf8JsonWriter> write)
    {using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream))write(writer);return stream.ToArray();}
}
