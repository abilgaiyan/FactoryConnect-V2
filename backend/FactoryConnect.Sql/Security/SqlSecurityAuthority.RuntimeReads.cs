using System.Diagnostics;
using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed record RuntimeMachineAuthority(Guid RuntimeIdentityId,Guid AssignmentId,long AssignmentGeneration,long CredentialGeneration,Guid SiteId,Guid MachineId);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Exact assigned-machine read under current authority. No human session or caller asserted runtime ID is accepted.</summary>
    public Task<T> ReadRuntimeMachineAsync<T>(RuntimeMtlsChannel channel,Guid siteId,Guid machineId,
        Func<SqlConnection,SqlTransaction,RuntimeMachineAuthority,Task<T>> handler,CancellationToken ct=default)
    {
        _=AuthorizationScope.Machine(siteId,machineId);ArgumentNullException.ThrowIfNull(channel);ArgumentNullException.ThrowIfNull(handler);
        // TLS/CA work happens before opening a SQL transaction. No external validation holds authority gates.
        var evidence=channel.Revalidate(ct);var certificateHash=SHA256.HashData(evidence.Certificate);
        return Transaction(null,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.runtime.read",true);
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),false,ct);
            var state=await State(c,t,ct);
            if(Stopwatch.GetElapsedTime(evidence.CheckedAt)>TimeSpan.FromSeconds(5)||evidence.RootFrom>state.Now||evidence.RootUntil<=state.Now)throw new SecurityDeniedException();
            RuntimeMachineAuthority authority;
            await using(var q=Command(c,t,"""
                SELECT b.RuntimeIdentityId,a.AssignmentId,a.AssignmentGeneration,b.CredentialGeneration,b.CertificateDer
                FROM security.RuntimeCertificateBinding b
                JOIN security.RuntimeCredentialState currentCredential ON currentCredential.RuntimeIdentityId=b.RuntimeIdentityId AND currentCredential.CredentialGeneration=b.CredentialGeneration
                JOIN security.RuntimeCredentialGeneration g ON g.RuntimeIdentityId=b.RuntimeIdentityId AND g.CredentialGeneration=b.CredentialGeneration
                JOIN security.RuntimeCredentialPublicKey k ON k.RuntimeIdentityId=g.RuntimeIdentityId AND k.CredentialGeneration=g.CredentialGeneration AND k.PublicKeyHash=g.CredentialBindingHash
                JOIN security.Principal p ON p.PrincipalId=b.RuntimeIdentityId AND p.Kind='R'
                JOIN configuration.RuntimeAssignmentState s ON s.RuntimeIdentityId=b.RuntimeIdentityId AND s.SiteId=@site AND s.MachineId=@machine
                JOIN configuration.RuntimeAssignment a ON a.AssignmentId=s.AssignmentId AND a.AssignmentGeneration=s.AssignmentGeneration
                JOIN [master].Machine m ON m.MachineId=s.MachineId AND m.SiteId=s.SiteId
                JOIN [master].Site site ON site.SiteId=s.SiteId
                WHERE b.CertificateHash=@certificateHash AND b.TrustAnchorHash=@anchorHash
                    AND b.ValidFrom<=@now AND b.ExpiresAt>@now AND g.ValidFrom<=@now AND g.ExpiresAt>@now
                    AND a.EffectiveFrom<=@now AND (a.EffectiveTo IS NULL OR a.EffectiveTo>@now)
                    AND m.IsRetired=0 AND site.IsRetired=0
                    AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=p.PrincipalId)
                    AND NOT EXISTS(SELECT 1 FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=g.RuntimeIdentityId AND CredentialGeneration=g.CredentialGeneration)
                    AND NOT EXISTS(SELECT 1 FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=a.AssignmentId);
                """))
            {
                Add(q,("@site",siteId),("@machine",machineId),("@now",state.Now));
                q.Parameters.Add("@certificateHash",SqlDbType.Binary,32).Value=certificateHash;
                q.Parameters.Add("@anchorHash",SqlDbType.Binary,32).Value=evidence.AnchorHash;
                await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)||!((byte[])r[4]).AsSpan().SequenceEqual(evidence.Certificate))throw new SecurityDeniedException();
                authority=new(r.GetGuid(0),r.GetGuid(1),r.GetInt64(2),r.GetInt64(3),siteId,machineId);
                if(await r.ReadAsync(ct))throw new SecurityDeniedException();
            }
            return await handler(c,t,authority);
        },ct);
    }
}
