using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql.Inventory;

namespace FactoryConnect.Sql.Security;

public sealed record TrustedRosterItem(Guid MachineId,RosterStatus Status,string Reason,Guid? PackageRevisionId,
    Guid? AcknowledgementId,Guid? RuntimeIdentityId,long? ActivationGeneration,long? OperationalGeneration,IReadOnlyList<string> ConfiguredCapabilityIntent);
public sealed record TrustedSiteRoster(Guid SiteId,DateTimeOffset EvaluatedAt,long SecurityEpoch,string Status,IReadOnlyList<TrustedRosterItem> Machines);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Materialized database eligibility only. No caller proof flags, as-of time, HTTP binding or live transport claim.</summary>
    public Task<TrustedSiteRoster> ReadTrustedSiteRosterAsync(byte[] sessionHash,Guid siteId,CancellationToken ct=default)
    {
        var scope=AuthorizationScope.Site(siteId);var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.roster.read",true);
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);await Authorize(c,t,actor,"configuration.read",scope,state.Now,ct);
            await using var q=Command(c,t,"""
                IF NOT EXISTS(SELECT 1 FROM [master].Site WITH(HOLDLOCK) WHERE SiteId=@site) THROW 51042,'Unknown roster site.',1;
                SELECT TOP(101) m.MachineId INTO #machines FROM [master].Machine m WITH(HOLDLOCK) WHERE m.SiteId=@site ORDER BY m.MachineId;
                IF (SELECT COUNT(*) FROM #machines)>100 THROW 51042,'Site exceeds complete roster bound.',1;
                SELECT m.MachineId,h.AcknowledgementId,a.PackageRevisionId,a.RuntimeIdentityId,a.ActivationGeneration,a.OperationalGeneration,p.TimezoneRevisionId,p.TimezoneHash,pa.ScheduleRevisionId,pa.ScheduleHash,pa.BreakPolicyRevisionId,pa.BreakPolicyHash,pa.ProductionLineId,pa.EffectiveFrom AS PackageFrom,pa.EffectiveTo AS PackageUntil,
                    CASE
                    WHEN os.StateId IS NULL AND EXISTS(SELECT 1 FROM configuration.MachineOperationalTransition history WHERE history.SiteId=@site AND history.MachineId=m.MachineId) THEN 'MissingOperationalHead'
                    WHEN h.AcknowledgementId IS NULL AND EXISTS(SELECT 1 FROM configuration.RuntimeActivationAcknowledgement history WHERE history.SiteId=@site AND history.MachineId=m.MachineId) THEN 'MissingAcknowledgementHead'
                    WHEN h.AcknowledgementId IS NOT NULL AND (p.PackageRevisionId IS NULL OR p.SubmittedAt IS NULL OR v.ReviewId IS NULL OR pa.MachineId IS NULL OR ah.ActivationId IS NULL OR cs.RuntimeIdentityId IS NULL OR ass.AssignmentId IS NULL OR line.ProductionLineId IS NULL) THEN 'IncompleteAuthority'
                    WHEN m.IsRetired=1 OR site.IsRetired=1 OR line.IsRetired=1 THEN 'Retired'
                    WHEN os.StateId IS NULL THEN 'UnknownOperationalState'
                    WHEN o.Status<>'enabled' THEN 'DisabledOrSuspended'
                    WHEN h.AcknowledgementId IS NULL THEN 'NoAcknowledgedConfiguration'
                    WHEN o.Generation<>a.OperationalGeneration THEN 'OperatingGenerationChanged'
                    WHEN cs.CredentialGeneration<>a.CredentialGeneration OR ass.AssignmentId<>a.AssignmentId OR ass.AssignmentGeneration<>a.AssignmentGeneration OR ass.RuntimeIdentityId<>a.RuntimeIdentityId THEN 'RuntimeGenerationChanged'
                    WHEN g.RuntimeIdentityId IS NULL OR cert.RuntimeIdentityId IS NULL OR k.RuntimeIdentityId IS NULL OR rp.PrincipalId IS NULL THEN 'IncompleteAuthority'
                    WHEN EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=a.RuntimeIdentityId)
                        OR EXISTS(SELECT 1 FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=a.RuntimeIdentityId AND CredentialGeneration=a.CredentialGeneration)
                        OR EXISTS(SELECT 1 FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=a.AssignmentId)
                        OR EXISTS(SELECT 1 FROM configuration.ActivationRequestRevocation WHERE ActivationId=a.ActivationId)
                        OR EXISTS(SELECT 1 FROM configuration.PackageApprovalRevocation WHERE ReviewId=a.ReviewId) THEN 'Revoked'
                    WHEN EXISTS(SELECT 1 FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind='timezone' AND TimezoneRevisionId=p.TimezoneRevisionId AND ContentHash=p.TimezoneHash AND EffectiveFrom<=@now)
                        OR EXISTS(SELECT 1 FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind='schedule' AND ScheduleRevisionId=pa.ScheduleRevisionId AND ContentHash=pa.ScheduleHash AND EffectiveFrom<=@now)
                        OR EXISTS(SELECT 1 FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind='break' AND BreakPolicyRevisionId=pa.BreakPolicyRevisionId AND ContentHash=pa.BreakPolicyHash AND EffectiveFrom<=@now) THEN 'PolicyRevoked'
                    WHEN a.AppliedFrom>@now OR (a.EffectiveTo IS NOT NULL AND a.EffectiveTo<=@now)
                        OR ar.EffectiveFrom>@now OR (ar.EffectiveTo IS NOT NULL AND ar.EffectiveTo<=@now)
                        OR pa.EffectiveFrom>@now OR (pa.EffectiveTo IS NOT NULL AND pa.EffectiveTo<=@now)
                        OR assignment.EffectiveFrom>@now OR (assignment.EffectiveTo IS NOT NULL AND assignment.EffectiveTo<=@now)
                        OR g.ValidFrom>@now OR g.ExpiresAt<=@now OR cert.ValidFrom>@now OR cert.ExpiresAt<=@now OR o.EffectiveFrom>@now THEN 'OutsideEffectiveInterval'
                    ELSE 'EligibleConfigurationNotConnectivity' END AS Reason
                INTO #cut
                FROM #machines selected JOIN [master].Machine m WITH(HOLDLOCK) ON m.MachineId=selected.MachineId
                JOIN [master].Site site WITH(HOLDLOCK) ON site.SiteId=m.SiteId
                LEFT JOIN configuration.MachineOperationalState os ON os.MachineId=m.MachineId AND os.SiteId=@site
                LEFT JOIN configuration.MachineOperationalTransition o ON o.StateId=os.StateId AND o.Generation=os.Generation
                LEFT JOIN configuration.MachineAcknowledgementHead h ON h.MachineId=m.MachineId AND h.SiteId=@site
                LEFT JOIN configuration.RuntimeActivationAcknowledgement a ON a.AcknowledgementId=h.AcknowledgementId AND a.ActivationGeneration=h.ActivationGeneration
                LEFT JOIN configuration.ActivationRequest ar ON ar.ActivationId=a.ActivationId
                LEFT JOIN configuration.MachineActivationRequestHead ah ON ah.SiteId=@site AND ah.MachineId=m.MachineId
                LEFT JOIN configuration.ConfigurationPackageRevision p ON p.PackageRevisionId=a.PackageRevisionId AND p.SiteId=@site AND p.ContentHash=a.ContentHash
                LEFT JOIN configuration.PackageReview v ON v.ReviewId=a.ReviewId AND v.PackageRevisionId=p.PackageRevisionId AND v.ContentHash=p.ContentHash AND v.Decision='approved'
                LEFT JOIN configuration.PackageMachineAssignment pa ON pa.PackageRevisionId=p.PackageRevisionId AND pa.SiteId=@site AND pa.MachineId=m.MachineId
                LEFT JOIN [master].ProductionLine line WITH(HOLDLOCK) ON line.ProductionLineId=pa.ProductionLineId AND line.SiteId=@site
                LEFT JOIN configuration.RuntimeAssignmentState ass ON ass.SiteId=@site AND ass.MachineId=m.MachineId
                LEFT JOIN configuration.RuntimeAssignment assignment ON assignment.AssignmentId=a.AssignmentId
                LEFT JOIN security.RuntimeCredentialState cs ON cs.RuntimeIdentityId=a.RuntimeIdentityId
                LEFT JOIN security.RuntimeCredentialGeneration g ON g.RuntimeIdentityId=a.RuntimeIdentityId AND g.CredentialGeneration=a.CredentialGeneration
                LEFT JOIN security.RuntimeCertificateBinding cert ON cert.RuntimeIdentityId=a.RuntimeIdentityId AND cert.CredentialGeneration=a.CredentialGeneration AND cert.CertificateHash=a.CertificateHash
                LEFT JOIN security.RuntimeCredentialPublicKey k ON k.RuntimeIdentityId=g.RuntimeIdentityId AND k.CredentialGeneration=g.CredentialGeneration AND k.PublicKeyHash=g.CredentialBindingHash
                LEFT JOIN security.Principal rp ON rp.PrincipalId=a.RuntimeIdentityId AND rp.Kind='R';
                IF (SELECT SUM(CONVERT(bigint,DATALENGTH(p.CanonicalBytes))) FROM configuration.ConfigurationPackageRevision p
                    JOIN (SELECT DISTINCT PackageRevisionId FROM #cut WHERE PackageRevisionId IS NOT NULL) selected ON selected.PackageRevisionId=p.PackageRevisionId)>8388608
                    THROW 51042,'Complete roster package budget exceeded.',1;
                SELECT MachineId,AcknowledgementId,PackageRevisionId,RuntimeIdentityId,ActivationGeneration,OperationalGeneration,Reason,TimezoneRevisionId,TimezoneHash,ScheduleRevisionId,ScheduleHash,BreakPolicyRevisionId,BreakPolicyHash,ProductionLineId,PackageFrom,PackageUntil FROM #cut ORDER BY MachineId;
                SELECT p.PackageRevisionId,p.ContentHash,p.CanonicalBytes FROM configuration.ConfigurationPackageRevision p
                    JOIN (SELECT DISTINCT PackageRevisionId FROM #cut WHERE PackageRevisionId IS NOT NULL) selected ON selected.PackageRevisionId=p.PackageRevisionId;
                """);
            q.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=siteId;q.Parameters.Add("@now",SqlDbType.DateTimeOffset).Value=state.Now;
            var pins=new Dictionary<Guid,(Guid Timezone,byte[] TimezoneHash,Guid Schedule,byte[] ScheduleHash,Guid Break,byte[] BreakHash,Guid Line,DateTimeOffset From,DateTimeOffset? Until)>();
            var items=new List<TrustedRosterItem>();var packages=new Dictionary<Guid,JsonDocument>();
            try
            {
                await using(var r=await q.ExecuteReaderAsync(ct))
                {
                    while(await r.ReadAsync(ct))
                    {
                        var reason=r.GetString(6);var status=reason is "MissingOperationalHead" or "MissingAcknowledgementHead" or "IncompleteAuthority"?RosterStatus.Unavailable:reason=="EligibleConfigurationNotConnectivity"?RosterStatus.Eligible:RosterStatus.Excluded;
                        if(status==RosterStatus.Eligible)pins.Add(r.GetGuid(0),(r.GetGuid(7),(byte[])r[8],r.GetGuid(9),(byte[])r[10],r.GetGuid(11),(byte[])r[12],r.GetGuid(13),r.GetDateTimeOffset(14),r.IsDBNull(15)?null:r.GetDateTimeOffset(15)));
                        items.Add(new(r.GetGuid(0),status,reason,r.IsDBNull(2)?null:r.GetGuid(2),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(3)?null:r.GetGuid(3),r.IsDBNull(4)?null:r.GetInt64(4),r.IsDBNull(5)?null:r.GetInt64(5),[]));
                    }
                    await r.NextResultAsync(ct);
                    while(await r.ReadAsync(ct))
                    {
                        var id=r.GetGuid(0);var bytes=(byte[])r[2];if(bytes.Length>1048576||!CryptographicOperations.FixedTimeEquals((byte[])r[1],SHA256.HashData(bytes)))throw new SecurityDeniedException();
                        var doc=JsonDocument.Parse(bytes);packages.Add(id,doc);
                        if(doc.RootElement.GetProperty("revisionId").GetGuid()!=id||doc.RootElement.GetProperty("siteId").GetGuid()!=siteId)throw new SecurityDeniedException();
                    }
                }
                for(var i=0;i<items.Count;i++)
                {
                    var item=items[i];if(item.Status!=RosterStatus.Eligible)continue;
                    if(item.PackageRevisionId is not {} id||!packages.TryGetValue(id,out var doc))throw new SecurityDeniedException();
                    var matches=doc.RootElement.GetProperty("machines").EnumerateArray().Where(m=>m.GetProperty("machineId").GetGuid()==item.MachineId).ToArray();
                    if(matches.Length!=1)throw new SecurityDeniedException();
                    var machine=matches[0];var pin=pins[item.MachineId];
                    static bool Matches(JsonElement policy,Guid revision,byte[] hash)=>policy.GetProperty("revisionId").GetGuid()==revision&&CryptographicOperations.FixedTimeEquals(Convert.FromHexString(policy.GetProperty("contentHash").GetString()!),hash);
                    if(!Matches(doc.RootElement.GetProperty("timezone"),pin.Timezone,pin.TimezoneHash)||!Matches(machine.GetProperty("schedule"),pin.Schedule,pin.ScheduleHash)||!Matches(machine.GetProperty("breakPolicy"),pin.Break,pin.BreakHash)||
                        machine.GetProperty("productionLineId").GetGuid()!=pin.Line||machine.GetProperty("effectiveFrom").GetDateTimeOffset()!=pin.From||OptionalInstant(machine,"effectiveTo")!=pin.Until)throw new SecurityDeniedException();
                    // Intent is descriptive configuration only, never a claim that metric inputs or connectivity exist.
                    items[i]=item with{ConfiguredCapabilityIntent=Array.AsReadOnly(matches[0].GetProperty("capabilityIntent").EnumerateArray().Select(x=>x.GetString()??throw new SecurityDeniedException()).ToArray())};
                }
                Activity.Current?.SetTag("security.roster.rows",items.Count);
                var result=items.Any(i=>i.Status==RosterStatus.Unavailable)?"unavailable":items.Any(i=>i.Status==RosterStatus.Eligible)?"eligible-configurations":"healthy-empty";
                return new TrustedSiteRoster(siteId,state.Now,state.Epoch,result,items.AsReadOnly());
            }
            finally{foreach(var doc in packages.Values)doc.Dispose();}
        },ct);
    }
}
