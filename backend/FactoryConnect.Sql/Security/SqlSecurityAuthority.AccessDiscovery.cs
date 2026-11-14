using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Authenticated access managers may discover the current epoch and their own live delegable grants.</summary>
    public Task<byte[]> ReadAccessAdministrationAsync(byte[] sessionHash, CancellationToken ct = default) =>
        ReadAsync(sessionHash, "security.access.manage", AuthorizationScope.Installation,
            async (connection, transaction, cancellation) =>
            {
                long epoch;
                await using (var command = new SqlCommand(
                    "SELECT AuthorityEpoch FROM security.InstallationState WHERE Singleton=1", connection, transaction))
                    epoch = (long)(await command.ExecuteScalarAsync(cancellation) ??
                        throw new SecurityDeniedException());
                var grants = new List<object>();
                await using (var command = new SqlCommand("""
                    SELECT g.GrantId,g.Permission,g.ScopeKind,g.SiteId,g.MachineId,g.EffectiveFrom,g.ExpiresAt
                    FROM security.PermissionGrant g
                    JOIN security.SessionAuthority s ON s.PrincipalId=g.GranteeId
                    WHERE s.TokenHash=@session AND g.CanDelegate=1
                      AND g.EffectiveFrom<=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')
                      AND g.ExpiresAt>TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')
                      AND NOT EXISTS(SELECT 1 FROM security.GrantRevocation r WHERE r.GrantId=g.GrantId)
                    ORDER BY g.Permission,g.GrantId
                    """, connection, transaction))
                {
                    command.Parameters.Add("@session", System.Data.SqlDbType.VarBinary, 32).Value = sessionHash;
                    await using var reader = await command.ExecuteReaderAsync(cancellation);
                    while (await reader.ReadAsync(cancellation))
                        grants.Add(new {
                            grantId=reader.GetGuid(0),permission=reader.GetString(1),scopeKind=reader.GetString(2),
                            siteId=reader.IsDBNull(3)?(Guid?)null:reader.GetGuid(3),
                            machineId=reader.IsDBNull(4)?(Guid?)null:reader.GetGuid(4),
                            effectiveFrom=reader.GetDateTimeOffset(5),expiresAt=reader.GetDateTimeOffset(6)
                        });
                }
                return JsonSerializer.SerializeToUtf8Bytes(new { authorityEpoch=epoch, delegableGrants=grants });
            }, ct);
}
