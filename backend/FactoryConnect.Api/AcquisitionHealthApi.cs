using System.Text.Json;

namespace FactoryConnect.Api;

/// <summary>Bounded local host-health read model; does not establish live machine connectivity.</summary>
internal static class AcquisitionHealthApi
{
    internal static object Read(string path, int maxAgeSeconds)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return new { status = "missing", operational = false, reason = "No acquisition snapshot" } as object;
        if (file.Length is < 2 or > 65536)
            return new { status = "invalid", operational = false, reason = "Snapshot size invalid" };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new { status = "invalid", operational = false, reason = "Snapshot status invalid" };
            var writtenAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var age = DateTimeOffset.UtcNow - writtenAt;
            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(maxAgeSeconds))
                return new { status = "stale", operational = false, writtenAt, ageSeconds = Math.Max(0, (long)age.TotalSeconds) };
            var status = state.GetString();
            return new { status, operational = status is "idle" or "committed",
                writtenAt, ageSeconds = Math.Max(0, (long)age.TotalSeconds), snapshot = root.Clone() };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new { status = "invalid", operational = false, reason = "Snapshot unavailable or malformed" }; }
    }
}
