using System.Security.Cryptography;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

/// <summary>Human-session-only configuration operations. All authorization and persistence remain in SQL authority.</summary>
public static class ConfigurationAdministrationApi
{
    private sealed record SubmitInput(Guid OperationId, PackageDefinition Package, long ExpectedSecurityEpoch, string Reason);
    private sealed record ReviewInput(Guid OperationId, Guid ReviewId, Guid PackageRevisionId,
        string ContentHash, bool Approve, long ExpectedSecurityEpoch, string Reason);

    public static void MapConfigurationAdministration(this WebApplication app)
    {
        app.MapPost("/api/admin/v2/sites/{siteId:guid}/configuration/submissions",
            async (Guid siteId, SubmitInput request, HttpContext http, IConfiguration config, CancellationToken ct) =>
            {
                if (request.Package is null || request.Package.SiteId != siteId)
                    return Results.BadRequest(new { error = "Package site must match route." });
                return await Execute(http, config, (authority, hash) =>
                    authority.SubmitConfigurationAsync(hash, request.OperationId, request.Package,
                        request.ExpectedSecurityEpoch, request.Reason, ct), ct);
            }).RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/sites/{siteId:guid}/configuration/reviews",
            async (Guid siteId, ReviewInput request, HttpContext http, IConfiguration config, CancellationToken ct) =>
            {
                if (request.ContentHash is not { Length: 64 } contentHash ||
                    !System.Text.RegularExpressions.Regex.IsMatch(contentHash, "^[0-9a-fA-F]{64}$"))
                    return Results.BadRequest(new { error = "Expected SHA-256 hexadecimal content hash." });
                var contentHashBytes = Convert.FromHexString(contentHash);
                return await Execute(http, config, (authority, hash) =>
                    authority.ReviewConfigurationAsync(hash, new PackageReviewRequest(
                        request.OperationId, request.ReviewId, siteId, request.PackageRevisionId,
                        contentHashBytes, request.Approve,
                        request.ExpectedSecurityEpoch, request.Reason), ct), ct);
            }).RequireRateLimiting("security-boundary");

        app.MapGet("/api/admin/v2/configuration/operations/{operationId:guid}",
            async (Guid operationId, HttpContext http, IConfiguration config, CancellationToken ct) =>
                await Execute(http, config, (authority, hash) =>
                    authority.ReadConfigurationOperationAsync(hash, operationId, ct), ct))
            .RequireRateLimiting("security-boundary");
    }

    private static async Task<IResult> Execute(HttpContext http, IConfiguration config,
        Func<SqlSecurityAuthority, byte[], Task<byte[]>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
        if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
            string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
            return Results.Problem(statusCode: 503, title: "Configuration authority unavailable");
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
        var sessionHash = SHA256.HashData(bearer);
        CryptographicOperations.ZeroMemory(bearer);
        try
        {
            var result = await action(new SqlSecurityAuthority(
                config.GetConnectionString("Evidence")!, installationId), sessionHash);
            return Results.Content(System.Text.Encoding.UTF8.GetString(result), "application/json");
        }
        catch (SecurityDeniedException) { return Results.Problem(statusCode: 403, title: "Configuration access denied"); }
        catch (SecurityConflictException) { return Results.Conflict(new { error = "Configuration authority changed; refresh before retrying." }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid configuration request." }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return AuthorityDiagnostics.Unavailable(http, ex, "configuration-administration",
                "Configuration authority unavailable");
        }
        finally { CryptographicOperations.ZeroMemory(sessionHash); }
    }
}
