using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FactoryConnect.Sql.Configuration;

/// <summary>Version 1 canonical package profile. Exact source strings/bytes are never normalized.</summary>
public static class PackageCanonicalizer
{
    public const int Version = 1;
    public static byte[] Serialize(PackageDefinition package)
    {
        ArgumentNullException.ThrowIfNull(package);
        Id(package.PackageId); Id(package.RevisionId); Id(package.SiteId); Policy(package.Timezone);
        if (package.RevisionNumber < 1 || package.Machines.Count == 0) throw Invalid();
        Unique(package.Machines.Select(m => Key(m.MachineId)));
        Unique(package.Machines.SelectMany(m => m.Signals).Select(s => Key(s.MappingId)));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("canonicalizationVersion", Version);
            writer.WriteString("packageId", Key(package.PackageId));
            writer.WriteString("revisionId", Key(package.RevisionId));
            writer.WriteNumber("revisionNumber", package.RevisionNumber);
            writer.WriteString("siteId", Key(package.SiteId));
            WritePolicy(writer, "timezone", package.Timezone);
            writer.WriteStartArray("machines");
            foreach (var machine in package.Machines.OrderBy(m => Key(m.MachineId), StringComparer.Ordinal))
            {
                Id(machine.MachineId); Id(machine.ProductionLineId); Policy(machine.Schedule); Policy(machine.BreakPolicy);
                if (machine.EffectiveTo is { } end && end <= machine.EffectiveFrom) throw Invalid();
                Unique(machine.Signals.Select(s => s.SemanticKey));
                Unique(machine.CapabilityIntent);
                writer.WriteStartObject();
                writer.WriteString("machineId", Key(machine.MachineId));
                writer.WriteString("productionLineId", Key(machine.ProductionLineId));
                writer.WriteString("effectiveFrom", Instant(machine.EffectiveFrom));
                if (machine.EffectiveTo is { } to) writer.WriteString("effectiveTo", Instant(to));
                else writer.WriteNull("effectiveTo");
                WritePolicy(writer, "schedule", machine.Schedule);
                WritePolicy(writer, "breakPolicy", machine.BreakPolicy);
                writer.WriteStartArray("signals");
                foreach (var signal in machine.Signals.OrderBy(s => Key(s.MappingId), StringComparer.Ordinal))
                {
                    Id(signal.MappingId); Text(signal.SemanticKey, 100); Text(signal.Protocol, 100);
                    Text(signal.StreamKey, 1024); Text(signal.AddressEncoding, 100); Text(signal.Normalization, 100);
                    if (signal.AddressBytes.Length is < 1 or > 4096) throw Invalid();
                    writer.WriteStartObject();
                    writer.WriteString("mappingId", Key(signal.MappingId));
                    writer.WriteString("semanticKey", signal.SemanticKey);
                    writer.WriteString("protocol", signal.Protocol);
                    writer.WriteString("streamKey", signal.StreamKey);
                    writer.WriteString("addressEncoding", signal.AddressEncoding);
                    writer.WriteBase64String("addressBytes", signal.AddressBytes);
                    writer.WriteString("normalization", signal.Normalization);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("capabilityIntent");
                foreach (var capability in machine.CapabilityIntent.Order(StringComparer.Ordinal))
                { Text(capability, 100); writer.WriteStringValue(capability); }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    public static byte[] Hash(PackageDefinition package) => SHA256.HashData(Serialize(package));
    private static string Key(Guid id) => id.ToString("D");
    private static string Instant(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static void WritePolicy(Utf8JsonWriter w, string name, PolicyReference policy)
    {
        w.WriteStartObject(name); w.WriteString("revisionId", Key(policy.RevisionId));
        w.WriteString("contentHash", Convert.ToHexString(policy.ContentHash).ToLowerInvariant()); w.WriteEndObject();
    }
    private static void Id(Guid value) { if (value == Guid.Empty) throw Invalid(); }
    private static void Policy(PolicyReference value) { Id(value.RevisionId); if (value.ContentHash.Length != 32) throw Invalid(); }
    private static void Text(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw Invalid();
        // Reject malformed UTF-16 instead of letting JSON replace an unpaired surrogate.
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw Invalid(); }
            else if (char.IsLowSurrogate(value[i])) throw Invalid();
        }
    }
    private static void Unique(IEnumerable<string> values)
    { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var value in values) if (!seen.Add(value)) throw Invalid(); }
    private static ArgumentException Invalid() => new("Package identity, interval, policy reference or exact source field is invalid.");
}
