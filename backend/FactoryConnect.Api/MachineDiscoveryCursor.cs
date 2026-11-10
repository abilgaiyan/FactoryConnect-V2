using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.Api;

/// <summary>Short-lived, installation/site/session-bound machine seek cursor.
/// The signing key is independently configured; a missing key disables continuation.</summary>
internal static class MachineDiscoveryCursor
{
    private sealed record Payload(Guid Installation, Guid Site, string Session, Guid After, long Expires);

    internal static byte[]? ReadKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        byte[] key;
        try { key = Convert.FromBase64String(configured); }
        catch (FormatException) { throw new InvalidOperationException("Evidence:MachineDiscoveryCursorKey must be base64."); }
        if (key.Length < 32) throw new InvalidOperationException("Evidence:MachineDiscoveryCursorKey must contain at least 32 bytes.");
        return key;
    }

    internal static string Issue(byte[] key, Guid installation, Guid site, byte[] sessionHash, Guid after)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(installation, site,
            Convert.ToHexString(sessionHash), after, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
        var mac = HMACSHA256.HashData(key, payload);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            + "." + Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryRead(string? cursor, byte[] key, Guid installation, Guid site,
        byte[] sessionHash, out Guid after)
    {
        after = default;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 1024) return false;
        var parts = cursor.Split('.');
        if (parts.Length != 2) return false;
        try
        {
            static byte[] Decode(string value) => Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
            var payload = Decode(parts[0]);
            var signature = Decode(parts[1]);
            if (signature.Length != 32 || !CryptographicOperations.FixedTimeEquals(
                signature, HMACSHA256.HashData(key, payload))) return false;
            var parsed = JsonSerializer.Deserialize<Payload>(payload);
            if (parsed is null || parsed.Installation != installation || parsed.Site != site ||
                parsed.After == Guid.Empty || parsed.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                parsed.Expires > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(parsed.Session), Encoding.ASCII.GetBytes(Convert.ToHexString(sessionHash))))
                return false;
            after = parsed.After;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}
