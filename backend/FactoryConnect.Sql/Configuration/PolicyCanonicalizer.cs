using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FactoryConnect.Sql.Configuration;

/// <summary>
/// Versioned UTF-8 policy snapshot format. This serializer does not infer timezone
/// rules: callers must supply the exact immutable rules payload and its provenance.
/// </summary>
public static class PolicyCanonicalizer
{
    public const int Version = 1;

    public sealed record Shift(Guid ShiftId, int StartMinute, int EndMinute);
    public sealed record Break(Guid BreakId, Guid ShiftId, int StartOffsetMinute, int DurationMinute, bool ExcludeFromPlannedProduction);
    public sealed record Timezone(string IanaZoneId, string TzdbRelease, string ResolverVersion,
        string GapPolicy, string FoldPolicy, byte[] RulesBytes);

    public static byte[] Schedule(IReadOnlyList<Shift> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        if (shifts.Count == 0 || shifts.Any(s => s.ShiftId == Guid.Empty ||
            s.StartMinute is < 0 or >= 1440 || s.EndMinute is < 0 or >= 1440 ||
            s.StartMinute == s.EndMinute) || shifts.Select(s => s.ShiftId).Distinct().Count() != shifts.Count)
            throw new ArgumentException("Invalid shift snapshot.", nameof(shifts));
        return Write("schedule", w =>
        {
            w.WriteStartArray("shifts");
            foreach (var s in shifts.OrderBy(s => s.ShiftId))
            {
                w.WriteStartObject();
                w.WriteString("shiftId", s.ShiftId.ToString("D"));
                w.WriteNumber("startMinute", s.StartMinute);
                w.WriteNumber("endMinute", s.EndMinute);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    public static byte[] BreakPolicy(IReadOnlyList<Break> breaks)
    {
        ArgumentNullException.ThrowIfNull(breaks);
        if (breaks.Count == 0 || breaks.Any(b => b.BreakId == Guid.Empty || b.ShiftId == Guid.Empty ||
            b.StartOffsetMinute is < 0 or >= 1440 || b.DurationMinute is <= 0 or > 1440 ||
            b.StartOffsetMinute + b.DurationMinute > 1440) ||
            breaks.Select(b => b.BreakId).Distinct().Count() != breaks.Count)
            throw new ArgumentException("Invalid break snapshot.", nameof(breaks));
        return Write("break", w =>
        {
            w.WriteStartArray("breaks");
            foreach (var b in breaks.OrderBy(b => b.BreakId))
            {
                w.WriteStartObject();
                w.WriteString("breakId", b.BreakId.ToString("D"));
                w.WriteString("shiftId", b.ShiftId.ToString("D"));
                w.WriteNumber("startOffsetMinute", b.StartOffsetMinute);
                w.WriteNumber("durationMinute", b.DurationMinute);
                w.WriteBoolean("excludeFromPlannedProduction", b.ExcludeFromPlannedProduction);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    public static byte[] TimezonePolicy(Timezone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (string.IsNullOrWhiteSpace(zone.IanaZoneId) || zone.IanaZoneId.Length > 200 ||
            string.IsNullOrWhiteSpace(zone.TzdbRelease) || zone.TzdbRelease.Length > 32 ||
            string.IsNullOrWhiteSpace(zone.ResolverVersion) || zone.ResolverVersion.Length > 200 ||
            zone.GapPolicy is not ("reject" or "shift-forward") ||
            zone.FoldPolicy is not ("reject" or "earlier" or "later") ||
            zone.RulesBytes is not { Length: > 0 })
            throw new ArgumentException("Timezone rules and provenance are required.", nameof(zone));
        var rulesHash = SHA256.HashData(zone.RulesBytes);
        return Write("timezone", w =>
        {
            w.WriteString("ianaZoneId", zone.IanaZoneId);
            w.WriteString("tzdbRelease", zone.TzdbRelease);
            w.WriteString("resolverVersion", zone.ResolverVersion);
            w.WriteString("gapPolicy", zone.GapPolicy);
            w.WriteString("foldPolicy", zone.FoldPolicy);
            w.WriteString("rulesSha256", Convert.ToHexString(rulesHash).ToLowerInvariant());
            w.WriteBase64String("rulesBytes", zone.RulesBytes);
        });
    }

    public static byte[] Hash(byte[] canonicalBytes)
    {
        ArgumentNullException.ThrowIfNull(canonicalBytes);
        if (canonicalBytes.Length == 0) throw new ArgumentException("Empty canonical policy.", nameof(canonicalBytes));
        return SHA256.HashData(canonicalBytes);
    }

    private static byte[] Write(string kind, Action<Utf8JsonWriter> content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("canonicalizationVersion", Version);
            writer.WriteString("kind", kind);
            content(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
