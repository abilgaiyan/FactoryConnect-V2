using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health", NotReady);
app.MapGet("/health/ready", NotReady);
app.MapGet("/dashboard/config", () => Results.Ok(new
{
    reportingBasePath = "/",
    requestTimeoutMilliseconds = 30000,
    sources = Array.Empty<object>()
}));

// A real identity verifier must be supplied by the trusted host integration.
// No development verifier, password bypass, or bearer minted from client claims is registered.
app.MapPost("/api/security/v2/human-sessions", async (
    HttpContext http, IConfiguration config, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
    var verifier = http.RequestServices.GetService<IHumanIdentityVerifier>();
    if (verifier is null
        || !Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
        || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
        return NotReady();
    if (http.Request.ContentLength is > 65536)
        return Results.Problem(statusCode: 413, title: "Authentication assertion too large");
    string? assertion;
    try
    {
        // Bound the actual streamed body, including chunked requests without Content-Length.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > 65536)
                return Results.Problem(statusCode: 413, title: "Authentication assertion too large");
            buffer.Write(chunk, 0, read);
        }
        assertion = new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray());
    }
    catch (Exception e) when (e is IOException or System.Text.DecoderFallbackException) { return Results.BadRequest(); }
    if (string.IsNullOrWhiteSpace(assertion) || assertion.Length > 65536)
        return Results.BadRequest();
    try
    {
        var issued = await new SqlHumanSessionAuthority(
            config.GetConnectionString("Evidence")!, installationId, verifier).IssueAsync(assertion, ct);
        try
        {
            return Results.Ok(new { bearer = Convert.ToBase64String(issued.Token),
                principalId = issued.PrincipalId, expiresAt = issued.ExpiresAt });
        }
        finally { CryptographicOperations.ZeroMemory(issued.Token); }
    }
    catch (SecurityDeniedException) { return Results.Unauthorized(); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception) { return Results.Problem(statusCode: 503, title: "Session authority unavailable"); }
});

// Machine selection is derived from current SQL authorization, never browser-supplied role claims.
// A principal with no eligible machines receives an empty list, not an unrestricted inventory.
app.MapGet("/api/commissioning/v2/evidence-machines", async (
    HttpContext http, IConfiguration config, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
    if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
        || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
        || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
        return NotReady();
    if (!http.Request.Headers.TryGetValue("Authorization", out var authorization)
        || authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal)
        || authorization[0]!.Length > 192) return Results.Unauthorized();
    byte[] bearer;
    try { bearer = Convert.FromBase64String(authorization[0]![7..]); }
    catch (FormatException) { return Results.Unauthorized(); }
    if (bearer.Length != 32) { CryptographicOperations.ZeroMemory(bearer); return Results.Unauthorized(); }
    var hash = SHA256.HashData(bearer);
    CryptographicOperations.ZeroMemory(bearer);
    try
    {
        var machines = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
            .ReadOperatorEvidenceMachinesAsync(hash, siteId, ct);
        return Results.Ok(new { machines });
    }
    catch (SecurityDeniedException) { return Results.Unauthorized(); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception) { return Results.Problem(statusCode: 503, title: "Evidence authority unavailable"); }
    finally { CryptographicOperations.ZeroMemory(hash); }
});

// This route is deliberately opt-in. It uses an existing, independently issued human
// session and SQL's current commissioning.read authorization; it never issues sessions.
app.MapGet("/api/commissioning/v2/machines/{machineId:guid}/evidence", async (
    Guid machineId, HttpContext http, IConfiguration config, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
        || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
        || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
        return NotReady();

    // Do not accept bearer credentials on plaintext connections or from URL parameters.
    if (!http.Request.IsHttps)
        return Results.Problem(statusCode: 403, title: "HTTPS required");
    if (!http.Request.Headers.TryGetValue("Authorization", out var authorization)
        || authorization.Count != 1
        || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal)
        || authorization[0]!.Length > 192)
        return Results.Unauthorized();

    byte[] bearer;
    try { bearer = Convert.FromBase64String(authorization[0]![7..]); }
    catch (FormatException) { return Results.Unauthorized(); }
    if (bearer.Length != 32) { CryptographicOperations.ZeroMemory(bearer); return Results.Unauthorized(); }
    var sessionHash = SHA256.HashData(bearer);
    CryptographicOperations.ZeroMemory(bearer);
    try
    {
        var stream = config["Evidence:StreamKey"];
        if (string.IsNullOrWhiteSpace(stream)) return NotReady();
        var take = 20;
        if (http.Request.Query.TryGetValue("take", out var requested)
            && (!int.TryParse(requested.ToString(), out take) || take is <1 or >100))
            return Results.BadRequest(new { error = "take must be between 1 and 100" });
        var rows = await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
            .ReadMtConnectEvidenceAsync(sessionHash, siteId, machineId, stream, take, ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { machineId, siteId, streamKey = stream,
            evidenceKind = "historical-retained-evidence", currentConnectivity = (string?)null,
            rows });
    }
    catch (SecurityDeniedException) { return Results.Unauthorized(); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception)
    {
        return Results.Problem(statusCode: 503, title: "Evidence authority unavailable");
    }
    finally { CryptographicOperations.ZeroMemory(sessionHash); }
});

app.MapMethods("/api/{**path}", ["GET", "POST"], NotReady);
app.Run();

static IResult NotReady() => Results.Problem(
    statusCode: StatusCodes.Status503ServiceUnavailable,
    title: "V2 evidence authority is not configured",
    detail: "The requested evidence authority is not available.");
