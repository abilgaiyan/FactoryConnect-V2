using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FactoryConnect.Sql.Security;

internal static class DeveloperRuntimeCertificateGenerator
{
    internal static async Task GenerateAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        var privatePath = Path.Combine(root, "private.pem");
        var publicPath = Path.Combine(root, "public.pem");
        var leafPath = Path.Combine(root, "leaf.der");
        var anchorPath = Path.Combine(root, "anchor.der");
        if (!File.Exists(privatePath) || !File.Exists(publicPath))
            throw new InvalidOperationException("The existing runtime key pair is required.");
        if (File.Exists(leafPath) || File.Exists(anchorPath))
            throw new InvalidOperationException("Refusing to overwrite certificate output.");

        var secret = ReadSecret("New runtime key passphrase: ");
        try
        {
            using var runtimeKey = RSA.Create();
            runtimeKey.ImportFromEncryptedPem(await File.ReadAllTextAsync(privatePath), secret);
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(await File.ReadAllTextAsync(publicPath));
            if (runtimeKey.KeySize != 3072 || publicKey.KeySize != 3072 ||
                !runtimeKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
                throw new InvalidOperationException("Runtime public/private key mismatch or unsupported key size.");

            using var caKey = RSA.Create(3072);
            var now = DateTimeOffset.UtcNow;
            var caRequest = new CertificateRequest(
                "CN=FactoryConnect V2 Local Runtime Development CA", caKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
            using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(60));

            var leafRequest = new CertificateRequest(
                "CN=FactoryConnect V2 Local Simulator Runtime", publicKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") };
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
            leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            using var leaf = leafRequest.Create(ca, now.AddMinutes(-5), now.AddDays(30), serial);

            var leafDer = leaf.Export(X509ContentType.Cert);
            var anchorDer = ca.Export(X509ContentType.Cert);
            // Validate against the same profile used by the SQL certificate registration path.
            RuntimeCertificateProfile.ValidateShape(leaf, ca, now);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            if (!chain.Build(leaf) || chain.ChainElements.Count != 2)
                throw new InvalidOperationException("Generated certificate chain failed validation.");

            await using (var output = new FileStream(leafPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(leafDer);
            try
            {
                await using var output = new FileStream(anchorPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await output.WriteAsync(anchorDer);
            }
            catch
            {
                File.Delete(leafPath);
                throw;
            }
            Console.WriteLine("Runtime client certificate and local trust anchor generated and verified.");
            Console.WriteLine($"Leaf: {leafPath}");
            Console.WriteLine($"Anchor: {anchorPath}");
        }
        finally
        {
            Array.Clear(secret);
        }
    }

    private static char[] ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Interactive passphrase entry required.");
        Console.Error.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
            }
            else if (key.KeyChar != 0) chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return chars.ToArray();
    }
}
