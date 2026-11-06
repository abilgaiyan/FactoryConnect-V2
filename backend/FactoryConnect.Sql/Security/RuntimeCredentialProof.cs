using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record RuntimeCredentialPayload(Guid InstallationId,Guid OperationId,Guid ActorPrincipalId,
    string ActorIssuer,string ActorSubject,Guid RuntimeIdentityId,string RuntimeIssuer,string RuntimeSubject,
    string Action,long ExpectedGeneration,byte[] PublicKeySpki,DateTimeOffset ValidFrom,DateTimeOffset CredentialExpiresAt,
    Guid ApproverKeyId,Guid ExecutorKeyId,DateTimeOffset IssuedAt,DateTimeOffset ExpiresAt,long ExpectedEpoch,
    byte[] Nonce,byte[] BootstrapTrustHash,string Reason);
public sealed record SignedRuntimeCredential(RuntimeCredentialPayload Payload,IReadOnlyList<BootstrapSignature> Signatures,byte[] PossessionSignature);

/// <summary>Offline custody approval and new-key possession; this is not a transport authentication mechanism.</summary>
public static class RuntimeCredentialProof
{
    public static byte[] Canonicalize(RuntimeCredentialPayload p)
    {
        if(p.InstallationId==Guid.Empty||p.OperationId==Guid.Empty||p.ActorPrincipalId==Guid.Empty||p.RuntimeIdentityId==Guid.Empty||
            p.ActorPrincipalId==p.RuntimeIdentityId||p.ApproverKeyId==Guid.Empty||p.ExecutorKeyId==Guid.Empty||p.ApproverKeyId==p.ExecutorKeyId||
            p.ExpectedGeneration<0||p.ExpectedGeneration==long.MaxValue||p.ExpectedEpoch<0||p.Nonce.Length!=32||p.BootstrapTrustHash.Length!=32||
            p.Action is not ("enroll" or "rotate" or "revoke")||p.ExpiresAt<=p.IssuedAt||p.ExpiresAt-p.IssuedAt>TimeSpan.FromMinutes(15)||
            string.IsNullOrWhiteSpace(p.Reason)||p.Reason.Length>500||
            p.Action=="enroll"&&p.ExpectedGeneration!=0||p.Action!="enroll"&&p.ExpectedGeneration==0)
            throw new ArgumentException("Invalid bounded runtime credential operation.");
        _=BootstrapTrustManifest.IdentityBytes(p.ActorIssuer,p.ActorSubject);_=BootstrapTrustManifest.IdentityBytes(p.RuntimeIssuer,p.RuntimeSubject);
        _=new UTF8Encoding(false,true).GetBytes(p.Reason);
        if(p.Action=="revoke")
        {if(p.PublicKeySpki.Length!=0||p.ValidFrom!=DateTimeOffset.UnixEpoch||p.CredentialExpiresAt!=DateTimeOffset.UnixEpoch)throw new ArgumentException("Revocation carries no new credential.");}
        else
        {
            ValidateKey(p.PublicKeySpki);
            if(p.CredentialExpiresAt<=p.ValidFrom||p.CredentialExpiresAt>p.IssuedAt.AddDays(90)||p.ValidFrom>p.IssuedAt)
                throw new ArgumentException("Credential must start immediately and expire within 90 days.");
        }
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();w.WriteString("domain","FactoryConnect-V2:runtime-credential-operation:v1");
            w.WriteString("installationId",p.InstallationId);w.WriteString("operationId",p.OperationId);w.WriteString("actorPrincipalId",p.ActorPrincipalId);
            w.WriteString("actorIssuer",p.ActorIssuer);w.WriteString("actorSubject",p.ActorSubject);w.WriteString("runtimeIdentityId",p.RuntimeIdentityId);
            w.WriteString("runtimeIssuer",p.RuntimeIssuer);w.WriteString("runtimeSubject",p.RuntimeSubject);w.WriteString("action",p.Action);
            w.WriteString("expectedGeneration",p.ExpectedGeneration.ToString(CultureInfo.InvariantCulture));w.WriteBase64String("publicKeySpki",p.PublicKeySpki);
            w.WriteString("validFrom",Instant(p.ValidFrom));w.WriteString("credentialExpiresAt",Instant(p.CredentialExpiresAt));
            w.WriteString("approverKeyId",p.ApproverKeyId);w.WriteString("executorKeyId",p.ExecutorKeyId);
            w.WriteString("issuedAt",Instant(p.IssuedAt));w.WriteString("expiresAt",Instant(p.ExpiresAt));w.WriteNumber("expectedEpoch",p.ExpectedEpoch);
            w.WriteBase64String("nonce",p.Nonce);w.WriteString("bootstrapTrustHash",Convert.ToHexString(p.BootstrapTrustHash).ToLowerInvariant());w.WriteString("reason",p.Reason);w.WriteEndObject();
        }
        var bytes=stream.ToArray();if(bytes.Length>16384)throw new ArgumentException("Runtime credential operation exceeds the receipt limit.");return bytes;
    }
    public static void Verify(BootstrapTrustManifest trust,SignedRuntimeCredential request,DateTimeOffset now)
    {
        VerifyAuthorizedBytes(trust,request,Canonicalize(request.Payload),now);
    }
    internal static void VerifyAuthorizedBytes(BootstrapTrustManifest trust,SignedRuntimeCredential request,byte[] bytes,DateTimeOffset now)
    {
        var p=request.Payload;
        if(trust.InstallationId!=p.InstallationId||p.ActorIssuer!=trust.AllowedAdministratorIssuer||
            !CryptographicOperations.FixedTimeEquals(trust.Hash,p.BootstrapTrustHash)||p.IssuedAt>now||p.ExpiresAt<=now||
            p.Action!="revoke"&&p.CredentialExpiresAt<=now||request.Signatures.Count!=2)throw new SecurityDeniedException();
        var first=trust.Keys.SingleOrDefault(k=>k.KeyId==p.ApproverKeyId);var second=trust.Keys.SingleOrDefault(k=>k.KeyId==p.ExecutorKeyId);
        if(first is null||second is null||first.CustodianId==second.CustodianId)throw new SecurityDeniedException();
        var seen=new HashSet<Guid>();
        foreach(var proof in request.Signatures)
        {
            var key=proof.KeyId==first.KeyId?first:proof.KeyId==second.KeyId?second:null;
            if(key is null||!seen.Add(proof.KeyId)||key.ValidFrom>p.IssuedAt||key.ExpiresAt<p.ExpiresAt||key.ExpiresAt<=now||
                key.Issuer==p.ActorIssuer&&key.Subject==p.ActorSubject||key.Issuer==p.RuntimeIssuer&&key.Subject==p.RuntimeSubject)
                throw new SecurityDeniedException();
            using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(key.PublicKeySpki,out _);
            if(!rsa.VerifyData(bytes,proof.Signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new SecurityDeniedException();
        }
        if(p.Action=="revoke")
        {if(request.PossessionSignature.Length!=0)throw new SecurityDeniedException();return;}
        if(trust.Keys.Any(k=>k.PublicKeySpki.AsSpan().SequenceEqual(p.PublicKeySpki)))throw new SecurityDeniedException();
        using var runtime=RSA.Create();runtime.ImportSubjectPublicKeyInfo(p.PublicKeySpki,out _);
        if(!runtime.VerifyData(bytes,request.PossessionSignature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new SecurityDeniedException();
    }
    internal static void ValidateKey(byte[] spki)
    {
        if(spki.Length>2048)throw new ArgumentException("Oversized SPKI.");
        using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(spki,out var used);
        if(used!=spki.Length||!spki.AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo())||rsa.KeySize is <3072 or >8192||
            !rsa.ExportParameters(false).Exponent.AsSpan().SequenceEqual(new byte[]{1,0,1}))throw new ArgumentException("Canonical RSA 3072–8192 SPKI with exponent 65537 required.");
    }
    private static string Instant(DateTimeOffset t)=>t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",CultureInfo.InvariantCulture);
}
