using System.Text.Json;

namespace FactoryConnect.AcquisitionHost;

/// <summary>
/// Local, machine-readable acquisition health snapshot. This is host telemetry,
/// not an authenticated or authoritative machine-connectivity claim.
/// </summary>
internal sealed class AcquisitionHealthSnapshot(string destination)
{
    internal static object Inspect(string destination, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        var file = new FileInfo(destination);
        if (!file.Exists) return new { status = "missing", operational = false, reason = "No acquisition health snapshot" };
        if (file.Length is < 2 or > 65536) return new { status = "invalid", operational = false, reason = "Snapshot size invalid" };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new { status = "invalid", operational = false, reason = "Snapshot state invalid" };
            var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var age = DateTimeOffset.UtcNow - lastWrite;
            if (age < TimeSpan.Zero || age > maximumAge)
                return new { status = "stale", operational = false, lastWriteAt = lastWrite, ageSeconds = Math.Max(0, (long)age.TotalSeconds) };
            var value = state.GetString();
            return new { status = value, operational = value is "idle" or "committed", lastWriteAt = lastWrite,
                ageSeconds = Math.Max(0, (long)age.TotalSeconds), snapshot = root.Clone() };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new { status = "invalid", operational = false, reason = "Snapshot unavailable or malformed" }; }
    }

    internal void Write(object report)
    {
        var path = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(report);
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
