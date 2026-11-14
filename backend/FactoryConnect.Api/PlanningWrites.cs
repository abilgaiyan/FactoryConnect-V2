using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

internal static class PlanningWrites
{
    internal sealed record CreateShiftRequest(string? DisplayName, int StartMinute, int EndMinute);

    internal sealed record CreateBreakRequest(string? DisplayName, int StartOffsetMinute, int DurationMinute, bool ExcludeFromPlannedProduction);

    // The local Vite dashboard is an explicitly configured development origin; production remains same-origin HTTPS.
    private static bool AllowedOrigin(HttpContext http, IConfiguration config)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin == $"https://{http.Request.Host}") return true;
        return http.Request.IsHttps
            && http.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && http.Request.Host.Port == 5081
            && http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()
            && string.Equals(config["Identity:DevelopmentDashboardReturnUri"],
                "http://localhost:3000/operator-evidence", StringComparison.Ordinal)
            && origin == "http://localhost:3000";
    }

    internal static void MapPlanningWrites(this WebApplication app)
    {
        app.MapPost("/api/planning/v2/shifts", async (
            CreateShiftRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.StatusCode(403);
            // Same-origin browser writes only; the OIDC cookie must never authorize cross-site POSTs.
            if (!AllowedOrigin(http, config))
                return Results.StatusCode(403);
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
                || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
                || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.StatusCode(503);
            var name = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100
                || request.StartMinute is < 0 or > 1439
                || request.EndMinute is < 0 or > 1439
                || request.StartMinute == request.EndMinute)
                return Results.BadRequest(new { error = "Valid name and distinct start/end minutes (0–1439) required." });

            var ticket = await http.AuthenticateAsync("operator-cookie");
            var bearer = ticket.Principal?.FindFirst("fc2:session")?.Value;
            if (!ticket.Succeeded || string.IsNullOrWhiteSpace(bearer)) return Results.Unauthorized();
            byte[] token;
            try { token = Convert.FromBase64String(bearer); }
            catch (FormatException) { return Results.Unauthorized(); }
            if (token.Length != 32) { CryptographicOperations.ZeroMemory(token); return Results.Unauthorized(); }
            byte[] hash;
            try { hash = SHA256.HashData(token); }
            finally { CryptographicOperations.ZeroMemory(token); }
            try
            {
                await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ValidateHumanSessionAsync(hash, ct);
                var shiftId = Guid.NewGuid();
                await using var connection = new SqlConnection(config.GetConnectionString("Evidence"));
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand("""
                    INSERT INTO planning.ShiftDefinition
                        (ShiftId, SiteId, DisplayName, StartMinute, EndMinute)
                    VALUES (@shiftId, @siteId, @name, @start, @end)
                    """, connection);
                command.Parameters.Add("@shiftId", System.Data.SqlDbType.UniqueIdentifier).Value = shiftId;
                command.Parameters.Add("@siteId", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 100).Value = name;
                command.Parameters.Add("@start", System.Data.SqlDbType.SmallInt).Value = request.StartMinute;
                command.Parameters.Add("@end", System.Data.SqlDbType.SmallInt).Value = request.EndMinute;
                await command.ExecuteNonQueryAsync(ct);
                return Results.Created($"/api/planning/v2/shifts", new
                {
                    shiftId, siteId, displayName = name, request.StartMinute, request.EndMinute
                });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (SqlException ex) when (ex.Number == 51041)
            {
                return Results.Conflict(new { error = "Active shifts at this site cannot overlap." });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return AuthorityDiagnostics.Unavailable(http, ex, "planning-shift-create", "Shift creation unavailable");
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapPost("/api/planning/v2/shifts/{shiftId:guid}/breaks", async (
            Guid shiftId, CreateBreakRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps || !AllowedOrigin(http, config))
                return Results.StatusCode(403);
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
                || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
                || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.StatusCode(503);
            var name = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100
                || request.StartOffsetMinute is < 0 or > 1439
                || request.DurationMinute is < 1 or > 480)
                return Results.BadRequest(new { error = "Valid break name, offset (0–1439), and duration (1–480) required." });

            var ticket = await http.AuthenticateAsync("operator-cookie");
            var bearer = ticket.Principal?.FindFirst("fc2:session")?.Value;
            if (!ticket.Succeeded || string.IsNullOrWhiteSpace(bearer)) return Results.Unauthorized();
            byte[] token;
            try { token = Convert.FromBase64String(bearer); }
            catch (FormatException) { return Results.Unauthorized(); }
            if (token.Length != 32) { CryptographicOperations.ZeroMemory(token); return Results.Unauthorized(); }
            byte[] hash;
            try { hash = SHA256.HashData(token); }
            finally { CryptographicOperations.ZeroMemory(token); }
            try
            {
                await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ValidateHumanSessionAsync(hash, ct);
                var breakId = Guid.NewGuid();
                await using var connection = new SqlConnection(config.GetConnectionString("Evidence"));
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand("""
                    INSERT INTO planning.ShiftBreak
                        (BreakId, ShiftId, DisplayName, StartOffsetMinute, DurationMinute,
                         ExcludeFromPlannedProduction)
                    SELECT @breakId, ShiftId, @name, @start, @duration, @exclude
                    FROM planning.ShiftDefinition
                    WHERE ShiftId = @shiftId AND SiteId = @siteId AND IsRetired = 0
                    """, connection);
                command.Parameters.Add("@breakId", System.Data.SqlDbType.UniqueIdentifier).Value = breakId;
                command.Parameters.Add("@shiftId", System.Data.SqlDbType.UniqueIdentifier).Value = shiftId;
                command.Parameters.Add("@siteId", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 100).Value = name;
                command.Parameters.Add("@start", System.Data.SqlDbType.SmallInt).Value = request.StartOffsetMinute;
                command.Parameters.Add("@duration", System.Data.SqlDbType.SmallInt).Value = request.DurationMinute;
                command.Parameters.Add("@exclude", System.Data.SqlDbType.Bit).Value = request.ExcludeFromPlannedProduction;
                if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
                return Results.Created($"/api/planning/v2/shifts/{shiftId}/breaks", new
                {
                    breakId, shiftId, displayName = name, request.StartOffsetMinute,
                    request.DurationMinute, request.ExcludeFromPlannedProduction
                });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (SqlException ex) when (ex.Number is 51040 or 51043)
            {
                return Results.Conflict(new { error = "Break is outside the shift or overlaps an existing break." });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return AuthorityDiagnostics.Unavailable(http, ex, "planning-break-create", "Break creation unavailable");
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");
    }
}
