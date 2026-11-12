using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FactoryConnect.Api;

/// <summary>
/// Imports only MTConnect machine addresses from a trusted local Edge configuration file.
/// Never merges the Edge document's security, database or identity settings into the API.
/// </summary>
internal static class EdgeMtConnectConfiguration
{
    internal static void Load(WebApplicationBuilder builder)
    {
        var path = builder.Configuration["MTConnect:EdgeConfigurationFile"];
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("MTConnect:EdgeConfigurationFile must be an absolute path.");
        using var file = File.OpenRead(path);
        if (file.Length > 2_000_000)
            throw new InvalidOperationException("Edge configuration exceeds the 2 MB limit.");
        using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("MTConnect", out var mtconnect) ||
            !mtconnect.TryGetProperty("Machines", out var machines) ||
            machines.ValueKind != JsonValueKind.Array || machines.GetArrayLength() > 500)
            throw new InvalidOperationException("Edge configuration must contain MTConnect.Machines.");
        var values = new Dictionary<string, string?>();
        var seen = new HashSet<Guid>();
        var index = 0;
        foreach (var machine in machines.EnumerateArray())
        {
            if (machine.ValueKind != JsonValueKind.Object ||
                !machine.TryGetProperty("MachineId", out var idValue) ||
                !Guid.TryParse(idValue.GetString(), out var machineId) ||
                !seen.Add(machineId) ||
                !machine.TryGetProperty("DeviceKey", out var keyValue) ||
                string.IsNullOrWhiteSpace(keyValue.GetString()) ||
                !machine.TryGetProperty("BaseUri", out var uriValue) ||
                !Uri.TryCreate(uriValue.GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Invalid or duplicate MTConnect machine entry in Edge configuration.");
            var prefix = $"MTConnect:Machines:{index++}:";
            values[prefix + "MachineId"] = machineId.ToString();
            values[prefix + "DeviceKey"] = keyValue.GetString();
            values[prefix + "BaseUri"] = uri.ToString();
        }
        builder.Configuration.AddInMemoryCollection(values);
    }
}
