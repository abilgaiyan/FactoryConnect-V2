using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FactoryConnect.Sql.Security;

public sealed record CommissioningActivationPin(Guid ActivationId,long ActivationGeneration,Guid PackageRevisionId,string ContentHash,Guid ApprovalReviewId,
    Guid RuntimeIdentityId,Guid AssignmentId,long AssignmentGeneration,long CredentialGeneration,long OperationalGeneration,DateTimeOffset EffectiveFrom,DateTimeOffset? EffectiveTo);
public sealed record CommissioningMachine(Guid MachineId,string Status,string Reason,Guid? CurrentAssignmentId,Guid? CurrentRuntimeIdentityId,
    long? CurrentAssignmentGeneration,long? CurrentCredentialGeneration,long CurrentOperationalGeneration,string OperationalStatus,CommissioningActivationPin? RequestedActivation,
    Guid? LastAcknowledgementId,bool AcknowledgesRequestedActivation,IReadOnlyList<string> ConfiguredCapabilityIntent);
public sealed record CommissioningSiteDiscovery(Guid SiteId,DateTimeOffset EvaluatedAt,long SecurityEpoch,string Status,IReadOnlyList<CommissioningMachine> Machines);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Human commissioning discovery, independent of active roster eligibility. No runtime authentication or activation capability.</summary>
    public Task<CommissioningSiteDiscovery> ReadCommissioningSiteAsync(byte[] sessionHash,Guid siteId,CancellationToken ct=default)
    {
        var scope=AuthorizationScope.Site(siteId);var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.commissioning.read",true);
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);await Authorize(c,t,actor,"commissioning.read",scope,state.Now,ct);
            await using var q=Command(c,t,"""
                IF NOT EXISTS(SELECT 1 FROM [master].Site WITH(HOLDLOCK) WHERE SiteId=@site) THROW 51042,'Unknown commissioning discovery site.',1;
                SELECT TOP(101) m.MachineId INTO #machines FROM [master].Machine m WITH(HOLDLOCK) WHERE m.SiteId=@site ORDER BY m.MachineId;
                IF (SELECT COUNT(*) FROM #machines)>100 THROW 51042,'Site exceeds complete commissioning discovery bound.',1;
                SELECT m.MachineId,ass.AssignmentId AS CurrentAssignmentId,ass.RuntimeIdentityId AS CurrentRuntimeId,ass.AssignmentGeneration AS CurrentAssignmentGeneration,
                    cs.CredentialGeneration AS CurrentCredentialGeneration,COALESCE(o.Generation,0) AS CurrentOperationalGeneration,COALESCE(o.Status,'unknown') AS OperationalStatus,
                    ar.ActivationId,ar.ActivationGeneration,ar.PackageRevisionId,ar.ContentHash,ar.ReviewId,ar.RuntimeIdentityId,ar.AssignmentId,ar.AssignmentGeneration,ar.CredentialGeneration,ar.OperationalGeneration,ar.EffectiveFrom,ar.EffectiveTo,
                    h.AcknowledgementId,CONVERT(bit,CASE WHEN a.ActivationId=ar.ActivationId AND a.ActivationGeneration=ar.ActivationGeneration THEN 1 ELSE 0 END) AS ExactRequestedAcknowledgement,
                    p.TimezoneRevisionId,p.TimezoneHash,pa.ScheduleRevisionId,pa.ScheduleHash,pa.BreakPolicyRevisionId,pa.BreakPolicyHash,pa.ProductionLineId,pa.EffectiveFrom AS PackageFrom,pa.EffectiveTo AS PackageUntil,
                    CASE
                    WHEN os.StateId IS NULL AND EXISTS(SELECT 1 FROM configuration.MachineOperationalTransition history WHERE history.SiteId=@site AND history.MachineId=m.MachineId) THEN 'MissingOperationalHead'
                    WHEN ah.ActivationId IS NULL AND EXISTS(SELECT 1 FROM configuration.ActivationRequest history WHERE history.SiteId=@site AND history.MachineId=m.MachineId) THEN 'MissingActivationHead'
                    WHEN h.AcknowledgementId IS NULL AND EXISTS(SELECT 1 FROM configuration.RuntimeActivationAcknowledgement history WHERE history.SiteId=@site AND history.MachineId=m.MachineId) THEN 'MissingAcknowledgementHead'
                    WHEN ass.AssignmentId IS NULL AND EXISTS(SELECT 1 FROM configuration.RuntimeAssignment history WHERE history.SiteId=@site AND history.MachineId=m.MachineId) THEN 'MissingAssignmentHead'
                    WHEN ar.ActivationId IS NOT NULL AND (p.PackageRevisionId IS NULL OR p.SubmittedAt IS NULL OR v.ReviewId IS NULL OR pa.MachineId IS NULL OR line.ProductionLineId IS NULL) THEN 'IncompleteAuthority'
                    WHEN ass.AssignmentId IS NOT NULL AND (cs.RuntimeIdentityId IS NULL OR g.RuntimeIdentityId IS NULL OR rp.PrincipalId IS NULL OR k.RuntimeIdentityId IS NULL) THEN 'IncompleteAuthority'
                    WHEN m.IsRetired=1 OR site.IsRetired=1 OR line.IsRetired=1 THEN 'Retired'
                    WHEN ass.AssignmentId IS NULL THEN 'NoRuntimeAssignment'
                    WHEN EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=ass.RuntimeIdentityId)
                        OR EXISTS(SELECT 1 FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=ass.RuntimeIdentityId AND CredentialGeneration=cs.CredentialGeneration)
                        OR EXISTS(SELECT 1 FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=ass.AssignmentId) THEN 'RuntimeAuthorityRevoked'
                    WHEN cert.RuntimeIdentityId IS NULL THEN 'RuntimeCertificateNotRegistered'
                    WHEN assignment.EffectiveFrom>@now OR (assignment.EffectiveTo IS NOT NULL AND assignment.EffectiveTo<=@now)
                        OR g.ValidFrom>@now OR g.ExpiresAt<=@now OR cert.ValidFrom>@now OR cert.ExpiresAt<=@now
                        OR (ar.ActivationId IS NOT NULL AND (g.ExpiresAt<=CASE WHEN ar.EffectiveFrom>@now THEN ar.EffectiveFrom ELSE @now END
                            OR cert.ExpiresAt<=CASE WHEN ar.EffectiveFrom>@now THEN ar.EffectiveFrom ELSE @now END
                            OR (assignment.EffectiveTo IS NOT NULL AND assignment.EffectiveTo<=CASE WHEN ar.EffectiveFrom>@now THEN ar.EffectiveFrom ELSE @now END))) THEN 'RuntimeOutsideEffectiveInterval'
                    WHEN ar.ActivationId IS NULL THEN 'NoActivationRequest'
                    WHEN EXISTS(SELECT 1 FROM configuration.ActivationRequestRevocation WHERE ActivationId=ar.ActivationId)
                        OR EXISTS(SELECT 1 FROM configuration.PackageApprovalRevocation WHERE ReviewId=ar.ReviewId) THEN 'RequestOrApprovalRevoked'
                    WHEN ass.AssignmentId<>ar.AssignmentId OR ass.AssignmentGeneration<>ar.AssignmentGeneration OR ass.RuntimeIdentityId<>ar.RuntimeIdentityId OR cs.CredentialGeneration<>ar.CredentialGeneration THEN 'RuntimeGenerationChanged'
                    WHEN EXISTS(SELECT 1 FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind='timezone' AND TimezoneRevisionId=p.TimezoneRevisionId AND ContentHash=p.TimezoneHash AND EffectiveFrom<=CASE WHEN ar.EffectiveFrom>@now THEN ar.EffectiveFrom ELSE @now END)
                        OR EXISTS(SELECT 1 FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind='schedule' AND ScheduleRevisionId=pa.ScheduleRevisionId AND ContentHash=pa.ScheduleHash AND EffectiveFrom<=CASE WHEN ar.EffectiveFrom>@now THEN ar.EffectiveFrom ELSE @now END)
                        OR EXISTS(SELECT 1 FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind='break' AND BreakPolicyRevisionId=pa.BreakPolicyRevisionId AND ContentHash=pa.BreakPolicyHash AND EffectiveFrom<=CASE WHEN ar.EffectiveFrom>@now THEN ar.EffectiveFrom ELSE @now END) THEN 'PolicyRevokedAtFirstUse'
                    WHEN o.StateId IS NULL OR o.Status<>'enabled' THEN 'MachineNotEnabled'
                    WHEN o.Generation<>ar.OperationalGeneration THEN 'OperatingGenerationChanged'
                    WHEN (ar.EffectiveTo IS NOT NULL AND ar.EffectiveTo<=@now) OR (pa.EffectiveTo IS NOT NULL AND pa.EffectiveTo<=@now) THEN 'RequestExpired'
                    WHEN ar.EffectiveFrom>@now THEN 'ScheduledAwaitingAcknowledgement'
                    WHEN a.ActivationId=ar.ActivationId AND a.ActivationGeneration=ar.ActivationGeneration THEN 'RequestedActivationAcknowledgedNotConnectivity'
                    ELSE 'AwaitingRuntimeAcknowledgement' END AS Reason
                INTO #cut
                FROM #machines selected JOIN [master].Machine m WITH(HOLDLOCK) ON m.MachineId=selected.MachineId
                JOIN [master].Site site WITH(HOLDLOCK) ON site.SiteId=m.SiteId
                LEFT JOIN configuration.MachineOperationalState os ON os.MachineId=m.MachineId AND os.SiteId=@site
                LEFT JOIN configuration.MachineOperationalTransition o ON o.StateId=os.StateId AND o.Generation=os.Generation
                LEFT JOIN configuration.MachineAcknowledgementHead h ON h.MachineId=m.MachineId AND h.SiteId=@site
                LEFT JOIN configuration.RuntimeActivationAcknowledgement a ON a.AcknowledgementId=h.AcknowledgementId AND a.ActivationGeneration=h.ActivationGeneration
                LEFT JOIN configuration.MachineActivationRequestHead ah ON ah.SiteId=@site AND ah.MachineId=m.MachineId
                LEFT JOIN configuration.ActivationRequest ar ON ar.ActivationId=ah.ActivationId AND ar.ActivationGeneration=ah.ActivationGeneration
                LEFT JOIN configuration.ConfigurationPackageRevision p ON p.PackageRevisionId=ar.PackageRevisionId AND p.SiteId=@site AND p.ContentHash=ar.ContentHash
                LEFT JOIN configuration.PackageReview v ON v.ReviewId=ar.ReviewId AND v.PackageRevisionId=p.PackageRevisionId AND v.ContentHash=p.ContentHash AND v.Decision='approved'
                LEFT JOIN configuration.PackageMachineAssignment pa ON pa.PackageRevisionId=p.PackageRevisionId AND pa.SiteId=@site AND pa.MachineId=m.MachineId
                LEFT JOIN [master].ProductionLine line WITH(HOLDLOCK) ON line.ProductionLineId=pa.ProductionLineId AND line.SiteId=@site
                LEFT JOIN configuration.RuntimeAssignmentState ass ON ass.SiteId=@site AND ass.MachineId=m.MachineId
                LEFT JOIN configuration.RuntimeAssignment assignment ON assignment.AssignmentId=ass.AssignmentId
                LEFT JOIN security.RuntimeCredentialState cs ON cs.RuntimeIdentityId=ass.RuntimeIdentityId
                LEFT JOIN security.RuntimeCredentialGeneration g ON g.RuntimeIdentityId=cs.RuntimeIdentityId AND g.CredentialGeneration=cs.CredentialGeneration
                LEFT JOIN security.RuntimeCertificateBinding cert ON cert.RuntimeIdentityId=g.RuntimeIdentityId AND cert.CredentialGeneration=g.CredentialGeneration
                LEFT JOIN security.RuntimeCredentialPublicKey k ON k.RuntimeIdentityId=g.RuntimeIdentityId AND k.CredentialGeneration=g.CredentialGeneration AND k.PublicKeyHash=g.CredentialBindingHash
                LEFT JOIN security.Principal rp ON rp.PrincipalId=ass.RuntimeIdentityId AND rp.Kind='R';
                IF (SELECT SUM(CONVERT(bigint,DATALENGTH(p.CanonicalBytes))) FROM configuration.ConfigurationPackageRevision p
                    JOIN (SELECT DISTINCT PackageRevisionId FROM #cut WHERE PackageRevisionId IS NOT NULL) selected ON selected.PackageRevisionId=p.PackageRevisionId)>8388608
                    THROW 51042,'Complete commissioning discovery package budget exceeded.',1;
                SELECT MachineId,CurrentAssignmentId,CurrentRuntimeId,CurrentAssignmentGeneration,CurrentCredentialGeneration,CurrentOperationalGeneration,OperationalStatus,
                    ActivationId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration,OperationalGeneration,EffectiveFrom,EffectiveTo,
                    AcknowledgementId,ExactRequestedAcknowledgement,TimezoneRevisionId,TimezoneHash,ScheduleRevisionId,ScheduleHash,BreakPolicyRevisionId,BreakPolicyHash,ProductionLineId,PackageFrom,PackageUntil,Reason
                    FROM #cut ORDER BY MachineId;
                SELECT p.PackageRevisionId,p.ContentHash,p.CanonicalBytes FROM configuration.ConfigurationPackageRevision p
                    JOIN (SELECT DISTINCT PackageRevisionId FROM #cut WHERE PackageRevisionId IS NOT NULL) selected ON selected.PackageRevisionId=p.PackageRevisionId;
                """);
            q.Parameters.Add("@site",SqlDbType.UniqueIdentifier).Value=siteId;q.Parameters.Add("@now",SqlDbType.DateTimeOffset).Value=state.Now;
            var pins=new Dictionary<Guid,(Guid Timezone,byte[] TimezoneHash,Guid Schedule,byte[] ScheduleHash,Guid Break,byte[] BreakHash,Guid Line,DateTimeOffset From,DateTimeOffset? Until)>();
            var items=new List<CommissioningMachine>();var packages=new Dictionary<Guid,JsonDocument>();
            try
            {
                await using(var r=await q.ExecuteReaderAsync(ct))
                {
                    while(await r.ReadAsync(ct))
                    {
                        var reason=r.GetString(30);var status=reason is "MissingOperationalHead" or "MissingAcknowledgementHead" or "MissingActivationHead" or "MissingAssignmentHead" or "IncompleteAuthority"?"unavailable":reason switch
                        {"NoRuntimeAssignment" or "NoActivationRequest"=>"not-requested","ScheduledAwaitingAcknowledgement"=>"scheduled","AwaitingRuntimeAcknowledgement"=>"pending","RequestedActivationAcknowledgedNotConnectivity"=>"acknowledged",_=>"blocked"};
                        CommissioningActivationPin? activation=r.IsDBNull(7)?null:new(r.GetGuid(7),r.GetInt64(8),r.GetGuid(9),Convert.ToHexString((byte[])r[10]).ToLowerInvariant(),r.GetGuid(11),r.GetGuid(12),r.GetGuid(13),r.GetInt64(14),r.GetInt64(15),r.GetInt64(16),r.GetDateTimeOffset(17),r.IsDBNull(18)?null:r.GetDateTimeOffset(18));
                        if(activation is not null&&status!="unavailable")pins.Add(r.GetGuid(0),(r.GetGuid(21),(byte[])r[22],r.GetGuid(23),(byte[])r[24],r.GetGuid(25),(byte[])r[26],r.GetGuid(27),r.GetDateTimeOffset(28),r.IsDBNull(29)?null:r.GetDateTimeOffset(29)));
                        items.Add(new(r.GetGuid(0),status,reason,r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.IsDBNull(3)?null:r.GetInt64(3),r.IsDBNull(4)?null:r.GetInt64(4),r.GetInt64(5),r.GetString(6),activation,r.IsDBNull(19)?null:r.GetGuid(19),r.GetBoolean(20),[]));
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
                    ct.ThrowIfCancellationRequested();var item=items[i];if(item.RequestedActivation is not {} activation||item.Status=="unavailable")continue;
                    if(!packages.TryGetValue(activation.PackageRevisionId,out var doc))throw new SecurityDeniedException();
                    var matches=doc.RootElement.GetProperty("machines").EnumerateArray().Where(m=>m.GetProperty("machineId").GetGuid()==item.MachineId).ToArray();if(matches.Length!=1)throw new SecurityDeniedException();
                    var machine=matches[0];var pin=pins[item.MachineId];
                    static bool Matches(JsonElement policy,Guid revision,byte[] hash)=>policy.GetProperty("revisionId").GetGuid()==revision&&CryptographicOperations.FixedTimeEquals(Convert.FromHexString(policy.GetProperty("contentHash").GetString()!),hash);
                    if(!Matches(doc.RootElement.GetProperty("timezone"),pin.Timezone,pin.TimezoneHash)||!Matches(machine.GetProperty("schedule"),pin.Schedule,pin.ScheduleHash)||!Matches(machine.GetProperty("breakPolicy"),pin.Break,pin.BreakHash)||
                        machine.GetProperty("productionLineId").GetGuid()!=pin.Line||machine.GetProperty("effectiveFrom").GetDateTimeOffset()!=pin.From||OptionalInstant(machine,"effectiveTo")!=pin.Until)throw new SecurityDeniedException();
                    items[i]=item with{ConfiguredCapabilityIntent=Array.AsReadOnly(machine.GetProperty("capabilityIntent").EnumerateArray().Select(x=>x.GetString()??throw new SecurityDeniedException()).ToArray())};
                }
                Activity.Current?.SetTag("security.commissioning.rows",items.Count);
                return new CommissioningSiteDiscovery(siteId,state.Now,state.Epoch,items.Any(i=>i.Status=="unavailable")?"unavailable":"complete",items.AsReadOnly());
            }
            finally{foreach(var doc in packages.Values)doc.Dispose();}
        },ct);
    }
}
