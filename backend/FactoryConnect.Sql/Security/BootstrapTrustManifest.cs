using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record BootstrapCustodian(Guid KeyId,Guid CustodianId,string Issuer,string Subject,
    byte[] PublicKeySpki,DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt);
public sealed record BootstrapPayload(Guid InstallationId,Guid OperationId,Guid PrincipalId,
    string AdministratorIssuer,string AdministratorSubject,string Profile,Guid ApproverKeyId,Guid ExecutorKeyId,
    DateTimeOffset IssuedAt,DateTimeOffset ExpiresAt,DateTimeOffset AdministratorExpiresAt,long ExpectedEpoch,
    byte[] Nonce,byte[] TrustManifestHash,string Reason);
public sealed record BootstrapSignature(Guid KeyId,byte[] Signature);
public sealed record SignedBootstrapRequest(BootstrapPayload Payload,IReadOnlyList<BootstrapSignature> Signatures);
public sealed record BootstrapTrustFile(Guid InstallationId,string AllowedAdministratorIssuer,IReadOnlyList<BootstrapCustodian> Keys);

/// <summary>Explicit installation trust input, loaded out of band. Submitted requests cannot supply their own trust keys.</summary>
public sealed class BootstrapTrustManifest
{
    public const string AdministratorProfile="security-administrator-v1";
    private readonly BootstrapCustodian[] keys;
    private readonly byte[] bytes;
    public Guid InstallationId { get; }
    public string AllowedAdministratorIssuer { get; }
    public byte[] Hash=>SHA256.HashData(bytes);
    public byte[] CanonicalBytes=>bytes.ToArray();
    internal IReadOnlyList<BootstrapCustodian> Keys=>keys;

    public BootstrapTrustManifest(BootstrapTrustFile file)
    {
        if(file.InstallationId==Guid.Empty||!Uri.TryCreate(file.AllowedAdministratorIssuer,UriKind.Absolute,out var uri)||
            uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Query.Length!=0||file.Keys.Count is <2 or >16)
            throw new ArgumentException("Explicit installation trust and HTTPS administrator issuer are required.");
        InstallationId=file.InstallationId;AllowedAdministratorIssuer=file.AllowedAdministratorIssuer;
        _=IdentityBytes(AllowedAdministratorIssuer,"issuer-validation");
        keys=file.Keys.Select(k=>k with{PublicKeySpki=k.PublicKeySpki.ToArray()}).OrderBy(k=>k.KeyId.ToString("D"),StringComparer.Ordinal).ToArray();
        var ids=new HashSet<Guid>();var publicKeys=new HashSet<string>(StringComparer.Ordinal);
        var custodians=new Dictionary<Guid,string>();var people=new Dictionary<string,Guid>(StringComparer.Ordinal);
        foreach(var k in keys)
        {
            if(k.KeyId==Guid.Empty||k.CustodianId==Guid.Empty||!ids.Add(k.KeyId)||k.ExpiresAt<=k.ValidFrom||k.PublicKeySpki.Length>2048)
                throw new ArgumentException("Invalid custodian identity, key or interval.");
            var identity=Convert.ToHexString(SHA256.HashData(IdentityBytes(k.Issuer,k.Subject)));
            if(custodians.TryGetValue(k.CustodianId,out var prior)&&prior!=identity||people.TryGetValue(identity,out var person)&&person!=k.CustodianId)
                throw new ArgumentException("Custodian identity aliases are inconsistent.");
            custodians[k.CustodianId]=identity;people[identity]=k.CustodianId;
            using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(k.PublicKeySpki,out var used);
            var canonicalKey=rsa.ExportSubjectPublicKeyInfo();
            if(used!=k.PublicKeySpki.Length||!k.PublicKeySpki.AsSpan().SequenceEqual(canonicalKey)||rsa.KeySize is <3072 or >8192||
                !rsa.ExportParameters(false).Exponent.AsSpan().SequenceEqual(new byte[]{1,0,1})||!publicKeys.Add(Convert.ToHexString(SHA256.HashData(canonicalKey))))
                throw new ArgumentException("Distinct canonical SPKI RSA public keys of at least 3072 bits and exponent 65537 are required.");
        }
        if(custodians.Count<2)throw new ArgumentException("Two independently bound custodians are required.");
        bytes=JsonBytes(w=>
        {
            w.WriteStartObject();w.WriteString("domain","FactoryConnect-V2:bootstrap-trust:v1");
            w.WriteString("installationId",InstallationId);w.WriteString("allowedAdministratorIssuer",AllowedAdministratorIssuer);
            w.WriteStartArray("keys");foreach(var k in keys)
            {
                w.WriteStartObject();w.WriteString("keyId",k.KeyId);w.WriteString("custodianId",k.CustodianId);
                w.WriteString("issuer",k.Issuer);w.WriteString("subject",k.Subject);w.WriteBase64String("publicKeySpki",k.PublicKeySpki);
                w.WriteString("validFrom",Instant(k.ValidFrom));w.WriteString("expiresAt",Instant(k.ExpiresAt));w.WriteEndObject();
            }
            w.WriteEndArray();w.WriteEndObject();
        });
    }

    public void Verify(BootstrapPayload payload,IReadOnlyList<BootstrapSignature> signatures,DateTimeOffset now)
    {
        var canonical=Canonicalize(payload);
        if(payload.InstallationId!=InstallationId||payload.AdministratorIssuer!=AllowedAdministratorIssuer||
            !CryptographicOperations.FixedTimeEquals(payload.TrustManifestHash,Hash)||payload.IssuedAt>now||payload.ExpiresAt<=now||
            payload.AdministratorExpiresAt<=now||signatures.Count!=2)throw new SecurityDeniedException();
        var first=keys.SingleOrDefault(k=>k.KeyId==payload.ApproverKeyId);
        var second=keys.SingleOrDefault(k=>k.KeyId==payload.ExecutorKeyId);
        if(first is null||second is null||first.CustodianId==second.CustodianId||
            IdentityBytes(first.Issuer,first.Subject).AsSpan().SequenceEqual(IdentityBytes(payload.AdministratorIssuer,payload.AdministratorSubject))||
            IdentityBytes(second.Issuer,second.Subject).AsSpan().SequenceEqual(IdentityBytes(payload.AdministratorIssuer,payload.AdministratorSubject)))
            throw new SecurityDeniedException();
        var supplied=new HashSet<Guid>();
        foreach(var signature in signatures)
        {
            if(!supplied.Add(signature.KeyId)||signature.KeyId!=first.KeyId&&signature.KeyId!=second.KeyId)throw new SecurityDeniedException();
            var key=signature.KeyId==first.KeyId?first:second;
            if(key.ValidFrom>payload.IssuedAt||key.ExpiresAt<payload.ExpiresAt||key.ValidFrom>now||key.ExpiresAt<=now)throw new SecurityDeniedException();
            using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(key.PublicKeySpki,out _);
            if(!rsa.VerifyData(canonical,signature.Signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new SecurityDeniedException();
        }
    }

    public static byte[] Canonicalize(BootstrapPayload p)
    {
        if(p.InstallationId==Guid.Empty||p.OperationId==Guid.Empty||p.PrincipalId==Guid.Empty||p.ApproverKeyId==Guid.Empty||
            p.ExecutorKeyId==Guid.Empty||p.ApproverKeyId==p.ExecutorKeyId||p.Profile!=AdministratorProfile||p.ExpectedEpoch<0||
            p.Nonce.Length!=32||p.TrustManifestHash.Length!=32||p.ExpiresAt<=p.IssuedAt||p.ExpiresAt-p.IssuedAt>TimeSpan.FromMinutes(15)||
            p.AdministratorExpiresAt<=p.ExpiresAt||p.AdministratorExpiresAt>p.IssuedAt.AddDays(90)||
            string.IsNullOrWhiteSpace(p.Reason)||p.Reason.Length>500)throw new ArgumentException("Invalid bounded bootstrap request.");
        _=IdentityBytes(p.AdministratorIssuer,p.AdministratorSubject);_=new UTF8Encoding(false,true).GetBytes(p.Reason);
        return JsonBytes(w=>
        {
            w.WriteStartObject();w.WriteString("domain","FactoryConnect-V2:initial-bootstrap:v1");
            w.WriteString("installationId",p.InstallationId);w.WriteString("operationId",p.OperationId);w.WriteString("principalId",p.PrincipalId);
            w.WriteString("administratorIssuer",p.AdministratorIssuer);w.WriteString("administratorSubject",p.AdministratorSubject);
            w.WriteString("profile",p.Profile);w.WriteString("approverKeyId",p.ApproverKeyId);w.WriteString("executorKeyId",p.ExecutorKeyId);
            w.WriteString("issuedAt",Instant(p.IssuedAt));w.WriteString("expiresAt",Instant(p.ExpiresAt));
            w.WriteString("administratorExpiresAt",Instant(p.AdministratorExpiresAt));w.WriteNumber("expectedEpoch",p.ExpectedEpoch);
            w.WriteBase64String("nonce",p.Nonce);w.WriteString("trustManifestHash",Convert.ToHexString(p.TrustManifestHash).ToLowerInvariant());
            w.WriteString("reason",p.Reason);w.WriteEndObject();
        });
    }
    internal static byte[] IdentityBytes(string issuer,string subject)
    {
        var encoding=new UTF8Encoding(false,true);var a=encoding.GetBytes(issuer);var b=encoding.GetBytes(subject);
        if(string.IsNullOrWhiteSpace(issuer)||string.IsNullOrWhiteSpace(subject)||a.Length>2048||b.Length>2048)
            throw new ArgumentException("Exact external identity is required.");
        return JsonBytes(w=>{w.WriteStartArray();w.WriteStringValue(issuer);w.WriteStringValue(subject);w.WriteEndArray();});
    }
    private static string Instant(DateTimeOffset value)=>value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",CultureInfo.InvariantCulture);
    private static byte[] JsonBytes(Action<Utf8JsonWriter> write)
    {using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){write(w);}return stream.ToArray();}
}
