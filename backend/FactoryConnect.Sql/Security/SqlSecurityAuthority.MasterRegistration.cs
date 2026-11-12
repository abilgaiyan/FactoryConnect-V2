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


    public sealed record AdministrationLine(Guid ProductionLineId, Guid SiteId, string DisplayName, bool IsRetired);
    public Task<IReadOnlyList<AdministrationLine>> ReadAdministrationLinesAsync(
        byte[] sessionHash, CancellationToken ct = default)
    {
        if (sessionHash is null || sessionHash.Length != 32) throw new SecurityDeniedException();
        return ReadAsync(sessionHash, "configuration.master.manage", AuthorizationScope.Installation,
            async (connection, transaction, token) =>
            {
                await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT ProductionLineId, SiteId, DisplayName, IsRetired FROM master.ProductionLine ORDER BY DisplayName, ProductionLineId",
                    connection, transaction);
                await using var reader = await command.ExecuteReaderAsync(token);
                var lines = new List<AdministrationLine>();
                while (await reader.ReadAsync(token))
                    lines.Add(new AdministrationLine(reader.GetGuid(0), reader.GetGuid(1),
                        reader.GetString(2), reader.GetBoolean(3)));
                return (IReadOnlyList<AdministrationLine>)lines;
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

    public sealed record CommissioningAssignment(Guid MachineId, Guid ProductionLineId,
        string MtConnectDeviceId, Guid LastOperationId);

    public Task<IReadOnlyList<CommissioningAssignment>> ReadCommissioningAssignmentsAsync(
        byte[] sessionHash, CancellationToken ct = default)
    {
        if (sessionHash is null || sessionHash.Length != 32) throw new SecurityDeniedException();
        return ReadAsync(sessionHash, "configuration.master.manage", AuthorizationScope.Installation,
            async (connection, transaction, token) =>
            {
                await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT MachineId, ProductionLineId, MtConnectDeviceId, LastOperationId FROM master.MachineCommissioning ORDER BY MachineId",
                    connection, transaction);
                await using var reader = await command.ExecuteReaderAsync(token);
                var rows = new List<CommissioningAssignment>();
                while (await reader.ReadAsync(token))
                    rows.Add(new CommissioningAssignment(reader.GetGuid(0), reader.GetGuid(1),
                        reader.GetString(2), reader.GetGuid(3)));
                return (IReadOnlyList<CommissioningAssignment>)rows;
            }, ct);
    }

    // A commissioning assignment is administrative configuration, never acquisition activation.
    public Task SaveCommissioningAssignmentAsync(byte[] sessionHash, Guid operationId,
        Guid machineId, Guid productionLineId, string deviceId, CancellationToken ct = default)
    {
        if (sessionHash is null || sessionHash.Length != 32 || operationId == Guid.Empty)
            throw new SecurityDeniedException();
        if (machineId == Guid.Empty || productionLineId == Guid.Empty ||
            string.IsNullOrWhiteSpace(deviceId) || deviceId.Trim().Length > 200)
            throw new ArgumentException("Invalid commissioning assignment.");
        var normalizedDeviceId = deviceId.Trim();
        var token = sessionHash.ToArray();
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, action = "commissioning-assign", machineId, productionLineId,
            deviceId = normalizedDeviceId
        });
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
                return 0;
            }

            await using (var check = new Microsoft.Data.SqlClient.SqlCommand("""
                SELECT COUNT_BIG(*)
                FROM master.Machine AS m WITH (UPDLOCK, HOLDLOCK)
                JOIN master.ProductionLine AS l WITH (UPDLOCK, HOLDLOCK) ON l.SiteId=m.SiteId
                WHERE m.MachineId=@machine AND l.ProductionLineId=@line
                  AND m.IsRetired=0 AND l.IsRetired=0
                """, connection, transaction))
            {
                check.Parameters.AddWithValue("@machine", machineId);
                check.Parameters.AddWithValue("@line", productionLineId);
                if ((long)(await check.ExecuteScalarAsync(ct) ?? 0L) != 1)
                    throw new InvalidOperationException("Machine and production line must be active and in the same site.");
            }
            await Exec(connection, transaction, """
                INSERT security.SecurityOperation
                    (OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                VALUES (@op,@actor,@bytes,@hash,'I',NULL,NULL,@result);
                """, ct, ("@op", operationId), ("@actor", actor), ("@bytes", request),
                ("@hash", SHA256.HashData(request)),
                ("@result", JsonSerializer.SerializeToUtf8Bytes(new { saved = true })));
            await Exec(connection, transaction, """
                MERGE master.MachineCommissioning WITH (HOLDLOCK) AS target
                USING (SELECT @machine AS MachineId) AS source ON target.MachineId=source.MachineId
                WHEN MATCHED THEN UPDATE SET ProductionLineId=@line,
                    MtConnectDeviceId=@device, LastOperationId=@op
                WHEN NOT MATCHED THEN INSERT (MachineId,ProductionLineId,MtConnectDeviceId,LastOperationId)
                    VALUES (@machine,@line,@device,@op);
                """, ct, ("@machine", machineId), ("@line", productionLineId),
                ("@device", normalizedDeviceId), ("@op", operationId));
            await Exec(connection, transaction, """
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                VALUES (@id,@op,@actor,@action,@reason);
                """, ct, ("@id", Guid.NewGuid()), ("@op", operationId), ("@actor", actor),
                ("@action", "machine-commissioning-assign"),
                ("@reason", "Administrator assigned production line and MTConnect device"));
            return 0;
        }, ct);
    }

    // Registration is a master-data change, not an acquisition or machine activation.
    // The installation-level grant is deliberately separate from evidence-reading rights.
    public Task<Guid> RegisterMasterRecordAsync(byte[] sessionHash, Guid operationId,
        string kind, Guid? siteId, string displayName, CancellationToken ct = default, Guid? machineId = null)
    {
        if (sessionHash is null || sessionHash.Length != 32 || operationId == Guid.Empty)
            throw new SecurityDeniedException();
        if (kind is not ("site" or "line" or "machine") ||
            (kind == "site" ? siteId is not null : siteId is null || siteId == Guid.Empty) ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200 ||
            (machineId.HasValue && (kind != "machine" || machineId == Guid.Empty)))
            throw new ArgumentException("Invalid master-data registration request.");
        var name = displayName.Trim();
        var token = sessionHash.ToArray();
        var request = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, kind, siteId, displayName = name, machineId });
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
                _ => await store.RegisterMachineAsync(connection, transaction, siteId!.Value, name, machineId, ct)
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
