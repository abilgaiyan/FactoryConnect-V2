using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

public static class MasterRegistrationApi
{
    private sealed record CommissioningRequest(Guid OperationId, Guid MachineId, Guid ProductionLineId, string DeviceId);
    private sealed record RegistrationRequest(Guid OperationId, string Kind, Guid? SiteId, string DisplayName, Guid? MachineId = null);

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

        app.MapGet("/api/admin/v2/commissioning-assignments", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
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
                var assignments = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ReadCommissioningAssignmentsAsync(hash, ct);
                return Results.Ok(new { assignments });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "commissioning-assignments", "Commissioning assignments unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapGet("/api/admin/v2/lines", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
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
                var lines = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ReadAdministrationLinesAsync(hash, ct);
                return Results.Ok(new { lines });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "administration-lines", "Production-line inventory unavailable"); }
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
                    request.DisplayName.Trim().Length > 200 ||
                    (request.MachineId.HasValue && (request.Kind != "machine" || request.MachineId == Guid.Empty)))
                    return Results.BadRequest(new { error = "Invalid registration request" });
                var recordId = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .RegisterMasterRecordAsync(sessionHash, request.OperationId, request.Kind,
                        request.SiteId, request.DisplayName, ct, request.MachineId);
                return Results.Ok(new { recordId, request.OperationId, request.Kind });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (SecurityConflictException) { return Results.Conflict(new { error = "Operation ID conflict" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Registration conflicts with existing master data" }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "master-registration", "Master-data authority unavailable"); }
            finally { CryptographicOperations.ZeroMemory(sessionHash); }
        }).RequireRateLimiting("security-boundary");
        app.MapPost("/api/admin/v2/commissioning-assignments", async (
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
                CommissioningRequest? request;
                try { request = await http.Request.ReadFromJsonAsync<CommissioningRequest>(cancellationToken: ct); }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Invalid commissioning JSON" }); }
                if (request is null || request.OperationId == Guid.Empty ||
                    request.MachineId == Guid.Empty || request.ProductionLineId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Trim().Length > 200)
                    return Results.BadRequest(new { error = "Invalid commissioning assignment" });
                await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .SaveCommissioningAssignmentAsync(sessionHash, request.OperationId,
                        request.MachineId, request.ProductionLineId, request.DeviceId, ct);
                return Results.Ok(new { saved = true, request.OperationId, acquisitionActivated = false });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (SecurityConflictException) { return Results.Conflict(new { error = "Operation ID conflict" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Machine or production line invalid for commissioning" }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "commissioning-save", "Commissioning authority unavailable"); }
            finally { CryptographicOperations.ZeroMemory(sessionHash); }
        }).RequireRateLimiting("security-boundary");
    }
}
