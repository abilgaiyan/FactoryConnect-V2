using FactoryConnect.Sql;
using Microsoft.Data.SqlClient;

// Child dispatch must precede fixture/bootstrap evaluation and SQL database creation.
if(args is ["--activity-child"])
{
    Environment.ExitCode=await ActivityProcessRestartAcceptance.RunChildAsync();
    return;
}

var canonicalPassed = ConfigurationAcceptance.CanonicalChecks();
if (args is ["--canonical-only"]) { Console.WriteLine($"PASS: {canonicalPassed} canonical checks."); return; }
var activityPure=ActivityInterpreterAcceptance.PureChecks();
var bootstrapPure=activityPure+(args is ["--ingestion-only"] or ["--host-only"] or ["--activity-history-only"] ? 0 : await RuntimeMtlsTransportAcceptance.Run()+RuntimeCertificateAcceptance.PureChecks()+BootstrapAcceptance.PureChecks()+RecoveryTrustAcceptance.PureChecks()+RosterEligibilityAcceptance.PureChecks());
if(args is ["--pure-only"]) { Console.WriteLine($"PASS: {canonicalPassed+bootstrapPure} pure acceptance checks."); return; }
var admin = Environment.GetEnvironmentVariable("FC2_TEST_SQL")
    ?? throw new InvalidOperationException("FC2_TEST_SQL is required; SQL acceptance cannot be skipped.");
var root = new SqlConnectionStringBuilder(admin) { InitialCatalog = "master" };
await using var control = new SqlConnection(root.ConnectionString);
await control.OpenAsync();
var server = Convert.ToString(await Scalar(control, "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'))"))!;
Console.WriteLine($"SQL engine: {await Scalar(control, "SELECT CONCAT(CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),' / ', CONVERT(nvarchar(128), SERVERPROPERTY('Edition')))")}");
var databases = new List<string>();
var passed = canonicalPassed+bootstrapPure;
try
{
    if(args is ["--ingestion-only"] or ["--host-only"] or ["--activity-history-only"])
    {
        var demoDb=await NewDatabase();var demoTarget=new InstallationTarget(demoDb.ConnectionString,demoDb.InitialCatalog,server,Guid.NewGuid(),true);await new SqlInstaller().ApplyAsync(demoTarget);
        var demoChecks=args is ["--host-only"]?await AcquisitionHostAcceptance.Run(demoTarget with{InitializeEmpty=false}):await MtConnectIngestionAcceptance.Run(demoTarget with{InitializeEmpty=false},measureActivityHistory:args is ["--activity-history-only"]);Console.WriteLine(args is ["--activity-history-only"]?$"PASS: {demoChecks} activity history checks; measured disposable SQL fixture, no production SLO claim.":args is ["--host-only"]?$"PASS: {demoChecks} whole-process acquisition host checks; controlled HTTP/mTLS and disposable SQL.":$"PASS: {demoChecks} first-machine ingestion checks; real HTTP/mTLS and disposable SQL.");return;
    }
    var main = await NewDatabase();
    var installer = new SqlInstaller();
    var target = new InstallationTarget(main.ConnectionString, main.InitialCatalog, server, Guid.NewGuid(), true);
    Check(await installer.ApplyAsync(target) == SqlMigration.BuiltIn().Count, "fresh install");
    Check(await installer.ApplyAsync(target with { InitializeEmpty = false }) == 0, "unchanged rerun");
    await Reject(() => installer.ApplyAsync(target with { InstallationId = Guid.NewGuid() }), "wrong installation");
    await Reject(() => installer.ApplyAsync(target with { ExpectedServer = "wrong-server" }), "wrong server");
    await Reject(() => installer.ApplyAsync(target with { Database = "master" }), "wrong database");
    var original = SqlMigration.BuiltIn();
    var upgrade = await NewDatabase();
    var upgradeTarget = target with {ConnectionString=upgrade.ConnectionString,Database=upgrade.InitialCatalog,InstallationId=Guid.NewGuid()};
    Check(await installer.ApplyAsync(upgradeTarget,[original[0]])==1,"install previous master baseline");
    Check(await installer.ApplyAsync(upgradeTarget)==original.Count-1,"upgrade existing master baseline");
    await Reject(() => installer.ApplyAsync(target, original.Select((m,i)=>i==0 ? m with {Sql=m.Sql+"\n-- changed"}:m).ToArray()), "checksum mismatch");
    await Reject(() => installer.ApplyAsync(target, [new("000-retroactive", "SELECT 1"), ..original]), "retroactive migration");
    var failure = new SqlMigration("999-failure", "CREATE TABLE dbo.ShouldRollback(Id int); THROW 51001, 'deliberate failure', 1;");
    await Reject(() => installer.ApplyAsync(target, [..original, failure]), "migration failure");
    await using (var c = new SqlConnection(main.ConnectionString))
    {
        await c.OpenAsync();
        Check(Convert.ToInt32(await Scalar(c, "SELECT COUNT(*) FROM sys.tables WHERE name='ShouldRollback'")) == 0, "DDL rollback");
        Check(Convert.ToInt32(await Scalar(c, "SELECT COUNT(*) FROM platform.SchemaMigration")) == original.Count, "ledger rollback");
        await Execute(c, """
            INSERT [master].Site(SiteId,DisplayName) VALUES
                ('11111111-1111-1111-1111-111111111111',N'One'),
                ('22222222-2222-2222-2222-222222222222',N'Two');
            INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES
                ('33333333-3333-3333-3333-333333333333','11111111-1111-1111-1111-111111111111',N'Machine');
            """);
        await Reject(() => Execute(c, "INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES(NEWID(),NEWID(),N'Orphan')"), "orphan site FK");
        await Reject(() => Execute(c, "UPDATE [master].Machine SET MachineId=NEWID()"), "immutable machine ID");
        await Reject(() => Execute(c, "UPDATE [master].Machine SET SiteId='22222222-2222-2222-2222-222222222222'"), "immutable machine site ownership");
        await Execute(c, "UPDATE [master].Machine SET DisplayName=N'Renamed machine'");
        Check(Convert.ToInt32(await Scalar(c, "SELECT COUNT(*) FROM [master].Machine WHERE MachineId='33333333-3333-3333-3333-333333333333'")) == 1, "rename preserves identity");
        await Reject(() => Execute(c, "DELETE [master].Site WHERE SiteId='11111111-1111-1111-1111-111111111111'"), "no cascading identity delete");
        await Reject(() => Execute(c, "INSERT [master].Site(SiteId,DisplayName) VALUES(NEWID(),N' ' )"), "empty name constraint");
    }
    var unknown = await NewDatabase();
    await using (var c = new SqlConnection(unknown.ConnectionString))
    { await c.OpenAsync(); await Execute(c, "CREATE TABLE dbo.ForeignProduct(Id int)"); }
    await Reject(() => installer.ApplyAsync(target with { ConnectionString = unknown.ConnectionString, Database = unknown.InitialCatalog }), "unknown nonempty database");
    var sealedDb = await NewDatabase();
    await Reject(() => installer.ApplyAsync(target with { ConnectionString = sealedDb.ConnectionString, Database = sealedDb.InitialCatalog, InitializeEmpty = false }), "initialization opt-in");
    var failedNew = target with { ConnectionString = sealedDb.ConnectionString, Database = sealedDb.InitialCatalog };
    await Reject(() => installer.ApplyAsync(failedNew, [new("001-fail", "THROW 51001, 'fail', 1;")]), "bootstrap rollback failure");
    await using (var c = new SqlConnection(sealedDb.ConnectionString))
    {
        await c.OpenAsync();
        Check(Convert.ToInt32(await Scalar(c, "SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('platform')")) == 0, "bootstrap fully rolled back");
    }
    var concurrent = await NewDatabase();
    var concurrentTarget = target with { ConnectionString = concurrent.ConnectionString, Database = concurrent.InitialCatalog };
    var results = await Task.WhenAll(installer.ApplyAsync(concurrentTarget), installer.ApplyAsync(concurrentTarget));
    Check(results.Sum() == original.Count, "concurrent installers apply exactly once");
    passed += await MachineInventoryAcceptance.Run(main.ConnectionString,target.InstallationId);
    passed += await ConfigurationAcceptance.Run(main.ConnectionString);
    SqlConnection.ClearAllPools();
    await Execute(control,$"ALTER DATABASE [{main.InitialCatalog}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;");
    passed += await SecurityAcceptance.Run(main.ConnectionString,target.InstallationId);
    passed += await GrantIssuanceAcceptance.Run(main.ConnectionString,target.InstallationId);
    passed += await HumanSessionAcceptance.Run(main.ConnectionString,target.InstallationId);
    var bootstrapDb=await NewDatabase();
    var bootstrapTarget=target with {ConnectionString=bootstrapDb.ConnectionString,Database=bootstrapDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(bootstrapTarget);
    passed += await BootstrapAcceptance.Run(bootstrapTarget with{InitializeEmpty=false});
    var competingDb=await NewDatabase();
    var competingTarget=target with {ConnectionString=competingDb.ConnectionString,Database=competingDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(competingTarget);
    passed += await BootstrapAcceptance.CompetingRequests(competingTarget with{InitializeEmpty=false});
    var identityDb=await NewDatabase();
    var identityTarget=target with {ConnectionString=identityDb.ConnectionString,Database=identityDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(identityTarget);
    passed += await HumanIdentityAcceptance.Run(identityTarget with{InitializeEmpty=false});
    var recoveryDb=await NewDatabase();
    var recoveryTarget=target with {ConnectionString=recoveryDb.ConnectionString,Database=recoveryDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(recoveryTarget);
    passed += await RecoveryTrustAcceptance.Run(recoveryTarget with{InitializeEmpty=false});
    var workflowDb=await NewDatabase();
    var workflowTarget=target with {ConnectionString=workflowDb.ConnectionString,Database=workflowDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(workflowTarget);
    passed += await ConfigurationWorkflowAcceptance.Run(workflowTarget with{InitializeEmpty=false});
    var activationDb=await NewDatabase();
    var activationTarget=target with {ConnectionString=activationDb.ConnectionString,Database=activationDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(activationTarget);
    passed += await ActivationRequestAcceptance.Run(activationTarget with{InitializeEmpty=false});
    var runtimeDb=await NewDatabase();
    var runtimeTarget=target with {ConnectionString=runtimeDb.ConnectionString,Database=runtimeDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(runtimeTarget);
    passed += await RuntimeCredentialAcceptance.Run(runtimeTarget with{InitializeEmpty=false});
    var assignmentDb=await NewDatabase();
    var assignmentTarget=target with {ConnectionString=assignmentDb.ConnectionString,Database=assignmentDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(assignmentTarget);
    passed += await RuntimeAssignmentAcceptance.Run(assignmentTarget with{InitializeEmpty=false});
    var certificateDb=await NewDatabase();
    var certificateTarget=target with {ConnectionString=certificateDb.ConnectionString,Database=certificateDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(certificateTarget);
    passed += await RuntimeCertificateAcceptance.Run(certificateTarget with{InitializeEmpty=false});
    var mtlsDb=await NewDatabase();
    var mtlsTarget=target with {ConnectionString=mtlsDb.ConnectionString,Database=mtlsDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(mtlsTarget);
    passed += await RuntimeMtlsSqlAcceptance.Run(mtlsTarget with{InitializeEmpty=false});
    var acknowledgementDb=await NewDatabase();
    var acknowledgementTarget=target with {ConnectionString=acknowledgementDb.ConnectionString,Database=acknowledgementDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(acknowledgementTarget);
    passed += await ActivationAcknowledgementAcceptance.Run(acknowledgementTarget with{InitializeEmpty=false});
    var operationalDb=await NewDatabase();
    var operationalTarget=target with {ConnectionString=operationalDb.ConnectionString,Database=operationalDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(operationalTarget);
    passed += await MachineOperationalAcceptance.Run(operationalTarget with{InitializeEmpty=false});
    var safetyDb=await NewDatabase();
    var safetyTarget=target with {ConnectionString=safetyDb.ConnectionString,Database=safetyDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(safetyTarget);
    passed += await SafetyRevocationAcceptance.Run(safetyTarget with{InitializeEmpty=false});
    var rosterDb=await NewDatabase();
    var rosterTarget=target with {ConnectionString=rosterDb.ConnectionString,Database=rosterDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(rosterTarget);
    passed += await TrustedRosterAcceptance.Run(rosterTarget with{InitializeEmpty=false});
    var discoveryDb=await NewDatabase();
    var discoveryTarget=target with {ConnectionString=discoveryDb.ConnectionString,Database=discoveryDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(discoveryTarget);
    passed += await CommissioningDiscoveryAcceptance.Run(discoveryTarget with{InitializeEmpty=false});
    var ingestionDb=await NewDatabase();
    var ingestionTarget=target with {ConnectionString=ingestionDb.ConnectionString,Database=ingestionDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(ingestionTarget);
    passed += await MtConnectIngestionAcceptance.Run(ingestionTarget with{InitializeEmpty=false});
    var activityDb=await NewDatabase();
    var activityTarget=target with {ConnectionString=activityDb.ConnectionString,Database=activityDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(activityTarget);
    passed += await ActivitySqlDurabilityAcceptance.Run(activityTarget with {InitializeEmpty=false});
    var hostDb=await NewDatabase();
    var hostTarget=target with {ConnectionString=hostDb.ConnectionString,Database=hostDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(hostTarget);
    passed += await AcquisitionHostAcceptance.Run(hostTarget with{InitializeEmpty=false});
    var performanceDb=await NewDatabase();
    var performanceTarget=target with {ConnectionString=performanceDb.ConnectionString,Database=performanceDb.InitialCatalog,InstallationId=Guid.NewGuid()};
    await installer.ApplyAsync(performanceTarget);
    passed += await InventoryPerformanceAcceptance.Run(performanceTarget with{InitializeEmpty=false});
    Console.WriteLine($"PASS: {passed} SQL acceptance checks; no skips.");
}
finally
{
    SqlConnection.ClearAllPools();
    foreach (var database in databases)
        await Execute(control, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
}

async Task<SqlConnectionStringBuilder> NewDatabase()
{
    var name = "FactoryConnect_V2_Test_" + Guid.NewGuid().ToString("N");
    // Names are generated here, never supplied by external input. Only these databases are dropped.
    await Execute(control, $"CREATE DATABASE [{name}]");
    databases.Add(name);
    return new SqlConnectionStringBuilder(admin) { InitialCatalog = name };
}
void Check(bool condition, string name)
{ if (!condition) throw new InvalidOperationException("FAILED: " + name); passed++; Console.WriteLine("PASS: " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception e) when (e is SqlException or InvalidOperationException)
    { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejection: " + name);
}
static async Task<object?> Scalar(SqlConnection c, string sql)
{ await using var command = new SqlCommand(sql, c); return await command.ExecuteScalarAsync(); }
static async Task Execute(SqlConnection c, string sql)
{ await using var command = new SqlCommand(sql, c); command.CommandTimeout = 60; await command.ExecuteNonQueryAsync(); }
