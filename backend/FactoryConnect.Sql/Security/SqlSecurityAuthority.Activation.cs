using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed record ActivationRequestCommand(Guid OperationId,Guid ActivationId,Guid SiteId,Guid MachineId,
    Guid PackageRevisionId,byte[] ContentHash,Guid ApprovalReviewId,Guid RuntimeIdentityId,Guid AssignmentId,
    long AssignmentGeneration,long CredentialGeneration,DateTimeOffset EffectiveFrom,DateTimeOffset? EffectiveTo,
    long ExpectedActivationGeneration,Guid? SupersedesActivationId,long ExpectedSecurityEpoch,string Reason);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Records one machine's pending activation intent. Never acknowledges application or publishes an active roster.</summary>
    public Task<byte[]> RequestActivationAsync(byte[] sessionHash,ActivationRequestCommand command,CancellationToken ct=default)
    {
        ValidateIdentityChange(command.OperationId,command.ActivationId,command.ExpectedSecurityEpoch,command.Reason);
        if(new[]{command.SiteId,command.MachineId,command.PackageRevisionId,command.ApprovalReviewId,command.RuntimeIdentityId,command.AssignmentId}.Any(id=>id==Guid.Empty)||
            command.ContentHash.Length!=32||command.AssignmentGeneration<=0||command.CredentialGeneration<=0||command.ExpectedActivationGeneration<0||
            command.ExpectedActivationGeneration==long.MaxValue||command.SupersedesActivationId==Guid.Empty||command.SupersedesActivationId==command.ActivationId||
            command.EffectiveTo is {} end&&end<=command.EffectiveFrom)throw new ArgumentException("Exact bounded activation identity, generations and interval required.");
        var hash=command.ContentHash.ToArray();var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(command.SiteId,command.MachineId);
        var from=command.EffectiveFrom.ToUniversalTime();var until=command.EffectiveTo?.ToUniversalTime();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="request-activation",command.ActivationId,command.SiteId,command.MachineId,
            command.PackageRevisionId,contentHash=Convert.ToHexString(hash),command.ApprovalReviewId,command.RuntimeIdentityId,command.AssignmentId,
            assignmentGeneration=Decimal(command.AssignmentGeneration),credentialGeneration=Decimal(command.CredentialGeneration),effectiveFrom=from,effectiveTo=until,
            expectedActivationGeneration=Decimal(command.ExpectedActivationGeneration),command.SupersedesActivationId,expectedSecurityEpoch=Decimal(command.ExpectedSecurityEpoch),command.Reason});
        return Transaction(command.OperationId,true,async(c,t)=>
        {
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);
            await Authorize(c,t,actor,"configuration.activate",scope,initial.Now,ct);
            var completed=await FindOperation(c,t,command.OperationId,ct);
            if(completed is not null)
            {if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            if(initial.Epoch!=command.ExpectedSecurityEpoch)throw new SecurityConflictException();
            byte[] canonical;
            await using(var q=Command(c,t,"""
                SELECT r.CanonicalBytes FROM configuration.ConfigurationPackageRevision r
                JOIN configuration.PackageReview v ON v.PackageRevisionId=r.PackageRevisionId AND v.ContentHash=r.ContentHash AND v.SiteId=r.SiteId
                WHERE r.SiteId=@site AND r.PackageRevisionId=@revision AND r.ContentHash=@hash AND r.SubmittedAt IS NOT NULL
                    AND v.ReviewId=@review AND v.Decision='approved';
                """))
            {
                Add(q,("@site",command.SiteId),("@revision",command.PackageRevisionId),("@hash",hash),("@review",command.ApprovalReviewId));
                canonical=(byte[]?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();
            }
            if(!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(canonical)))throw new SecurityDeniedException();
            using var parsed=JsonDocument.Parse(canonical);var root=parsed.RootElement;
            var machines=root.GetProperty("machines").EnumerateArray().Where(m=>m.GetProperty("machineId").GetGuid()==command.MachineId).ToArray();
            if(root.GetProperty("siteId").GetGuid()!=command.SiteId||root.GetProperty("revisionId").GetGuid()!=command.PackageRevisionId||machines.Length!=1)throw new SecurityDeniedException();
            var machine=machines[0];
            if(!Contains(machine.GetProperty("effectiveFrom").GetDateTimeOffset(),OptionalInstant(machine,"effectiveTo"),from,until))throw new SecurityDeniedException();
            await ConfigurationLocks(c,t,root,installationId,ct,command.MachineId);
            var final=await State(c,t,ct);var finalActor=await Session(c,t,token,final.Now,ct);
            if(actor!=finalActor)throw new SecurityDeniedException();
            await Authorize(c,t,actor,"configuration.activate",scope,final.Now,ct);
            if(final.Epoch!=command.ExpectedSecurityEpoch)throw new SecurityConflictException();
            if(until is {} expired&&expired<=final.Now)throw new SecurityDeniedException();
            await ValidateActivationPrerequisites(c,t,command,machine,from,until,final.Now,ct);
            await ValidatePinnedPolicies(c,t,root,machine,command.SiteId,from>final.Now?from:final.Now,ct);
            Guid? head=null;long generation=0;
            await using(var q=Command(c,t,"SELECT ActivationId,ActivationGeneration FROM configuration.MachineActivationRequestHead WHERE SiteId=@site AND MachineId=@machine"))
            {
                Add(q,("@site",command.SiteId),("@machine",command.MachineId));await using var r=await q.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct)){head=r.GetGuid(0);generation=r.GetInt64(1);}
            }
            if(generation!=command.ExpectedActivationGeneration||head!=command.SupersedesActivationId)throw new SecurityConflictException();
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.ActivationRequest WHERE ActivationId=@activation"))
            {Add(q,("@activation",command.ActivationId));if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();}
            var next=generation+1;
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=command.OperationId,activationId=command.ActivationId,activationGeneration=Decimal(next),status="pending-acknowledgement"});
            await MachineAuthorityOperation(c,t,command.OperationId,actor,scope,bytes,result,"request-activation",command.Reason,ct);
            await Exec(c,t,"""
                INSERT configuration.ActivationRequest(ActivationId,SiteId,MachineId,ActivationGeneration,PackageRevisionId,ContentHash,ReviewId,ApprovalDecision,
                    RuntimeIdentityId,AssignmentId,AssignmentGeneration,CredentialGeneration,EffectiveFrom,EffectiveTo,SupersedesActivationId,ActorPrincipalId,OperationId)
                VALUES(@activation,@site,@machine,@generation,@revision,@hash,@review,'approved',@runtime,@assignment,@assignmentGeneration,@credentialGeneration,@from,@until,@prior,@actor,@op);
                UPDATE configuration.MachineActivationRequestHead SET ActivationId=@activation,ActivationGeneration=@generation WHERE SiteId=@site AND MachineId=@machine;
                IF @@ROWCOUNT=0 INSERT configuration.MachineActivationRequestHead(SiteId,MachineId,ActivationId,ActivationGeneration) VALUES(@site,@machine,@activation,@generation);
                """,ct,("@activation",command.ActivationId),("@site",command.SiteId),("@machine",command.MachineId),("@generation",next),
                ("@revision",command.PackageRevisionId),("@hash",hash),("@review",command.ApprovalReviewId),("@runtime",command.RuntimeIdentityId),
                ("@assignment",command.AssignmentId),("@assignmentGeneration",command.AssignmentGeneration),("@credentialGeneration",command.CredentialGeneration),
                ("@from",from),("@until",(object?)until??DBNull.Value),("@prior",(object?)head??DBNull.Value),("@actor",actor),("@op",command.OperationId));
            return result;
        },ct);
    }

    public Task<byte[]> RevokeActivationRequestAsync(byte[] sessionHash,Guid operationId,Guid activationId,Guid siteId,Guid machineId,
        long expectedSecurityEpoch,string reason,CancellationToken ct=default)
    {
        ValidateIdentityChange(operationId,activationId,expectedSecurityEpoch,reason);var scope=AuthorizationScope.Machine(siteId,machineId);var token=sessionHash.ToArray();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="revoke-activation-request",activationId,siteId,machineId,expectedSecurityEpoch=Decimal(expectedSecurityEpoch),reason});
        return Transaction(operationId,true,async(c,t)=>
        {
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);
            await Authorize(c,t,actor,"configuration.activate",scope,initial.Now,ct);
            var completed=await FindOperation(c,t,operationId,ct);
            if(completed is not null){if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            if(initial.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.ActivationRequest WHERE ActivationId=@activation AND SiteId=@site AND MachineId=@machine"))
            {Add(q,("@activation",activationId),("@site",siteId),("@machine",machineId));if(await q.ExecuteScalarAsync(ct) is null)throw new SecurityDeniedException();}
            // Intent revocation needs no policy decision. The common machine resource serializes current-head transitions.
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"configuration.activate",scope,final.Now,ct);
            if(final.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,activationId,status="request-revoked-not-runtime-stop"});
            await MachineAuthorityOperation(c,t,operationId,actor,scope,bytes,result,"revoke-activation-request",reason,ct);
            await Exec(c,t,"IF NOT EXISTS(SELECT 1 FROM configuration.ActivationRequestRevocation WHERE ActivationId=@activation) INSERT configuration.ActivationRequestRevocation(ActivationId,ActorPrincipalId,OperationId) VALUES(@activation,@actor,@op)",ct,("@activation",activationId),("@actor",actor),("@op",operationId));
            return result;
        },ct);
    }

    public Task<byte[]> ReadActivationOperationAsync(byte[] sessionHash,Guid operationId,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Exact operation required.");var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);var completed=await FindOperation(c,t,operationId,ct)??throw new SecurityDeniedException();
            if(completed.Actor!=actor)throw new SecurityDeniedException();
            using var parsed=JsonDocument.Parse(completed.Request);
            if(!parsed.RootElement.TryGetProperty("action",out var action)||action.GetString() is not ("request-activation" or "revoke-activation-request"))throw new SecurityDeniedException();
            await Authorize(c,t,actor,"configuration.activate",completed.Scope,state.Now,ct);return completed.Result;
        },ct);
    }

    private static async Task ValidateActivationPrerequisites(SqlConnection c,SqlTransaction t,ActivationRequestCommand request,JsonElement machine,
        DateTimeOffset from,DateTimeOffset? until,DateTimeOffset now,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            SELECT a.EffectiveFrom,a.EffectiveTo,g.ValidFrom,g.ExpiresAt
            FROM configuration.RuntimeAssignmentState s JOIN configuration.RuntimeAssignment a ON a.AssignmentId=s.AssignmentId
            JOIN security.RuntimeIdentity i ON i.RuntimeIdentityId=a.RuntimeIdentityId
            JOIN security.Principal p ON p.PrincipalId=i.RuntimeIdentityId AND p.Kind='R'
            JOIN security.RuntimeCredentialState currentCredential ON currentCredential.RuntimeIdentityId=i.RuntimeIdentityId
            JOIN security.RuntimeCredentialGeneration g ON g.RuntimeIdentityId=i.RuntimeIdentityId AND g.CredentialGeneration=currentCredential.CredentialGeneration
            JOIN [master].Machine m ON m.MachineId=a.MachineId AND m.SiteId=a.SiteId
            JOIN [master].Site site ON site.SiteId=m.SiteId
            JOIN [master].ProductionLine line ON line.SiteId=site.SiteId AND line.ProductionLineId=@line
            WHERE s.SiteId=@site AND s.MachineId=@machine AND a.AssignmentId=@assignment AND a.AssignmentGeneration=@assignmentGeneration
                AND i.RuntimeIdentityId=@runtime AND currentCredential.CredentialGeneration=@credentialGeneration
                AND m.IsRetired=0 AND site.IsRetired=0 AND line.IsRetired=0
                AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=i.RuntimeIdentityId)
                AND NOT EXISTS(SELECT 1 FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=a.AssignmentId)
                AND NOT EXISTS(SELECT 1 FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=i.RuntimeIdentityId AND CredentialGeneration=g.CredentialGeneration)
                AND NOT EXISTS(SELECT 1 FROM configuration.PackageApprovalRevocation WHERE ReviewId=@review);
            """);
        Add(q,("@site",request.SiteId),("@machine",request.MachineId),("@assignment",request.AssignmentId),("@assignmentGeneration",request.AssignmentGeneration),
            ("@runtime",request.RuntimeIdentityId),("@credentialGeneration",request.CredentialGeneration),("@review",request.ApprovalReviewId),("@line",machine.GetProperty("productionLineId").GetGuid()));
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
        var assignmentFrom=r.GetDateTimeOffset(0);var assignmentTo=r.IsDBNull(1)?(DateTimeOffset?)null:r.GetDateTimeOffset(1);
        var credentialFrom=r.GetDateTimeOffset(2);var credentialTo=r.GetDateTimeOffset(3);var firstUse=from>now?from:now;
        if(assignmentFrom>now||assignmentTo is {} end&&end<=now||!Contains(assignmentFrom,assignmentTo,from,until)||
            credentialFrom>now||credentialTo<=now||credentialFrom>firstUse||credentialTo<=firstUse)throw new SecurityDeniedException();
    }
    private static async Task ValidatePinnedPolicies(SqlConnection c,SqlTransaction t,JsonElement root,JsonElement machine,Guid siteId,DateTimeOffset at,CancellationToken ct)
    {
            var timezone=root.GetProperty("timezone");var schedule=machine.GetProperty("schedule");var breaks=machine.GetProperty("breakPolicy");
            await using(var q=Command(c,t,"""
                SELECT COUNT(*) FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND EffectiveFrom<=@now AND
                    ((PolicyKind='timezone' AND TimezoneRevisionId=@timezone AND ContentHash=@timezoneHash) OR
                     (PolicyKind='schedule' AND ScheduleRevisionId=@schedule AND ContentHash=@scheduleHash) OR
                     (PolicyKind='break' AND BreakPolicyRevisionId=@break AND ContentHash=@breakHash));
                """))
            {
                Add(q,("@site",siteId),("@now",at),("@timezone",timezone.GetProperty("revisionId").GetGuid()),("@timezoneHash",PinHash(timezone)),
                    ("@schedule",schedule.GetProperty("revisionId").GetGuid()),("@scheduleHash",PinHash(schedule)),("@break",breaks.GetProperty("revisionId").GetGuid()),("@breakHash",PinHash(breaks)));
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0)throw new SecurityDeniedException();
            }
    }
    private static async Task MachineAuthorityOperation(SqlConnection c,SqlTransaction t,Guid op,Guid actor,AuthorizationScope scope,byte[] request,byte[] result,string action,string reason,CancellationToken ct)
    {
        await Exec(c,t,"""
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                VALUES(@op,@actor,@request,@hash,'M',@site,@machine,@result);
            INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@audit,@op,@actor,@action,@reason);
            UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
            """,ct,("@op",op),("@actor",actor),("@request",request),("@hash",SHA256.HashData(request)),("@site",scope.SiteId!.Value),("@machine",scope.MachineId!.Value),
            ("@result",result),("@audit",Guid.NewGuid()),("@action",action),("@reason",reason));
    }
    private static bool Contains(DateTimeOffset outerFrom,DateTimeOffset? outerTo,DateTimeOffset innerFrom,DateTimeOffset? innerTo)
        =>innerFrom>=outerFrom&&(outerTo is null||innerTo is {} end&&end<=outerTo.Value);
    private static DateTimeOffset? OptionalInstant(JsonElement element,string name)=>element.GetProperty(name).ValueKind==JsonValueKind.Null?null:element.GetProperty(name).GetDateTimeOffset();
    private static byte[] PinHash(JsonElement pin)=>Convert.FromHexString(pin.GetProperty("contentHash").GetString()!);
    private static string Decimal(long value)=>value.ToString(CultureInfo.InvariantCulture);
}
