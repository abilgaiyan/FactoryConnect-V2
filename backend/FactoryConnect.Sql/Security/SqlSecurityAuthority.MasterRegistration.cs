using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql.MasterData;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    public sealed record AdministrationSite(Guid SiteId, string DisplayName, bool IsRetired);

    public Task<IReadOnlyList<AdministrationSite>> ReadAdministrationSitesAsync(
        byte[] sessionHash, CancellationToken ct = default)
    {
        if (sessionHash is null || sessionHash.Length != 32) throw new SecurityDeniedException();
        return ReadAsync(sessionHash, "configuration.master.manage", AuthorizationScope.Installation,
            async (connection, transaction, token) =>
            {
                await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT SiteId, DisplayName, IsRetired FROM master.Site ORDER BY DisplayName, SiteId",
                    connection, transaction);
                await using var reader = await command.ExecuteReaderAsync(token);
                var sites = new List<AdministrationSite>();
                while (await reader.ReadAsync(token))
                    sites.Add(new AdministrationSite(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
                return (IReadOnlyList<AdministrationSite>)sites;
            }, ct);
    }


    public sealed record AdministrationMachine(Guid MachineId, Guid SiteId, string DisplayName, bool IsRetired);
    public Task<IReadOnlyList<AdministrationMachine>> ReadAdministrationMachinesAsync(
        byte[] sessionHash, CancellationToken ct = default)
    {
        if (sessionHash is null || sessionHash.Length != 32) throw new SecurityDeniedException();
        return ReadAsync(sessionHash, "configuration.master.manage", AuthorizationScope.Installation,
            async (connection, transaction, token) =>
            {
                await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT MachineId, SiteId, DisplayName, IsRetired FROM master.Machine ORDER BY DisplayName, MachineId",
                    connection, transaction);
                await using var reader = await command.ExecuteReaderAsync(token);
                var machines = new List<AdministrationMachine>();
                while (await reader.ReadAsync(token))
                    machines.Add(new AdministrationMachine(reader.GetGuid(0), reader.GetGuid(1),
                        reader.GetString(2), reader.GetBoolean(3)));
                return (IReadOnlyList<AdministrationMachine>)machines;
            }, ct);
    }

    // Registration is a master-data change, not an acquisition or machine activation.
    // The installation-level grant is deliberately separate from evidence-reading rights.
    public Task<Guid> RegisterMasterRecordAsync(byte[] sessionHash, Guid operationId,
        string kind, Guid? siteId, string displayName, CancellationToken ct = default)
    {
        if (sessionHash is null || sessionHash.Length != 32 || operationId == Guid.Empty)
            throw new SecurityDeniedException();
        if (kind is not ("site" or "line" or "machine") ||
            (kind == "site" ? siteId is not null : siteId is null || siteId == Guid.Empty) ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            throw new ArgumentException("Invalid master-data registration request.");
        var name = displayName.Trim();
        var token = sessionHash.ToArray();
        var request = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, kind, siteId, displayName = name });
        return Transaction(operationId, true, async (connection, transaction) =>
        {
            var state = await State(connection, transaction, ct);
            var actor = await Session(connection, transaction, token, state.Now, ct);
            await Authorize(connection, transaction, actor, "configuration.master.manage",
                AuthorizationScope.Installation, state.Now, ct);
            var completed = await FindOperation(connection, transaction, operationId, ct);
            if (completed is not null)
            {
                if (completed.Actor != actor || !completed.Request.AsSpan().SequenceEqual(request))
                    throw new SecurityConflictException();
                using var result = JsonDocument.Parse(completed.Result);
                return result.RootElement.GetProperty("recordId").GetGuid();
            }

            var store = new MachineAdministrationStore(connectionString);
            var recordId = kind switch
            {
                "site" => await store.RegisterSiteAsync(connection, transaction, name, ct),
                "line" => await store.RegisterLineAsync(connection, transaction, siteId!.Value, name, ct),
                _ => await store.RegisterMachineAsync(connection, transaction, siteId!.Value, name, ct)
            };
            var resultBytes = JsonSerializer.SerializeToUtf8Bytes(new { recordId });
            await Exec(connection, transaction, """
                INSERT security.SecurityOperation
                    (OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                VALUES (@op,@actor,@bytes,@hash,'I',NULL,NULL,@result);
                """, ct, ("@op", operationId), ("@actor", actor), ("@bytes", request),
                ("@hash", SHA256.HashData(request)), ("@result", resultBytes));
            await Exec(connection, transaction, """
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                VALUES (@id,@op,@actor,@action,@reason);
                """, ct, ("@id", Guid.NewGuid()), ("@op", operationId), ("@actor", actor),
                ("@action", "master-register-" + kind), ("@reason", "Administrator registered " + kind));
            return recordId;
        }, ct);
    }
}
