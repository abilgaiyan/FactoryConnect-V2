using System.Security.Cryptography;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

const string database = "FactoryConnect_V2_Dev";
var siteId = Guid.Parse("54981c9d-d569-4c9f-a977-78e66d02c1f2");
if (args.Length != 1 || args[0] is not ("preview" or "write"))
    throw new ArgumentException("Usage: dotnet run --project scripts/dev/PolicyBootstrap -- preview|write");
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
