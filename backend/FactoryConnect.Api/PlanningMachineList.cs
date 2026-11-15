using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;
namespace FactoryConnect.Api;
internal static class PlanningMachineList {
 internal static void MapPlanningMachines(this WebApplication app) {
 app.MapGet("/api/planning/v2/machines", async (HttpContext http, IConfiguration config, CancellationToken ct) => {
 http.Response.Headers.CacheControl = "no-store";
 if (!http.Request.IsHttps) return Results.StatusCode(403);
 if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) || !Guid.TryParse(config["Evidence:SiteId"], out var siteId) || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence"))) return Results.StatusCode(503);
 var ticket = await http.AuthenticateAsync("operator-cookie");
 var bearer = ticket.Principal?.FindFirst("fc2:session")?.Value;
 if (!ticket.Succeeded || string.IsNullOrWhiteSpace(bearer)) return Results.Unauthorized();
 byte[] token;
 try { token = Convert.FromBase64String(bearer); } catch (FormatException) { return Results.Unauthorized(); }
 if (token.Length != 32) { CryptographicOperations.ZeroMemory(token); return Results.Unauthorized(); }
 byte[] hash;
 try { hash = SHA256.HashData(token); } finally { CryptographicOperations.ZeroMemory(token); }
 try {
 await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId).ValidateHumanSessionAsync(hash, ct);
 var machines = new List<object>();
 await using var connection = new SqlConnection(config.GetConnectionString("Evidence"));
 await connection.OpenAsync(ct);
 await using var command = new SqlCommand("SELECT MachineId, DisplayName FROM master.Machine WHERE SiteId = @site AND IsRetired = 0 ORDER BY DisplayName, MachineId", connection);
 command.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
 await using var reader = await command.ExecuteReaderAsync(ct);
 while (await reader.ReadAsync(ct)) machines.Add(new { machineId = reader.GetGuid(0), displayName = reader.GetString(1) });
 return Results.Ok(new { siteId, machines, source = "sql-master", isSample = false });
 } catch (SecurityDeniedException) { return Results.Unauthorized(); }
 catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
 catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "planning-machine-list", "Planning machines unavailable"); }
 finally { CryptographicOperations.ZeroMemory(hash); }
 }).RequireRateLimiting("security-boundary");
 }
}