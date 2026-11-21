using System.Security.Cryptography;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

const string database = "FactoryConnect_V2_Dev";
var siteId = Guid.Parse("54981c9d-d569-4c9f-a977-78e66d02c1f2");
if (!((args.Length == 1 && args[0] is ("preview" or "write")) ||
      (args.Length == 5 && args[0] is ("timezone-preview" or "timezone-write"))))
    throw new ArgumentException("Usage: preview|write OR timezone-preview|timezone-write <TZif-path> <SHA256-hex> <tzdb-release> <resolver-version>");
var mode = args[0];
var server = Environment.GetEnvironmentVariable("FC2_POLICY_SQL_SERVER") ?? @".\FCINVENTORY";
if (server != @".\FCINVENTORY")
    throw new InvalidOperationException("Only the known local development SQL instance is permitted.");
var connectionString = new SqlConnectionStringBuilder
{
    DataSource = server, InitialCatalog = database, IntegratedSecurity = true,
    TrustServerCertificate = true, Encrypt = SqlConnectionEncryptOption.Mandatory,
    ApplicationName = "fc2-local-policy-bootstrap"
}.ConnectionString;
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
if (!string.Equals(connection.Database, database, StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected SQL database.");
if (mode is "timezone-preview" or "timezone-write")
{
    var rulesPath = Path.GetFullPath(args[1]);
    var rules = await File.ReadAllBytesAsync(rulesPath);
    if (rules.Length < 44 || rules.Length > 1048576 ||
        rules[0] != (byte)'T' || rules[1] != (byte)'Z' ||
        rules[2] != (byte)'i' || rules[3] != (byte)'f' ||
        rules[4] is not ((byte)'2' or (byte)'3' or (byte)'4'))
        throw new InvalidOperationException("Expected a supported IANA TZif v2/v3/v4 rules file.");
    var actualHash = Convert.ToHexString(SHA256.HashData(rules)).ToLowerInvariant();
    if (args[2].Length != 64 || !string.Equals(actualHash, args[2], StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("TZif rules SHA-256 does not match the independently supplied expected digest.");
    if (string.IsNullOrWhiteSpace(args[3]) || string.IsNullOrWhiteSpace(args[4]) ||
        args[3].Length > 32 || args[4].Length > 200)
        throw new InvalidOperationException("Explicit TZDB release and resolver version are required.");
    var zone = new PolicyCanonicalizer.Timezone("Asia/Kolkata", args[3], args[4],
        "reject", "reject", rules);
    var canonical = PolicyCanonicalizer.TimezonePolicy(zone);
    Console.WriteLine($"IANA zone: {zone.IanaZoneId}; TZDB release (operator supplied): {zone.TzdbRelease}");
    Console.WriteLine($"Resolver version (operator supplied): {zone.ResolverVersion}");
    Console.WriteLine($"Rules SHA-256: {actualHash}");
    Console.WriteLine($"Timezone policy SHA-256: {Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()}");
    Console.WriteLine("Gap/fold: reject/reject");
    if (mode == "timezone-preview")
    {
        Console.WriteLine("Read-only timezone preview complete. No revision written.");
        return;
    }
    var saved = await SqlPolicyRevisionWriter.SaveTimezoneAsync(connection, siteId, zone);
    Console.WriteLine($"Timezone revision: {saved.RevisionId:D}");
    return;
}
var snapshot = await PlanningPolicySnapshot.ReadAsync(connection, siteId);
var schedule = PolicyCanonicalizer.Schedule(snapshot.Shifts);
var breaks = PolicyCanonicalizer.BreakPolicy(snapshot.Breaks);
Console.WriteLine($"Site: {siteId:D}");
Console.WriteLine($"Shifts: {snapshot.Shifts.Count}; excluded breaks: {snapshot.Breaks.Count}");
Console.WriteLine($"Schedule SHA-256: {Convert.ToHexString(SHA256.HashData(schedule)).ToLowerInvariant()}");
Console.WriteLine($"Break SHA-256: {Convert.ToHexString(SHA256.HashData(breaks)).ToLowerInvariant()}");
if (mode == "preview")
{
    Console.WriteLine("Read-only preview complete. No revisions written.");
    return;
}

// Timezone payload is intentionally NOT generated here: authoritative TZDB rules,
// release, resolver version and gap/fold policies must be separately established.
// This command persists only the two fully validated planning snapshots.
var savedSchedule = await SqlPolicyRevisionWriter.SaveScheduleAsync(connection, siteId, snapshot.Shifts);
var savedBreak = await SqlPolicyRevisionWriter.SaveBreakPolicyAsync(connection, siteId, snapshot.Breaks);
Console.WriteLine($"Schedule revision: {savedSchedule.RevisionId:D}");
Console.WriteLine($"Break revision: {savedBreak.RevisionId:D}");
Console.WriteLine("Timezone revision NOT written; authoritative rules and provenance still required.");
