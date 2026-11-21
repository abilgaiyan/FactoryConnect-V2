using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Sql.Configuration;
using FactoryConnect.Sql.Activity;
using Microsoft.Data.SqlClient;

// Explicit source mapping is required; do not guess protocol address encoding or normalization.
if (args.Length != 7 || args[0] != "preview")
    throw new ArgumentException("Usage: preview <stream-key> <address-encoding> <address-utf8> <normalization> <effective-from-UTC> <capability-intent> (set FC2_MAPPING_ID, FC2_PACKAGE_ID and FC2_PACKAGE_REVISION_ID environment variables)");
var streamKey = args[1];
if (streamKey != "mtconnect:uuid:fc2-local-sim-0001")
    throw new ArgumentException("Expected the verified local simulator device UUID stream key.");
var addressEncoding = args[2];
var address = args[3];
var normalization = args[4];
if (addressEncoding != "utf-8" || string.IsNullOrWhiteSpace(address) || Encoding.UTF8.GetByteCount(address) > 512)
    throw new ArgumentException("Exact nonempty UTF-8 MTConnect address (up to 512 bytes) required.");
_ = ApprovedActivityValueMap.Parse(normalization);
var effectiveFrom = DateTimeOffset.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
if (effectiveFrom.Offset != TimeSpan.Zero)
    throw new ArgumentException("Effective-from must be explicitly UTC (+00:00 or Z).");
var capability = args[6].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (capability.Length == 0)
    throw new ArgumentException("At least one explicitly validated capability is required.");
// Mapping ID is deterministic for the first preview; no database writes.
var site = Guid.Parse("54981c9d-d569-4c9f-a977-78e66d02c1f2");
var machine = Guid.Parse("49befb26-0ac8-4bdf-87fe-a4ff9e6f3bfd");
var line = Guid.Parse("e0c48c4b-a48d-4d34-a830-bca7a599bd1d");
var timezone = Guid.Parse("a71c15b1-5ec1-49f5-a038-9e430d3c342a");
var schedule = Guid.Parse("068a14dc-ef3c-4624-a34b-4917668797fc");
var breaks = Guid.Parse("1662e94d-913f-4ec8-a16c-968efece8a53");
var timezoneHash = Convert.FromHexString("A5547DC0B1A6FAABF40BEA0914ACC52FB7C31ECB1CCA40E80A09DAC7D4FD0CC1");
var scheduleHash = Convert.FromHexString("3CA8364A62044979CC5D4CB80923452C2F571E1C3442B1A99EDDCC6ACDB2C1AE");
var breakHash = Convert.FromHexString("080E255B11B73DA83AB209A7A98EC4AADC945A0D8A716367E5209567940C4B00");
var builder = new SqlConnectionStringBuilder
{
    DataSource = @".\FCINVENTORY", InitialCatalog = "FactoryConnect_V2_Dev",
    IntegratedSecurity = true, TrustServerCertificate = true,
    Encrypt = SqlConnectionEncryptOption.Mandatory, ApplicationName = "fc2-package-preview-readonly"
};
await using var connection = new SqlConnection(builder.ConnectionString);
await connection.OpenAsync();
foreach (var (table, idColumn, id, hash) in new[]
{
    ("configuration.SiteTimezoneRevision", "TimezoneRevisionId", timezone, timezoneHash),
    ("configuration.SharedScheduleRevision", "ScheduleRevisionId", schedule, scheduleHash),
    ("configuration.SharedBreakPolicyRevision", "BreakPolicyRevisionId", breaks, breakHash)
})
{
    await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table} WHERE SiteId=@site AND {idColumn}=@id AND ContentHash=@hash", connection);
    command.Parameters.AddWithValue("@site", site);
    command.Parameters.AddWithValue("@id", id);
    command.Parameters.Add("@hash", System.Data.SqlDbType.Binary, 32).Value = hash;
    if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 1)
        throw new InvalidOperationException($"Pinned policy missing or hash mismatch: {table}");
}
await using (var command = new SqlCommand("""
    SELECT CASE WHEN EXISTS(SELECT 1 FROM [master].Site WHERE SiteId=@site AND IsRetired=0)
          AND EXISTS(SELECT 1 FROM [master].Machine WHERE SiteId=@site AND MachineId=@machine AND IsRetired=0)
          AND EXISTS(SELECT 1 FROM [master].ProductionLine WHERE SiteId=@site AND ProductionLineId=@line)
          THEN 1 ELSE 0 END
    """, connection))
{
    command.Parameters.AddWithValue("@site", site);
    command.Parameters.AddWithValue("@machine", machine);
    command.Parameters.AddWithValue("@line", line);
    if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
        throw new InvalidOperationException("Site, machine or production line readiness failed.");
}
var mapping = Guid.Parse(Environment.GetEnvironmentVariable("FC2_MAPPING_ID")
    ?? throw new ArgumentException("FC2_MAPPING_ID must be supplied explicitly as a GUID."));
var packageId = Guid.Parse(Environment.GetEnvironmentVariable("FC2_PACKAGE_ID")
    ?? throw new ArgumentException("FC2_PACKAGE_ID must be supplied explicitly as a GUID."));
var revisionId = Guid.Parse(Environment.GetEnvironmentVariable("FC2_PACKAGE_REVISION_ID")
    ?? throw new ArgumentException("FC2_PACKAGE_REVISION_ID must be supplied explicitly as a GUID."));
var definition = new PackageDefinition(packageId, revisionId, 1, site,
    new PolicyReference(timezone, timezoneHash),
    new[] { new MachineConfiguration(machine, line, effectiveFrom, null,
        new PolicyReference(schedule, scheduleHash), new PolicyReference(breaks, breakHash),
        new[] { new SignalDefinition(mapping, "machine-activity", "mtconnect", streamKey,
            addressEncoding, Encoding.UTF8.GetBytes(address), normalization) }, capability) });
var bytes = PackageCanonicalizer.Serialize(definition);
Console.WriteLine(Encoding.UTF8.GetString(bytes));
Console.WriteLine($"Canonical SHA-256: {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
Console.WriteLine("READ ONLY: no package submitted, reviewed or activated.");
