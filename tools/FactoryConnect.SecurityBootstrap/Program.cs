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
        if(File.Exists(keyPath)||File.Exists(publicPath))throw new InvalidOperationException("Refusing to overwrite a custodian key.");
        using var rsa=RSA.Create(3072);
        var password=ReadSecret("New custodian key passphrase (12+ characters): ");
        try
        {
            if(password.Length<12)throw new InvalidOperationException("Passphrase must contain at least 12 characters.");
            var repeat=ReadSecret("Confirm passphrase: ");
            try
            {
                if(!password.AsSpan().SequenceEqual(repeat))throw new InvalidOperationException("Passphrases differ.");
            }
            finally { Array.Clear(repeat); }
            var encrypted=rsa.ExportEncryptedPkcs8PrivateKeyPem(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc,HashAlgorithmName.SHA256,200000));
            await WriteNew(keyPath,Encoding.ASCII.GetBytes(encrypted));
            await WriteNew(publicPath,Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem()));
        }
        finally { Array.Clear(password); }
        Console.WriteLine("Encrypted custodian key pair generated. Keep the private key under custodian control.");
        return 0;
    }
    if(args is ["sign",var privatePath,var canonicalPath,var signaturePath])
    {
        if(Path.GetFullPath(privatePath)==Path.GetFullPath(signaturePath)||Path.GetFullPath(canonicalPath)==Path.GetFullPath(signaturePath))
            throw new InvalidOperationException();
        if(File.Exists(signaturePath))throw new InvalidOperationException("Refusing to overwrite an existing signature.");
        var keyText=await File.ReadAllTextAsync(privatePath);
        using var rsa=RSA.Create();
        var password=ReadSecret("Custodian key passphrase: ");
        try { rsa.ImportFromEncryptedPem(keyText,password); }
        finally { Array.Clear(password); }
        if(rsa.KeySize<3072)throw new InvalidOperationException();
        var bytes=await File.ReadAllBytesAsync(canonicalPath);
        // Custodian must independently review the prepared payload and canonical bytes before signing.
        await WriteNew(signaturePath,rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        Console.WriteLine("Canonical request signed with the custodian private key.");
        return 0;
    }
    if(args is ["verify-signatures",var manifestPathVerify,var payloadPathVerify,var canonicalPathVerify,
        var approverSigPath,var executorSigPath])
    {
        // Diagnostic only: expired payloads can be checked offline. No authority is granted or changed.
        var manifestFile=await Read<BootstrapTrustFile>(manifestPathVerify);
        var trust=new BootstrapTrustManifest(manifestFile);
        var payload=await Read<BootstrapPayload>(payloadPathVerify);
        var canonical=BootstrapTrustManifest.Canonicalize(payload);
        var suppliedCanonical=await File.ReadAllBytesAsync(canonicalPathVerify);
        var bytesMatch=canonical.AsSpan().SequenceEqual(suppliedCanonical);
        Console.WriteLine($"Canonical bytes match: {bytesMatch}");
        var manifestHashMatch=CryptographicOperations.FixedTimeEquals(payload.TrustManifestHash,trust.Hash);
        Console.WriteLine($"Manifest hash matches payload: {manifestHashMatch}");
        var identityMatch=payload.InstallationId==trust.InstallationId &&
            payload.AdministratorIssuer==trust.AllowedAdministratorIssuer;
        Console.WriteLine($"Installation and issuer match: {identityMatch}");
        var diagnosticApprover=manifestFile.Keys.SingleOrDefault(k=>k.KeyId==payload.ApproverKeyId);
        var diagnosticExecutor=manifestFile.Keys.SingleOrDefault(k=>k.KeyId==payload.ExecutorKeyId);
        bool VerifyOne(BootstrapCustodian? custodian,string signaturePath)
        {
            if(custodian is null)return false;
            using var rsa=RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(custodian.PublicKeySpki,out _);
            return rsa.VerifyData(canonical,File.ReadAllBytes(signaturePath),
                HashAlgorithmName.SHA256,RSASignaturePadding.Pss);
        }
        var firstValid=VerifyOne(diagnosticApprover,approverSigPath);
        var secondValid=VerifyOne(diagnosticExecutor,executorSigPath);
        Console.WriteLine($"Approver signature valid against manifest: {firstValid}");
        Console.WriteLine($"Executor signature valid against manifest: {secondValid}");
        var distinct=diagnosticApprover is not null && diagnosticExecutor is not null &&
            diagnosticApprover.CustodianId!=diagnosticExecutor.CustodianId;
        Console.WriteLine($"Distinct custodians: {distinct}");
        Console.WriteLine($"Request expired: {payload.ExpiresAt<=DateTimeOffset.UtcNow}");
        // Exit status reflects cryptographic diagnostics, not live bootstrap authorization.
        return bytesMatch&&manifestHashMatch&&identityMatch&&firstValid&&secondValid&&distinct?0:1;
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
        Console.Error.WriteLine("Usage: verify-signatures <manifest.json> <payload.json> <canonical.bin> <approver.sig> <executor.sig>");
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

static char[] ReadSecret(string prompt)
{
    if(Console.IsInputRedirected)throw new InvalidOperationException("Interactive passphrase entry required.");
    Console.Error.Write(prompt);
    var password=new List<char>();
    while(true)
    {
        var k=Console.ReadKey(intercept:true);
        if(k.Key==ConsoleKey.Enter)break;
        if(k.Key==ConsoleKey.Backspace)
        {
            if(password.Count>0)password.RemoveAt(password.Count-1);
            continue;
        }
        if(k.KeyChar!=0)password.Add(k.KeyChar);
    }
    Console.Error.WriteLine();
    return password.ToArray();
}
