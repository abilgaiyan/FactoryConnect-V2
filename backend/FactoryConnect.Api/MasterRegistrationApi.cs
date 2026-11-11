using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

public static class MasterRegistrationApi
{
    private sealed record RegistrationRequest(Guid OperationId, string Kind, Guid? SiteId, string DisplayName);

    public static void MapMasterRegistration(this WebApplication app)
    {
        app.MapGet("/api/admin/v2/sites", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
                string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.Problem(statusCode: 503, title: "Master-data authority unavailable");
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
                var sites = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ReadAdministrationSitesAsync(hash, ct);
                return Results.Ok(new { sites });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "administration-sites", "Site inventory unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapGet("/api/admin/v2/machines", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
                string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.Problem(statusCode: 503, title: "Master-data authority unavailable");
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
                return Results.Ok(new { machines });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "administration-machines", "Machine inventory unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/master-registration", async (
            HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps)
                return Results.Problem(statusCode: 403, title: "HTTPS required");
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
                string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.Problem(statusCode: 503, title: "Master-data authority unavailable");
            if (!http.Request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal) ||
                authorization[0]!.Length > 192)
                return Results.Unauthorized();
            byte[] bearer;
            try { bearer = Convert.FromBase64String(authorization[0]![7..]); }
            catch (FormatException) { return Results.Unauthorized(); }
            if (bearer.Length != 32)
            {
                CryptographicOperations.ZeroMemory(bearer);
                return Results.Unauthorized();
            }
            var sessionHash = SHA256.HashData(bearer);
            CryptographicOperations.ZeroMemory(bearer);
            try
            {
                RegistrationRequest? request;
                try { request = await http.Request.ReadFromJsonAsync<RegistrationRequest>(cancellationToken: ct); }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Invalid registration JSON" }); }
                if (request is null || request.OperationId == Guid.Empty ||
                    request.Kind is not ("site" or "line" or "machine") ||
                    (request.Kind == "site" ? request.SiteId is not null :
                        request.SiteId is null || request.SiteId == Guid.Empty) ||
                    string.IsNullOrWhiteSpace(request.DisplayName) ||
                    request.DisplayName.Trim().Length > 200)
                    return Results.BadRequest(new { error = "Invalid registration request" });
                var recordId = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .RegisterMasterRecordAsync(sessionHash, request.OperationId, request.Kind,
                        request.SiteId, request.DisplayName, ct);
                return Results.Ok(new { recordId, request.OperationId, request.Kind });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (SecurityConflictException) { return Results.Conflict(new { error = "Operation ID conflict" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Registration conflicts with existing master data" }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "master-registration", "Master-data authority unavailable"); }
            finally { CryptographicOperations.ZeroMemory(sessionHash); }
        }).RequireRateLimiting("security-boundary");
    }
}
