using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

/// <summary>
/// Historical V1 reporting is explicitly separate from verified live V2 evidence.
/// Reads only imported legacy_reporting tables and never writes or authenticates machine state.
/// </summary>
internal static class LegacyHistoryReportingApi
{
    private const int MaximumResults = 2000;
    private const int MaximumWindowDays = 31;
    private static readonly TimeSpan LegacyLocalOffset = TimeSpan.FromMinutes(330);

    internal static void MapLegacyHistoryReporting(this WebApplication app)
    {
        app.MapGet("/api/reporting/v2/legacy/machines", async (
            HttpContext http, IConfiguration config, CancellationToken ct) =>
            await AuthorizedRead(http, config, async (connection, ct) =>
            {
                var machines = new List<object>();
                await using var command = new SqlCommand("""
                    SELECT SourceDatabase, SourceMachineId, DeviceKey, V2MachineId
                    FROM legacy_reporting.Machine
                    ORDER BY DeviceKey, SourceDatabase, SourceMachineId
                    """, connection) { CommandTimeout = 20 };
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    machines.Add(new {
                        sourceDatabase = reader.GetString(0), sourceMachineId = reader.GetGuid(1),
                        deviceKey = reader.GetString(2),
                        v2MachineId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3)
                    });
                return Results.Ok(new { source = "imported-v1-legacy", evidenceKind = "historical-reporting-only",
                    liveStatus = "not-established", isSample = false, machines });
            }, ct)).RequireRateLimiting("security-boundary");

        app.MapGet("/api/reporting/v2/legacy/machines/{machineId:guid}/metrics", async (
            Guid machineId, DateOnly from, DateOnly through, HttpContext http,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!ValidRange(from, through)) return Results.BadRequest(new { error = "Select an inclusive date range of at most 31 days." });
            return await AuthorizedRead(http, config, async (connection, ct) =>
            {
                var metrics = new List<object>();
                await using var command = new SqlCommand("""
                    SELECT TOP (@take)
                        p.SourceDatabase, p.SourceRowId, p.PeriodKind, p.SourceSiteId,
                        p.ShiftId, p.ShiftStartsAtUtc, p.ShiftEndsAtUtc,
                        p.ProductionBusinessDate, p.MetricKey, p.DefinitionVersion,
                        p.Status, p.MetricValue, p.Unit, p.ReasonCode
                    FROM legacy_reporting.MetricProjection p
                    INNER JOIN legacy_reporting.Machine m ON m.SourceDatabase=p.SourceDatabase AND m.SourceMachineId=p.SourceMachineId
                    WHERE p.SourceMachineId = @machine
                      AND (
                        (p.ProductionBusinessDate >= @fromDate AND p.ProductionBusinessDate < @throughExclusive)
                        OR (p.ProductionBusinessDate IS NULL AND p.ShiftStartsAtUtc < @throughUtc
                          AND p.ShiftEndsAtUtc > @fromUtc)
                      )
                    ORDER BY p.ShiftStartsAtUtc, p.SourceRowId
                    """, connection) { CommandTimeout = 20 };
                command.Parameters.Add("@take", SqlDbType.Int).Value = MaximumResults + 1;
                command.Parameters.Add("@machine", SqlDbType.UniqueIdentifier).Value = machineId;
                command.Parameters.Add("@fromDate", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                command.Parameters.Add("@throughExclusive", SqlDbType.Date).Value = through.AddDays(1).ToDateTime(TimeOnly.MinValue);
                command.Parameters.Add("@fromUtc", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), LegacyLocalOffset);
                command.Parameters.Add("@throughUtc", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(through.AddDays(1).ToDateTime(TimeOnly.MinValue), LegacyLocalOffset);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (metrics.Count == MaximumResults) return Results.StatusCode(413);
                    metrics.Add(new {
                        sourceDatabase = reader.GetString(0), sourceRowId = reader.GetInt64(1),
                        periodKind = reader.GetByte(2),
                        sourceSiteId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        shiftId = reader.IsDBNull(4) ? null : reader.GetString(4),
                        shiftStartsAtUtc = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5),
                        shiftEndsAtUtc = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6),
                        productionBusinessDate = reader.IsDBNull(7) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(7)),
                        metricKey = reader.GetString(8),
                        definitionVersion = reader.IsDBNull(9) ? null : reader.GetString(9),
                        status = reader.GetByte(10),
                        metricValue = reader.IsDBNull(11) ? null : reader.GetString(11),
                        unit = reader.IsDBNull(12) ? null : reader.GetString(12),
                        reasonCode = reader.IsDBNull(13) ? (byte?)null : reader.GetByte(13)
                    });
                }
                return Results.Ok(new {
                    machineId, from, through, source = "imported-v1-legacy",
                    evidenceKind = "historical-reporting-only", liveStatus = "not-established",
                    isSample = false, metrics
                });
            }, ct);
        }).RequireRateLimiting("security-boundary");

        // Imported V1 shift input aggregates are source facts, not recomputed OEE.
        app.MapGet("/api/reporting/v2/legacy/machines/{machineId:guid}/shift-aggregates", async (
            Guid machineId, DateOnly from, DateOnly through, HttpContext http,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!ValidRange(from, through)) return Results.BadRequest(new { error = "Select an inclusive date range of at most 31 days." });
            return await AuthorizedRead(http, config, async (connection, ct) =>
            {
                var aggregates = new List<object>();
                await using var command = new SqlCommand("""
                    SELECT TOP (@take)
                        s.SourceDatabase, s.SourceRowId, s.SourceSiteId,
                        s.ShiftScheduleAssignmentId, s.ShiftId,
                        s.ShiftStartsAtUtc, s.ShiftEndsAtUtc,
                        s.MetricInputKey, s.AggregateValue, s.Unit, s.InputCount
                    FROM legacy_reporting.ShiftAggregate s
                    INNER JOIN legacy_reporting.Machine m
                        ON m.SourceDatabase=s.SourceDatabase AND m.SourceMachineId=s.SourceMachineId
                    WHERE s.SourceMachineId=@machine
                      AND s.ShiftStartsAtUtc < @throughUtc
                      AND s.ShiftEndsAtUtc > @fromUtc
                    ORDER BY s.ShiftStartsAtUtc, s.SourceRowId
                    """, connection) { CommandTimeout = 20 };
                command.Parameters.Add("@take", SqlDbType.Int).Value = MaximumResults + 1;
                command.Parameters.Add("@machine", SqlDbType.UniqueIdentifier).Value = machineId;
                command.Parameters.Add("@fromUtc", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), LegacyLocalOffset);
                command.Parameters.Add("@throughUtc", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(through.AddDays(1).ToDateTime(TimeOnly.MinValue), LegacyLocalOffset);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (aggregates.Count == MaximumResults) return Results.StatusCode(413);
                    aggregates.Add(new {
                        sourceDatabase = reader.GetString(0), sourceRowId = reader.GetInt64(1),
                        sourceSiteId = reader.IsDBNull(2) ? null : reader.GetString(2),
                        shiftScheduleAssignmentId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        shiftId = reader.IsDBNull(4) ? null : reader.GetString(4),
                        shiftStartsAtUtc = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5),
                        shiftEndsAtUtc = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6),
                        metricInputKey = reader.GetString(7),
                        aggregateValue = reader.IsDBNull(8) ? null : reader.GetString(8),
                        unit = reader.IsDBNull(9) ? null : reader.GetString(9),
                        inputCount = reader.IsDBNull(10) ? (long?)null : Convert.ToInt64(reader.GetValue(10), System.Globalization.CultureInfo.InvariantCulture)
                    });
                }
                return Results.Ok(new {
                    machineId, from, through, source = "imported-v1-legacy",
                    evidenceKind = "historical-reporting-only", liveStatus = "not-established",
                    isSample = false, aggregates
                });
            }, ct);
        }).RequireRateLimiting("security-boundary");

        app.MapGet("/api/reporting/v2/legacy/machines/{machineId:guid}/activity", async (
            Guid machineId, DateOnly from, DateOnly through, HttpContext http,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!ValidRange(from, through)) return Results.BadRequest(new { error = "Select an inclusive date range of at most 31 days." });
            return await AuthorizedRead(http, config, async (connection, ct) =>
            {
                // Intervals are selected by overlap, NOT by the start date, so midnight-
                // crossing periods are not lost. EndedAt=NULL is not an ended period.
                var rows = new List<object>();
                await using var command = new SqlCommand("""
                    SELECT TOP (@take) a.SourceDatabase, a.SourceMachineId, a.MachineState,
                        a.StartedAt, a.EndedAt, a.ProjectionRevision, a.OutputOrdinal
                    FROM legacy_reporting.ActivityPeriod a
                    INNER JOIN legacy_reporting.Machine m ON m.SourceDatabase=a.SourceDatabase AND m.SourceMachineId=a.SourceMachineId
                    WHERE a.SourceMachineId = @machine
                      AND a.EndedAt IS NOT NULL AND a.EndedAt > a.StartedAt
                      AND a.StartedAt < @endUtc AND a.EndedAt > @startUtc
                    ORDER BY a.StartedAt, a.ProjectionRevision, a.OutputOrdinal
                    """, connection) { CommandTimeout = 20 };
                command.Parameters.Add("@take", SqlDbType.Int).Value = MaximumResults + 1;
                command.Parameters.Add("@machine", SqlDbType.UniqueIdentifier).Value = machineId;
                command.Parameters.Add("@startUtc", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), LegacyLocalOffset);
                command.Parameters.Add("@endUtc", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(through.AddDays(1).ToDateTime(TimeOnly.MinValue), LegacyLocalOffset);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count == MaximumResults) return Results.StatusCode(413);
                    rows.Add(new {
                        sourceDatabase = reader.GetString(0), sourceMachineId = reader.GetGuid(1),
                        machineState = reader.GetByte(2),
                        startedAt = reader.GetFieldValue<DateTimeOffset>(3),
                        endedAt = reader.GetFieldValue<DateTimeOffset>(4),
                        projectionRevision = reader.GetDecimal(5),
                        outputOrdinal = reader.GetInt32(6)
                    });
                }
                return Results.Ok(new {
                    machineId, from, through, source = "imported-v1-legacy",
                    evidenceKind = "historical-reporting-only", liveStatus = "not-established",
                    timeBasis = "Asia/Kolkata historical factory calendar; activity periods are raw overlapping intervals, not daily or shift aggregates",
                    isSample = false, periods = rows
                });
            }, ct);
        }).RequireRateLimiting("security-boundary");
    }

    private static bool ValidRange(DateOnly from, DateOnly through)
        => from <= through && from >= new DateOnly(2000, 1, 1)
           && through <= new DateOnly(9999, 12, 30)
           && through.DayNumber - from.DayNumber < MaximumWindowDays;

    private static async Task<IResult> AuthorizedRead(HttpContext http, IConfiguration config,
        Func<SqlConnection, CancellationToken, Task<IResult>> read, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.StatusCode(403);
        if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId)
            || string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
            return Results.StatusCode(503);
        var ticket = await http.AuthenticateAsync("operator-cookie");
        var session = ticket.Principal?.FindFirst("fc2:session")?.Value;
        if (!ticket.Succeeded || string.IsNullOrWhiteSpace(session)) return Results.Unauthorized();
        byte[] token;
        try { token = Convert.FromBase64String(session); }
        catch (FormatException) { return Results.Unauthorized(); }
        if (token.Length != 32) { CryptographicOperations.ZeroMemory(token); return Results.Unauthorized(); }
        var hash = SHA256.HashData(token);
        CryptographicOperations.ZeroMemory(token);
        try
        {
            await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId)
                .ValidateHumanSessionAsync(hash, ct);
            await using var connection = new SqlConnection(config.GetConnectionString("Evidence"));
            await connection.OpenAsync(ct);
            return await read(connection, ct);
        }
        catch (SecurityDeniedException) { return Results.Unauthorized(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return AuthorityDiagnostics.Unavailable(http, ex, "legacy-reporting-read", "Historical V1 reporting unavailable"); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
