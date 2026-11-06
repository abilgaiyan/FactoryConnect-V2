using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// SQL-backed inventory is deliberately distinct from the active runtime roster.
// This endpoint never claims that a machine is commissioned, live, connected or reporting.
app.MapGet("/internal/installation/v1/inventory", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var connectionString = configuration["FC2_READONLY_SQL"];
    var identity = configuration["FC2_INSTALLATION_ID"];
    if (string.IsNullOrWhiteSpace(connectionString) || !Guid.TryParse(identity, out var expectedInstallation) ||
        expectedInstallation == Guid.Empty)
        return InventoryUnavailable();

    try
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        // Do not allow the host to silently target master or the original product database.
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog) ||
            builder.InitialCatalog.Equals("master", StringComparison.OrdinalIgnoreCase) ||
            builder.InitialCatalog.Equals("FactoryConnect", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(builder.AttachDBFilename))
            return InventoryUnavailable();

        await using var sql = new SqlConnection(builder.ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await using (var identityCommand = new SqlCommand(
            "SELECT InstallationId FROM platform.InstallationIdentity WHERE Singleton=1 AND Product=N\'FactoryConnect-V2\'", sql, transaction))
        {
            var actual = await identityCommand.ExecuteScalarAsync(cancellationToken);
            if (actual is not Guid installed || installed != expectedInstallation)
                return InventoryUnavailable();
        }

        var machines = new List<object>();
        await using (var command = new SqlCommand("""
            SELECT m.MachineId,m.SiteId,m.DisplayName,m.IsRetired
            FROM [master].Machine m
            ORDER BY m.SiteId,m.MachineId
            """, sql, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                machines.Add(new
                {
                    machineId = reader.GetGuid(0),
                    siteId = reader.GetGuid(1),
                    displayName = reader.GetString(2),
                    isRetired = reader.GetBoolean(3)
                });
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new
        {
            authority = "v2-sql-master-inventory",
            installationId = expectedInstallation,
            semantics = "configured-identities-only",
            machineCount = machines.Count,
            machines
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (SqlException)
    {
        return InventoryUnavailable();
    }
    catch (InvalidOperationException)
    {
        return InventoryUnavailable();
    }
    catch (ArgumentException)
    {
        return InventoryUnavailable();
    }
});

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health", NotReady);
app.MapGet("/health/ready", NotReady);
app.MapGet("/dashboard/config", () => Results.Ok(new
{
    reportingBasePath = "/",
    requestTimeoutMilliseconds = 30000,
    sources = Array.Empty<object>()
}));

// No evidence authority exists yet. Never return simulated production results.
app.MapMethods("/api/{**path}", ["GET", "POST"], NotReady);
app.Run();

static IResult InventoryUnavailable() => Results.Problem(
    statusCode: StatusCodes.Status503ServiceUnavailable,
    title: "V2 SQL inventory is unavailable",
    detail: "A verified installation-scoped, read-only inventory authority is not available.");

static IResult NotReady() => Results.Problem(
    statusCode: StatusCodes.Status503ServiceUnavailable,
    title: "V2 evidence authority is not configured",
    detail: "This foundation host has no acquisition or reporting provider.");
