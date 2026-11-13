using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Security;

var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,WriteIndented=true};
try
{
    // Offline custodian operations. Private keys are never read during SQL bootstrap apply.
    if(args is ["keygen", var keyPath, var publicPath])
    {
        if(Path.GetFullPath(keyPath)==Path.GetFullPath(publicPath))throw new InvalidOperationException();
        using var rsa=RSA.Create(3072);
        // PKCS#8 PEM contains sensitive private material. Store outside the repository, restrict ACLs, and never share it.
        await WriteNew(keyPath,Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
        await WriteNew(publicPath,Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem()));
        Console.WriteLine("Custodian key pair generated. Keep the private key offline under custodian control.");
        return 0;
    }
    if(args is ["sign",var privatePath,var canonicalPath,var signaturePath])
    {
        if(Path.GetFullPath(privatePath)==Path.GetFullPath(signaturePath)||Path.GetFullPath(canonicalPath)==Path.GetFullPath(signaturePath))
            throw new InvalidOperationException();
        var keyText=await File.ReadAllTextAsync(privatePath);
        using var rsa=RSA.Create();
        rsa.ImportFromPem(keyText);
        if(rsa.KeySize<3072)throw new InvalidOperationException();
        var bytes=await File.ReadAllBytesAsync(canonicalPath);
        // Verify the input is canonical by the ordinary bootstrap command before distributing it for signature.
        await WriteNew(signaturePath,rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        Console.WriteLine("Canonical request signed with the custodian private key.");
        return 0;
    }
    if(args is ["prepare",var manifestPath,var principal,var subject,var approver,var executor,var reason,var payloadPath,var bytesPath])
    {
        var file=await Read<BootstrapTrustFile>(manifestPath);var trust=new BootstrapTrustManifest(file);var now=DateTimeOffset.UtcNow;
        var first=file.Keys.Single(k=>k.KeyId==Guid.Parse(approver));var second=file.Keys.Single(k=>k.KeyId==Guid.Parse(executor));
        if(first.CustodianId==second.CustodianId||new[]{first,second}.Any(k=>k.ValidFrom>now||k.ExpiresAt<now.AddMinutes(10)||k.Issuer==file.AllowedAdministratorIssuer&&k.Subject==subject))
            throw new InvalidOperationException();
        var p=new BootstrapPayload(file.InstallationId,Guid.NewGuid(),Guid.Parse(principal),file.AllowedAdministratorIssuer,subject,
            BootstrapTrustManifest.AdministratorProfile,first.KeyId,second.KeyId,now,now.AddMinutes(10),now.AddDays(30),0,
            RandomNumberGenerator.GetBytes(32),trust.Hash,reason);
        var canonical=BootstrapTrustManifest.Canonicalize(p);
        if(Path.GetFullPath(payloadPath)==Path.GetFullPath(bytesPath))throw new InvalidOperationException();
        await WriteNew(payloadPath,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p,options)));
        await WriteNew(bytesPath,canonical);
        Console.WriteLine($"Prepared unsigned request. Canonical SHA-256: {Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()}");
        return 0;
    }
    if(args is ["canonicalize",var payloadPath2,var bytesPath2])
    {
        var canonical=BootstrapTrustManifest.Canonicalize(await Read<BootstrapPayload>(payloadPath2));await WriteNew(bytesPath2,canonical);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant());return 0;
    }
    if(args is ["assemble",var manifestPath2,var payloadPath3,var firstSignature,var secondSignature,var signedPath])
    {
        var trust=new BootstrapTrustManifest(await Read<BootstrapTrustFile>(manifestPath2));var p=await Read<BootstrapPayload>(payloadPath3);
        var request=new SignedBootstrapRequest(p,[new(p.ApproverKeyId,await File.ReadAllBytesAsync(firstSignature)),new(p.ExecutorKeyId,await File.ReadAllBytesAsync(secondSignature))]);
        trust.Verify(p,request.Signatures,DateTimeOffset.UtcNow);
        await WriteNew(signedPath,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,options)));Console.WriteLine("Assembled verified public bootstrap artifacts.");return 0;
    }
    if(args.Length!=6)
    {
        Console.Error.WriteLine("Usage: keygen <private.pem> <public.pem> or sign <private.pem> <canonical.bin> <signature.bin>");
        Console.Error.WriteLine("Usage: prepare <public-manifest> <principal-guid> <subject> <approver-key-guid> <executor-key-guid> <reason> <payload.json> <canonical.bin>");
        Console.Error.WriteLine("Or: canonicalize <payload.json> <canonical.bin>");
        Console.Error.WriteLine("Or: assemble <public-manifest> <payload.json> <approver.sig> <executor.sig> <signed-request.json>");
        Console.Error.WriteLine("Or: <connection-env-name> <database> <expected-server> <installation-guid> <public-manifest.json> <signed-request.json>");return 2;
    }
    var manifest=await Read<BootstrapTrustFile>(args[4]);var signed=await Read<SignedBootstrapRequest>(args[5]);
    var connection=Environment.GetEnvironmentVariable(args[0])??throw new InvalidOperationException();
    var authority=new SqlBootstrapAuthority(new InstallationTarget(connection,args[1],args[2],Guid.Parse(args[3]),false),new BootstrapTrustManifest(manifest));
    Console.WriteLine(Encoding.UTF8.GetString(await authority.ApplyAsync(signed)));return 0;
}
catch(Exception)
{
    Console.Error.WriteLine("Bootstrap operation failed. No successful initialization claimed. Verify explicit target, public trust, request/signatures, output paths and current authority.");return 1;
}
async Task<T> Read<T>(string path)
{
    var raw=await File.ReadAllBytesAsync(path);if(raw.Length>1048576)throw new JsonException();
    var start=raw.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF})?3:0;
    var text=new UTF8Encoding(false,true).GetString(raw,start,raw.Length-start);
    using var document=JsonDocument.Parse(text);UniqueProperties(document.RootElement);
    return document.RootElement.Deserialize<T>(options)??throw new InvalidOperationException();
}
static void UniqueProperties(JsonElement value)
{
    if(value.ValueKind==JsonValueKind.Object)
    {
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in value.EnumerateObject())
        {if(!names.Add(property.Name))throw new JsonException();UniqueProperties(property.Value);}
    }
    else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())UniqueProperties(item);
}
static async Task WriteNew(string path,byte[] bytes)
{await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);await stream.WriteAsync(bytes);}
