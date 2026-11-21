using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Configuration;

/// <summary>Reads and validates a site's planning source before creating immutable policy snapshots.</summary>
public static class PlanningPolicySnapshot
{
    public sealed record Snapshot(IReadOnlyList<PolicyCanonicalizer.Shift> Shifts,
        IReadOnlyList<PolicyCanonicalizer.Break> Breaks);

    public static async Task<Snapshot> ReadAsync(SqlConnection connection, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (siteId == Guid.Empty) throw new ArgumentException("Site required.", nameof(siteId));
        var shifts = new List<PolicyCanonicalizer.Shift>();
        var breaks = new List<PolicyCanonicalizer.Break>();
        await using (var command = new SqlCommand("""
            SELECT ShiftId,StartMinute,EndMinute
            FROM planning.ShiftDefinition
            WHERE SiteId=@site AND IsRetired=0
            ORDER BY ShiftId
            """, connection))
        {
            command.Parameters.AddWithValue("@site", siteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                shifts.Add(new(reader.GetGuid(0),reader.GetInt16(1),reader.GetInt16(2)));
        }
        if (shifts.Count != 3 ||
            !shifts.Any(s => s.StartMinute==450 && s.EndMinute==960) ||
            !shifts.Any(s => s.StartMinute==960 && s.EndMinute==0) ||
            !shifts.Any(s => s.StartMinute==0 && s.EndMinute==450))
            throw new InvalidOperationException("Unexpected active shift calendar; refusing policy snapshot.");

        await using (var command = new SqlCommand("""
            SELECT b.BreakId,b.ShiftId,b.StartOffsetMinute,b.DurationMinute,b.ExcludeFromPlannedProduction
            FROM planning.ShiftBreak b
            JOIN planning.ShiftDefinition s ON s.ShiftId=b.ShiftId
            WHERE s.SiteId=@site AND s.IsRetired=0
            ORDER BY b.BreakId
            """, connection))
        {
            command.Parameters.AddWithValue("@site", siteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                breaks.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetInt16(2),
                    reader.GetInt16(3),reader.GetBoolean(4)));
        }
        if (breaks.Count != 3 || breaks.Select(b => b.ShiftId).Distinct().Count()!=3 ||
            breaks.Any(b => b.DurationMinute!=30 || !b.ExcludeFromPlannedProduction ||
                b.StartOffsetMinute!=(shifts.Single(s=>s.ShiftId==b.ShiftId).StartMinute==0 ? 210 : 240)))
            throw new InvalidOperationException("Unexpected break calendar; refusing policy snapshot.");
        // Validate the exact serializable snapshots before any caller attempts persistence.
        _ = PolicyCanonicalizer.Schedule(shifts);
        _ = PolicyCanonicalizer.BreakPolicy(breaks);
        return new Snapshot(shifts,breaks);
    }
}
