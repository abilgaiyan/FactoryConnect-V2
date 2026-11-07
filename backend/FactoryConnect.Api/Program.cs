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

// This route is deliberately opt-in. It uses an existing, independently issued human
// session and SQL's current commissioning.read authorization; it never issues sessions.
app.MapGet("/api/commissioning/v2/machines/{machineId:guid}/evidence", async (
    Guid machineId, HttpContext http, IConfiguration config, CancellationToken ct) =>
{
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
