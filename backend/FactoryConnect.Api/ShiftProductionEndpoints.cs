using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

/// <summary>Human-declared production quantities, separate from machine-verified OEE evidence.</summary>
internal static class ShiftProductionEndpoints
{
    internal sealed record ProductionRequest(Guid ShiftId, DateOnly BusinessDate,
        decimal GoodQuantity, decimal RejectedQuantity, string? QuantityUnit,
        string? ProductionOrder, string? PartNumber, string? Operation, string? RevisionReason);

    private static async Task<byte[]?> SessionHash(HttpContext http)
    {
        var ticket = await http.AuthenticateAsync("operator-cookie");
        var encoded = ticket.Principal?.FindFirst("fc2:session")?.Value;
        if (!ticket.Succeeded || string.IsNullOrWhiteSpace(encoded)) return null;
        byte[] token;
        try { token = Convert.FromBase64String(encoded); }
        catch (FormatException) { return null; }
        if (token.Length != 32) { CryptographicOperations.ZeroMemory(token); return null; }
        try { return SHA256.HashData(token); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    internal static void MapShiftProduction(this WebApplication app)
    {
        app.MapGet("/api/planning/v2/machines/{machineId:guid}/production", async (
            Guid machineId, DateOnly businessDate, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.StatusCode(403);
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
                || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
                || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.StatusCode(503);
            if (machineId == Guid.Empty || businessDate.Year is < 2000 or > 2100)
                return Results.BadRequest();
            var hash = await SessionHash(http);
            if (hash is null) return Results.Unauthorized();
            try
            {
                await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .ValidateHumanSessionAsync(hash, ct);
                var records = new List<object>();
                await using var connection = new SqlConnection(config.GetConnectionString("Evidence"));
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand("""
                    SELECT ShiftId, ProductionRevisionId, Revision, GoodQuantity, RejectedQuantity,
                           QuantityUnit, ProductionOrder, PartNumber, Operation, RevisionReason,
                           RecordedAtUtc, RecordedByPrincipalId
                    FROM planning.vLatestShiftProduction
                    WHERE SiteId=@site AND MachineId=@machine AND BusinessDate=@date
                    ORDER BY ShiftId
                    """, connection);
                command.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machineId;
                command.Parameters.Add("@date", System.Data.SqlDbType.Date).Value = businessDate.ToDateTime(TimeOnly.MinValue);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    records.Add(new {
                        shiftId = reader.GetGuid(0), productionRevisionId = reader.GetGuid(1),
                        revision = reader.GetInt32(2), goodQuantity = reader.GetDecimal(3),
                        rejectedQuantity = reader.GetDecimal(4), quantityUnit = reader.GetString(5),
                        productionOrder = reader.IsDBNull(6) ? null : reader.GetString(6),
                        partNumber = reader.IsDBNull(7) ? null : reader.GetString(7),
                        operation = reader.IsDBNull(8) ? null : reader.GetString(8),
                        revisionReason = reader.GetString(9), recordedAtUtc = reader.GetDateTime(10),
                        recordedByPrincipalId = reader.GetGuid(11)
                    });
                return Results.Ok(new { siteId, machineId, businessDate, records,
                    source = "supervisor-declared-sql", evidenceKind = "human-declared-production",
                    isSample = false, oeeQualified = false });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { return AuthorityDiagnostics.Unavailable(http, ex, "shift-production-read", "Shift production unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapPost("/api/planning/v2/machines/{machineId:guid}/production", async (
            Guid machineId, ProductionRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.StatusCode(403);
            var origin = http.Request.Headers.Origin.ToString();
            var sameOrigin = origin == $"https://{http.Request.Host}";
            var devOrigin = http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()
                && http.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && http.Request.Host.Port == 5081 && origin == "http://localhost:3000"
                && config["Identity:DevelopmentDashboardReturnUri"] == "http://localhost:3000/operator-evidence";
            if (!sameOrigin && !devOrigin) return Results.StatusCode(403);
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
                || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
                || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.StatusCode(503);
            var unit = request.QuantityUnit?.Trim();
            var reason = request.RevisionReason?.Trim();
            var order = request.ProductionOrder?.Trim();
            var part = request.PartNumber?.Trim();
            var operation = request.Operation?.Trim();
            if (machineId == Guid.Empty || request.ShiftId == Guid.Empty
                || request.BusinessDate.Year is < 2000 or > 2100
                || request.GoodQuantity < 0 || request.RejectedQuantity < 0
                || request.GoodQuantity > 999999999999999m || request.RejectedQuantity > 999999999999999m
                || decimal.Round(request.GoodQuantity, 3) != request.GoodQuantity
                || decimal.Round(request.RejectedQuantity, 3) != request.RejectedQuantity
                || string.IsNullOrWhiteSpace(unit) || unit.Length > 50
                || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000
                || order?.Length > 100 || part?.Length > 100 || operation?.Length > 100)
                return Results.BadRequest(new { error = "Valid machine, shift, date, nonnegative quantities, unit and revision reason required." });
            var hash = await SessionHash(http);
            if (hash is null) return Results.Unauthorized();
            try
            {
                return await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .WriteAsync(hash, "security.access.manage", AuthorizationScope.Machine(siteId, machineId),
                        async (connection, transaction, actor, cancellation) =>
                        {
                            await using (var lockCommand = new SqlCommand("""
                                DECLARE @result int;
                                EXEC @result = sys.sp_getapplock
                                    @Resource=@resource, @LockMode='Exclusive',
                                    @LockOwner='Transaction', @LockTimeout=10000;
                                SELECT @result;
                                """, connection, transaction))
                            {
                                lockCommand.Parameters.Add("@resource", System.Data.SqlDbType.NVarChar, 255).Value =
                                    $"shift-production:{siteId:N}:{machineId:N}:{request.ShiftId:N}:{request.BusinessDate:yyyyMMdd}";
                                var lockResult = (int)(await lockCommand.ExecuteScalarAsync(cancellation)
                                    ?? throw new InvalidOperationException("Production revision lock returned no status."));
                                if (lockResult < 0) return (IResult)Results.Conflict(new { error = "Concurrent production revision. Retry." });
                            }
                            var id = Guid.NewGuid();
                            await using var command = new SqlCommand("""
                                INSERT INTO planning.ShiftProductionRevision
                                    (ProductionRevisionId, SiteId, MachineId, ShiftId, BusinessDate,
                                     Revision, GoodQuantity, RejectedQuantity, QuantityUnit,
                                     ProductionOrder, PartNumber, Operation, RevisionReason, RecordedByPrincipalId)
                                SELECT @id, @site, m.MachineId, s.ShiftId, @date,
                                       COALESCE((SELECT MAX(p.Revision) FROM planning.ShiftProductionRevision p
                                           WHERE p.SiteId=@site AND p.MachineId=@machine
                                             AND p.ShiftId=@shift AND p.BusinessDate=@date),0)+1,
                                       @good, @rejected, @unit, @order, @part, @operation, @reason, @actor
                                FROM master.Machine m CROSS JOIN planning.ShiftDefinition s
                                WHERE m.MachineId=@machine AND m.SiteId=@site
                                  AND s.ShiftId=@shift AND s.SiteId=@site AND s.IsRetired=0;
                                SELECT Revision FROM planning.ShiftProductionRevision WHERE ProductionRevisionId=@id;
                                """, connection, transaction);
                            command.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = id;
                            command.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                            command.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machineId;
                            command.Parameters.Add("@shift", System.Data.SqlDbType.UniqueIdentifier).Value = request.ShiftId;
                            command.Parameters.Add("@date", System.Data.SqlDbType.Date).Value = request.BusinessDate.ToDateTime(TimeOnly.MinValue);
                            var good = command.Parameters.Add("@good", System.Data.SqlDbType.Decimal);
                            good.Precision = 18; good.Scale = 3; good.Value = request.GoodQuantity;
                            var rejected = command.Parameters.Add("@rejected", System.Data.SqlDbType.Decimal);
                            rejected.Precision = 18; rejected.Scale = 3; rejected.Value = request.RejectedQuantity;
                            command.Parameters.Add("@unit", System.Data.SqlDbType.NVarChar, 50).Value = unit;
                            command.Parameters.Add("@order", System.Data.SqlDbType.NVarChar, 100).Value = (object?)order ?? DBNull.Value;
                            command.Parameters.Add("@part", System.Data.SqlDbType.NVarChar, 100).Value = (object?)part ?? DBNull.Value;
                            command.Parameters.Add("@operation", System.Data.SqlDbType.NVarChar, 100).Value = (object?)operation ?? DBNull.Value;
                            command.Parameters.Add("@reason", System.Data.SqlDbType.NVarChar, 1000).Value = reason;
                            command.Parameters.Add("@actor", System.Data.SqlDbType.UniqueIdentifier).Value = actor;
                            var revision = await command.ExecuteScalarAsync(cancellation);
                            if (revision is null) return (IResult)Results.NotFound();
                            return (IResult)Results.Created($"/api/planning/v2/machines/{machineId}/production?businessDate={request.BusinessDate:yyyy-MM-dd}",
                                new { productionRevisionId = id, siteId, machineId, request.ShiftId,
                                    request.BusinessDate, revision = (int)revision, request.GoodQuantity,
                                    request.RejectedQuantity, quantityUnit = unit,
                                    evidenceKind = "human-declared-production", oeeQualified = false });
                        }, ct);
            }
            catch (SecurityDeniedException) { return Results.StatusCode(403); }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            { return Results.Conflict(new { error = "Concurrent production revision. Reload and retry." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { return AuthorityDiagnostics.Unavailable(http, ex, "shift-production-write", "Production revision unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");
    }
}
