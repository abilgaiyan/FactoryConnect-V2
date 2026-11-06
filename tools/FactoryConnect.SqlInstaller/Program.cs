using FactoryConnect.Sql;

if (args.Length is not (4 or 5) || (args.Length == 5 && args[4] != "--initialize-empty"))
{
    Console.Error.WriteLine("Usage: SqlInstaller <connection-env-name> <database> <expected-server-name> <installation-guid> [--initialize-empty]");
    return 2;
}
try
{
    var connection = Environment.GetEnvironmentVariable(args[0]) ?? throw new InvalidOperationException("Connection environment variable is missing.");
    var target = new InstallationTarget(connection, args[1], args[2], Guid.Parse(args[3]), args.Length == 5);
    var count = await new SqlInstaller().ApplyAsync(target);
    Console.WriteLine($"PASS: {count} migration(s) applied to the explicitly selected installation.");
    return 0;
}
catch (Exception)
{
    // Connection strings and SQL exception payloads may contain sensitive endpoint/configuration data.
    Console.Error.WriteLine("Installation failed; target validation or SQL migration did not complete. No successful acceptance claimed.");
    return 1;
}
