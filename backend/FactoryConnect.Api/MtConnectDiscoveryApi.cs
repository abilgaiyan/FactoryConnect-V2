using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

/// <summary>Read-only discovery against explicitly configured Edge MTConnect machines.</summary>
public static class MtConnectDiscoveryApi
{
    private sealed record ConfiguredMachine(string MachineId, string DeviceKey, string BaseUri);

    public static void MapMtConnectDiscovery(this WebApplication app)
    {
        app.MapGet("/api/admin/v2/mtconnect/configured-machines", async (
            HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
                string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.Problem(statusCode: 503, title: "Administration authority unavailable");
            if (!http.Request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal) ||
                authorization[0]!.Length > 192) return Results.Unauthorized();
            byte[] bearer;
            try { bearer = Convert.FromBase64String(authorization[0]![7..]); }
            catch (FormatException) { return Results.Unauthorized(); }
            if (bearer.Length != 32)
            {
                CryptographicOperations.ZeroMemory(bearer);
                return Results.Unauthorized();
            }
            var hash = SHA256.HashData(bearer);
            CryptographicOperations.ZeroMemory(bearer);
            try
            {
                var machines = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ReadAdministrationMachinesAsync(hash, ct);
                var registered = machines.Where(machine => !machine.IsRetired)
                    .Select(machine => machine.MachineId).ToHashSet();
                var configured = config.GetSection("MTConnect:Machines").Get<ConfiguredMachine[]>() ?? [];
                var entries = configured.Select(machine => new
                {
                    machineId = machine.MachineId,
                    deviceKey = machine.DeviceKey,
                    registered = Guid.TryParse(machine.MachineId, out var id) && registered.Contains(id)
                }).ToArray();
                return Results.Ok(new { configured = entries, configuredCount = entries.Length });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex,
                "mtconnect-configuration-inventory", "MTConnect configuration inventory unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapGet("/api/admin/v2/mtconnect/discover/{machineId:guid}", async (
            Guid machineId, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
                string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.Problem(statusCode: 503, title: "Administration authority unavailable");
            if (!http.Request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal) ||
                authorization[0]!.Length > 192) return Results.Unauthorized();
            byte[] bearer;
            try { bearer = Convert.FromBase64String(authorization[0]![7..]); }
            catch (FormatException) { return Results.Unauthorized(); }
            if (bearer.Length != 32)
            {
                CryptographicOperations.ZeroMemory(bearer);
                return Results.Unauthorized();
            }
            var hash = SHA256.HashData(bearer);
            CryptographicOperations.ZeroMemory(bearer);
            try
            {
                // The same administration permission as machine inventory is required.
                var machines = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ReadAdministrationMachinesAsync(hash, ct);
                if (!machines.Any(machine => machine.MachineId == machineId && !machine.IsRetired))
                    return Results.NotFound(new { error = "Active registered machine not found" });

                // Read the Edge configuration as supplied to this host; no second Agent URL registry.
                var configured = config.GetSection("MTConnect:Machines").Get<ConfiguredMachine[]>() ?? [];
                var target = configured.SingleOrDefault(machine =>
                    Guid.TryParse(machine.MachineId, out var id) && id == machineId);
                if (target is null) return Results.NotFound(new { error = "Machine not configured for MTConnect discovery" });
                if (!Uri.TryCreate(target.BaseUri, UriKind.Absolute, out var baseUri) ||
                    baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo) ||
                    !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
                    return Results.Problem(statusCode: 503, title: "Configured MTConnect Agent URI invalid");
                // The endpoint is derived exclusively from trusted host configuration, never user input.
                var probeUri = new Uri(baseUri.ToString().TrimEnd('/') + "/probe");
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var response = await client.GetAsync(probeUri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem(statusCode: 502, title: "Configured MTConnect Agent probe failed");
                if (response.Content.Headers.ContentLength is > 4_000_000)
                    return Results.Problem(statusCode: 502, title: "MTConnect probe response exceeds limit");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var limited = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await source.ReadAsync(chunk, ct)) != 0)
                {
                    if (limited.Length + count > 4_000_000)
                        return Results.Problem(statusCode: 502, title: "MTConnect probe response exceeds limit");
                    limited.Write(chunk, 0, count);
                }
                limited.Position = 0;
                var devices = MtConnectProbeDevices.Parse(limited);
                var matching = devices.Where(device =>
                    string.Equals(device.Id, target.DeviceKey, StringComparison.Ordinal) ||
                    string.Equals(device.Name, target.DeviceKey, StringComparison.Ordinal) ||
                    (!string.IsNullOrWhiteSpace(device.Uuid) &&
                     string.Equals(device.Uuid, target.DeviceKey, StringComparison.OrdinalIgnoreCase))).ToArray();
                var identityStatus = matching.Length switch
                {
                    0 => "unmatched",
                    1 => "potential-match",
                    _ => "ambiguous"
                };
                return Results.Ok(new { machineId, target.DeviceKey, devices, identityStatus,
                    matchingDeviceIds = matching.Select(device => device.Id).ToArray(),
                    acquisitionActivated = false });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (InvalidDataException) { return Results.Problem(statusCode: 502, title: "Invalid MTConnect probe document"); }
            catch (System.Xml.XmlException) { return Results.Problem(statusCode: 502, title: "Invalid MTConnect probe XML"); }
            catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Configured MTConnect Agent unreachable"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            { return Results.Problem(statusCode: 504, title: "MTConnect Agent probe timed out"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "mtconnect-discovery", "MTConnect discovery unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");
    }
}
