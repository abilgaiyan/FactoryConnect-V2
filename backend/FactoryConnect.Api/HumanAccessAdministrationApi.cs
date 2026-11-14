using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

/// <summary>Audited human enrollment and delegated access; never creates root grants or permits self-grants.</summary>
public static class HumanAccessAdministrationApi
{
    private sealed record EnrollInput(Guid OperationId, Guid PrincipalId, string Issuer, string Subject,
        long ExpectedEpoch, string Reason);
    private sealed record GrantInput(Guid OperationId, Guid GrantId, Guid GranteeId, Guid ParentGrantId,
        string Permission, string ScopeKind, Guid? SiteId, Guid? MachineId, bool UseAllowed, bool CanDelegate,
        DateTimeOffset EffectiveFrom, DateTimeOffset ExpiresAt, long ExpectedEpoch, string Reason);

    public static void MapHumanAccessAdministration(this WebApplication app)
    {
        app.MapGet("/api/admin/v2/security/access-context", async (HttpContext http,
            IConfiguration config, CancellationToken ct) =>
            await Execute(http, config, (authority, hash) => authority.ReadAccessAdministrationAsync(hash, ct), ct))
            .RequireRateLimiting("security-boundary");
        app.MapPost("/api/admin/v2/security/humans", async (EnrollInput input, HttpContext http,
            IConfiguration config, CancellationToken ct) =>
            await Execute(http, config, (authority, hash) => authority.EnrollHumanAsync(hash,
                new HumanEnrollmentRequest(input.OperationId, input.PrincipalId, input.Issuer,
                    input.Subject, input.ExpectedEpoch, input.Reason), ct), ct))
            .RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/security/permission-grants", async (GrantInput input, HttpContext http,
            IConfiguration config, CancellationToken ct) =>
            await Execute(http, config, (authority, hash) => authority.GrantPermissionAsync(hash,
                new PermissionGrantRequest(input.OperationId, input.GrantId, input.GranteeId,
                    input.ParentGrantId, input.Permission,
                    Scope(input.ScopeKind, input.SiteId, input.MachineId),
                    input.UseAllowed, input.CanDelegate, input.EffectiveFrom, input.ExpiresAt,
                    input.ExpectedEpoch, input.Reason), ct), ct))
            .RequireRateLimiting("security-boundary");
    }

    private static AuthorizationScope Scope(string kind, Guid? site, Guid? machine) => kind switch
    {
        "I" when site is null && machine is null => AuthorizationScope.Installation,
        "S" when site is { } s && machine is null => AuthorizationScope.Site(s),
        "M" when site is { } s && machine is { } m => AuthorizationScope.Machine(s, m),
        _ => throw new ArgumentException("Invalid permission scope.")
    };

    private static async Task<IResult> Execute(HttpContext http, IConfiguration config,
        Func<SqlSecurityAuthority, byte[], Task<byte[]>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
        if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
            string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
            return Results.Problem(statusCode: 503, title: "Human access authority unavailable");
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
            var json = await action(new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId), hash);
            return Results.Content(Encoding.UTF8.GetString(json), "application/json");
        }
        catch (SecurityDeniedException) { return Results.Problem(statusCode: 403, title: "Access management denied"); }
        catch (SecurityConflictException) { return Results.Conflict(new { error = "Authority changed; refresh the epoch before retrying." }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid access management request." }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "human-access", "Human access authority unavailable"); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
