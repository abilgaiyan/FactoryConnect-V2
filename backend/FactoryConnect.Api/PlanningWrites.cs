using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

internal static class PlanningWrites
{
    internal sealed record CreateShiftRequest(string? DisplayName, int StartMinute, int EndMinute);

    internal sealed record RecordShiftTargetRequest(Guid ShiftId, DateOnly BusinessDate, decimal TargetQuantity, string? QuantityUnit, string? ProductionOrder, string? PartNumber, string? Operation, string? RevisionReason);

    internal sealed record CreateStopReasonRequest(string? ReasonCode, string? DisplayName, string? Category, bool IsPlanned);

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
                // Authorization and mutation must be one transaction, not separate session validation and SQL writes.
                return await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .WriteAsync(hash, "security.access.manage", AuthorizationScope.Site(siteId),
                        async (connection, transaction, actor, token) =>
                        {
                var shiftId = Guid.NewGuid();
                await using var command = new SqlCommand("""
                    INSERT INTO planning.ShiftDefinition
                        (ShiftId, SiteId, DisplayName, StartMinute, EndMinute)
                    VALUES (@shiftId, @siteId, @name, @start, @end)
                    """, connection, transaction);
                command.Parameters.Add("@shiftId", System.Data.SqlDbType.UniqueIdentifier).Value = shiftId;
                command.Parameters.Add("@siteId", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 100).Value = name;
                command.Parameters.Add("@start", System.Data.SqlDbType.SmallInt).Value = request.StartMinute;
                command.Parameters.Add("@end", System.Data.SqlDbType.SmallInt).Value = request.EndMinute;
                await command.ExecuteNonQueryAsync(token);
                return (IResult)Results.Created($"/api/planning/v2/shifts", new
                {
                    shiftId, siteId, displayName = name, request.StartMinute, request.EndMinute
                });
                        }, ct);
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
                // Authorization and mutation must be one transaction, not separate session validation and SQL writes.
                return await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .WriteAsync(hash, "security.access.manage", AuthorizationScope.Site(siteId),
                        async (connection, transaction, actor, token) =>
                        {
                var breakId = Guid.NewGuid();
                await using var command = new SqlCommand("""
                    INSERT INTO planning.ShiftBreak
                        (BreakId, ShiftId, DisplayName, StartOffsetMinute, DurationMinute,
                         ExcludeFromPlannedProduction)
                    SELECT @breakId, ShiftId, @name, @start, @duration, @exclude
                    FROM planning.ShiftDefinition
                    WHERE ShiftId = @shiftId AND SiteId = @siteId AND IsRetired = 0
                    """, connection, transaction);
                command.Parameters.Add("@breakId", System.Data.SqlDbType.UniqueIdentifier).Value = breakId;
                command.Parameters.Add("@shiftId", System.Data.SqlDbType.UniqueIdentifier).Value = shiftId;
                command.Parameters.Add("@siteId", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 100).Value = name;
                command.Parameters.Add("@start", System.Data.SqlDbType.SmallInt).Value = request.StartOffsetMinute;
                command.Parameters.Add("@duration", System.Data.SqlDbType.SmallInt).Value = request.DurationMinute;
                command.Parameters.Add("@exclude", System.Data.SqlDbType.Bit).Value = request.ExcludeFromPlannedProduction;
                if (await command.ExecuteNonQueryAsync(token) == 0) return (IResult)Results.NotFound();
                return (IResult)Results.Created($"/api/planning/v2/shifts/{shiftId}/breaks", new
                {
                    breakId, shiftId, displayName = name, request.StartOffsetMinute,
                    request.DurationMinute, request.ExcludeFromPlannedProduction
                });
                        }, ct);
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

        app.MapPost("/api/planning/v2/stop-reasons", async (
            CreateStopReasonRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps || !AllowedOrigin(http, config)) return Results.StatusCode(403);
            if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
                || !Guid.TryParse(config["Evidence:SiteId"], out var siteId)
                || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
                return Results.StatusCode(503);
            var code = request.ReasonCode?.Trim().ToUpperInvariant();
            var name = request.DisplayName?.Trim();
            var category = request.Category?.Trim();
            if (string.IsNullOrWhiteSpace(code) || code.Length > 40
                || string.IsNullOrWhiteSpace(name) || name.Length > 150
                || category is not ("Breakdown" or "Setup" or "Tooling" or "Material"
                    or "Quality" or "Operator" or "Planned" or "Other"))
                return Results.BadRequest(new { error = "Valid reason code, name and category required." });
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
                // Authorization and mutation must be one transaction, not separate session validation and SQL writes.
                return await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .WriteAsync(hash, "security.access.manage", AuthorizationScope.Site(siteId),
                        async (connection, transaction, actor, token) =>
                        {
                var reasonId = Guid.NewGuid();
                await using var command = new SqlCommand("""
                    INSERT INTO planning.StopReason
                        (StopReasonId, SiteId, ReasonCode, DisplayName, Category, IsPlanned)
                    VALUES (@id, @site, @code, @name, @category, @planned)
                    """, connection, transaction);
                command.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = reasonId;
                command.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                command.Parameters.Add("@code", System.Data.SqlDbType.NVarChar, 40).Value = code;
                command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 150).Value = name;
                command.Parameters.Add("@category", System.Data.SqlDbType.NVarChar, 40).Value = category;
                command.Parameters.Add("@planned", System.Data.SqlDbType.Bit).Value = request.IsPlanned;
                await command.ExecuteNonQueryAsync(token);
                return (IResult)Results.Created("/api/planning/v2/stop-reasons", new {
                    stopReasonId = reasonId, siteId, reasonCode = code, displayName = name,
                    category, request.IsPlanned
                });
                        }, ct);
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                return Results.Conflict(new { error = "This site already has a reason with that code." });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return AuthorityDiagnostics.Unavailable(http, ex, "planning-reason-create", "Reason creation unavailable");
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");

        app.MapPost("/api/planning/v2/machines/{machineId:guid}/targets", async (
            Guid machineId, RecordShiftTargetRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps || !AllowedOrigin(http, config)) return Results.StatusCode(403);
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
                || request.TargetQuantity < 0 || request.TargetQuantity > 999999999999999m
                || decimal.Round(request.TargetQuantity, 3) != request.TargetQuantity
                || string.IsNullOrWhiteSpace(unit) || unit.Length > 50
                || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000
                || order?.Length > 100 || part?.Length > 100 || operation?.Length > 100)
                return Results.BadRequest(new { error = "Valid machine, shift, business date, nonnegative target, unit and revision reason required." });
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
                return await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                    .WriteAsync(hash, "security.access.manage", AuthorizationScope.Machine(siteId, machineId),
                        async (connection, transaction, actor, cancellation) =>
                        {
                            // Serializes revisions per machine/shift/date under the installation write gate.
                            // The FK constraints reject a shift or machine outside the configured site.
                            var revisionId = Guid.NewGuid();
                            await using var command = new SqlCommand("""
                                INSERT INTO planning.ShiftTargetRevision
                                    (TargetRevisionId, SiteId, MachineId, ShiftId, BusinessDate, Revision,
                                     TargetQuantity, QuantityUnit, ProductionOrder, PartNumber, Operation,
                                     RevisionReason, RecordedByPrincipalId)
                                SELECT @id, @site, m.MachineId, s.ShiftId, @date,
                                       COALESCE((SELECT MAX(t.Revision) FROM planning.ShiftTargetRevision t
                                           WHERE t.MachineId=@machine AND t.ShiftId=@shift AND t.BusinessDate=@date),0)+1,
                                       @quantity, @unit, @order, @part, @operation, @reason, @actor
                                FROM master.Machine m CROSS JOIN planning.ShiftDefinition s
                                WHERE m.MachineId=@machine AND m.SiteId=@site
                                  AND s.ShiftId=@shift AND s.SiteId=@site AND s.IsRetired=0;
                                SELECT Revision FROM planning.ShiftTargetRevision WHERE TargetRevisionId=@id;
                                """, connection, transaction);
                            command.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = revisionId;
                            command.Parameters.Add("@site", System.Data.SqlDbType.UniqueIdentifier).Value = siteId;
                            command.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machineId;
                            command.Parameters.Add("@shift", System.Data.SqlDbType.UniqueIdentifier).Value = request.ShiftId;
                            command.Parameters.Add("@date", System.Data.SqlDbType.Date).Value = request.BusinessDate.ToDateTime(TimeOnly.MinValue);
                            var quantity = command.Parameters.Add("@quantity", System.Data.SqlDbType.Decimal);
                            quantity.Precision = 18; quantity.Scale = 3; quantity.Value = request.TargetQuantity;
                            command.Parameters.Add("@unit", System.Data.SqlDbType.NVarChar, 50).Value = unit;
                            command.Parameters.Add("@order", System.Data.SqlDbType.NVarChar, 100).Value = (object?)order ?? DBNull.Value;
                            command.Parameters.Add("@part", System.Data.SqlDbType.NVarChar, 100).Value = (object?)part ?? DBNull.Value;
                            command.Parameters.Add("@operation", System.Data.SqlDbType.NVarChar, 100).Value = (object?)operation ?? DBNull.Value;
                            command.Parameters.Add("@reason", System.Data.SqlDbType.NVarChar, 1000).Value = reason;
                            command.Parameters.Add("@actor", System.Data.SqlDbType.UniqueIdentifier).Value = actor;
                            var revision = await command.ExecuteScalarAsync(cancellation);
                            if (revision is null) return (IResult)Results.NotFound();
                            return (IResult)Results.Created($"/api/planning/v2/machines/{machineId}/targets?businessDate={request.BusinessDate:yyyy-MM-dd}",
                                new { targetRevisionId = revisionId, siteId, machineId, request.ShiftId,
                                      request.BusinessDate, revision = (int)revision, request.TargetQuantity,
                                      quantityUnit = unit, revisionReason = reason, recordedByPrincipalId = actor });
                        }, ct);
            }
            catch (SecurityDeniedException) { return Results.StatusCode(403); }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            { return Results.Conflict(new { error = "Concurrent target revision conflict. Reload and retry." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { return AuthorityDiagnostics.Unavailable(http, ex, "planning-target-write", "Target revision unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }).RequireRateLimiting("security-boundary");
    }
}
