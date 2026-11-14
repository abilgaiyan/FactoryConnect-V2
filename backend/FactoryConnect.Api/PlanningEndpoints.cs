using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

/// <summary>Live, site-scoped planning reads. Never substitutes sample values for missing SQL data.</summary>
internal static class PlanningEndpoints
{
    internal static void MapPlanning(this WebApplication app)
    {
        app.MapGet("/api/planning/v2/shifts", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.StatusCode(403);
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
                || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
                || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.StatusCode(503);

            // Only the server-issued, encrypted OIDC cookie can authenticate this read.
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
                var shifts = new List<object>();
                await using var connection = new SqlConnection(config.GetConnectionString("Evidence"));
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand("""
                    SELECT ShiftId, DisplayName, StartMinute, EndMinute, ShiftDurationMinutes,
                           ExcludedBreakMinutes, PlannedProductionMinutes
                    FROM planning.vShiftCalendar
                    WHERE SiteId = @siteId AND IsRetired = 0
                    ORDER BY StartMinute, ShiftId
                    """, connection);
                command.Parameters.Add("@siteId", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    shifts.Add(new
                    {
                        shiftId = reader.GetGuid(0),
                        displayName = reader.GetString(1),
                        startMinute = reader.GetInt16(2),
                        endMinute = reader.GetInt16(3),
                        durationMinutes = reader.GetInt32(4),
                        excludedBreakMinutes = reader.GetInt32(5),
                        plannedProductionMinutes = reader.GetInt32(6)
                    });
                return Results.Ok(new { siteId, shifts, source = "sql-planning", isSample = false });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return AuthorityDiagnostics.Unavailable(http, ex, "planning-shifts-read", "Shift planning unavailable");
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");
    }
}
