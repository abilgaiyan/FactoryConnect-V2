using System.Text.Json;

namespace FactoryConnect.AcquisitionHost;

/// <summary>
/// Local, machine-readable acquisition health snapshot. This is host telemetry,
/// not an authenticated or authoritative machine-connectivity claim.
/// </summary>
internal sealed class AcquisitionHealthSnapshot(string destination)
{
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
