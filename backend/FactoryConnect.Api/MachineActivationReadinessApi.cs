using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

/// <summary>Authoritative, read-only commissioning readiness for the administration workflow.
/// Does not issue runtime credentials, activate machines or acknowledge acquisition.</summary>
public static class MachineActivationReadinessApi
{
    public static void MapMachineActivationReadiness(this WebApplication app)
    {
        app.MapGet("/api/admin/v2/sites/{siteId:guid}/activation-readiness", async (
            Guid siteId, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps)
                return Results.Problem(statusCode: 403, title: "HTTPS required");
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
                string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.Problem(statusCode: 503, title: "Commissioning authority unavailable");
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
            var hash = SHA256.HashData(bearer);
            CryptographicOperations.ZeroMemory(bearer);
            try
            {
                var readiness = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ReadCommissioningSiteAsync(hash, siteId, ct);
                return Results.Ok(readiness);
            }
            catch (SecurityDeniedException)
            {
                // Never log bearer material or session hashes.
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("FactoryConnect.Api.Commissioning")
                    .LogWarning("Commissioning authority denied readiness; TraceId={TraceId}; SiteId={SiteId}; Permission={Permission}",
                        http.TraceIdentifier, siteId, "commissioning.read");
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Commissioning access denied",
                    detail: "The session cannot read readiness for this site. Verify the commissioning.read site grant and sign in again.",
                    extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return AuthorityDiagnostics.Unavailable(http, ex, "activation-readiness", "Activation readiness unavailable");
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");
    }
}
