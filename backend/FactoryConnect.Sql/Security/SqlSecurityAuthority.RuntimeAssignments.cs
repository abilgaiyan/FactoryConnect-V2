using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;

namespace FactoryConnect.Sql.Security;

public sealed record RuntimeAssignmentRequest(Guid OperationId,Guid AssignmentId,Guid SiteId,Guid MachineId,
    Guid RuntimeIdentityId,long ExpectedCredentialGeneration,DateTimeOffset EffectiveFrom,DateTimeOffset? EffectiveTo,
    long ExpectedAssignmentGeneration,Guid? SupersedesAssignmentId,long ExpectedSecurityEpoch,string Reason);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Records one machine's runtime assignment. Does not activate a package or authenticate a runtime.</summary>
    public Task<byte[]> AssignRuntimeAsync(byte[] sessionHash,RuntimeAssignmentRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.AssignmentId,request.ExpectedSecurityEpoch,request.Reason);
        var scope=AuthorizationScope.Machine(request.SiteId,request.MachineId);
        if(request.RuntimeIdentityId==Guid.Empty||request.ExpectedCredentialGeneration<=0||request.ExpectedAssignmentGeneration<0||
            request.ExpectedAssignmentGeneration==long.MaxValue||request.SupersedesAssignmentId==Guid.Empty||request.SupersedesAssignmentId==request.AssignmentId||
            request.EffectiveTo is {} end&&end<=request.EffectiveFrom)throw new ArgumentException("Exact assignment identities, generations and interval required.");
        var token=sessionHash.ToArray();var from=request.EffectiveFrom.ToUniversalTime();var until=request.EffectiveTo?.ToUniversalTime();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="assign-runtime",request.AssignmentId,request.SiteId,request.MachineId,request.RuntimeIdentityId,
            expectedCredentialGeneration=Decimal(request.ExpectedCredentialGeneration),effectiveFrom=from,effectiveTo=until,
            expectedAssignmentGeneration=Decimal(request.ExpectedAssignmentGeneration),request.SupersedesAssignmentId,expectedSecurityEpoch=Decimal(request.ExpectedSecurityEpoch),request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.assignment.command","mutation");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);
            await Authorize(c,t,actor,"runtime.assign",scope,initial.Now,ct);
            var completed=await FindOperation(c,t,request.OperationId,ct);
            if(completed is not null)
            {if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            if(initial.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            // No policy decision is made here. All machine transitions share this exact exclusive resource.
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,request.MachineId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"runtime.assign",scope,final.Now,ct);
            if(final.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            if(until is {} expired&&expired<=final.Now)throw new SecurityDeniedException();
            await using(var q=Command(c,t,"""
                SELECT g.ValidFrom,g.ExpiresAt FROM security.RuntimeIdentity i
                JOIN security.Principal p ON p.PrincipalId=i.RuntimeIdentityId AND p.Kind='R'
                JOIN security.RuntimeCredentialState s ON s.RuntimeIdentityId=i.RuntimeIdentityId
                JOIN security.RuntimeCredentialGeneration g ON g.RuntimeIdentityId=s.RuntimeIdentityId AND g.CredentialGeneration=s.CredentialGeneration
                JOIN security.RuntimeCredentialPublicKey k ON k.RuntimeIdentityId=g.RuntimeIdentityId AND k.CredentialGeneration=g.CredentialGeneration AND k.PublicKeyHash=g.CredentialBindingHash
                JOIN [master].Machine m ON m.MachineId=@machine AND m.SiteId=@site
                JOIN [master].Site site ON site.SiteId=m.SiteId
                WHERE i.RuntimeIdentityId=@runtime AND s.CredentialGeneration=@credential AND m.IsRetired=0 AND site.IsRetired=0
                AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation WHERE PrincipalId=i.RuntimeIdentityId)
                AND NOT EXISTS(SELECT 1 FROM security.RuntimeCredentialRevocation WHERE RuntimeIdentityId=i.RuntimeIdentityId AND CredentialGeneration=g.CredentialGeneration);
                """))
            {
                Add(q,("@machine",request.MachineId),("@site",request.SiteId),("@runtime",request.RuntimeIdentityId),("@credential",request.ExpectedCredentialGeneration));
                await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)||r.GetDateTimeOffset(0)>final.Now||r.GetDateTimeOffset(1)<=final.Now||r.GetDateTimeOffset(1)<=(from>final.Now?from:final.Now))throw new SecurityDeniedException();
            }
            Guid? prior=null;long generation=0;
            await using(var q=Command(c,t,"SELECT AssignmentId,AssignmentGeneration FROM configuration.RuntimeAssignmentState WHERE SiteId=@site AND MachineId=@machine"))
            {Add(q,("@site",request.SiteId),("@machine",request.MachineId));await using var r=await q.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){prior=r.GetGuid(0);generation=r.GetInt64(1);}}
            if(prior!=request.SupersedesAssignmentId||generation!=request.ExpectedAssignmentGeneration)throw new SecurityConflictException();
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.RuntimeAssignment WHERE AssignmentId=@assignment"))
            {Add(q,("@assignment",request.AssignmentId));if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();}
            var next=generation+1;
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,assignmentId=request.AssignmentId,assignmentGeneration=Decimal(next),status="assigned-not-runtime-acknowledgement"});
            await MachineAuthorityOperation(c,t,request.OperationId,actor,scope,bytes,result,"assign-runtime",request.Reason,ct);
            await Exec(c,t,"""
                INSERT configuration.RuntimeAssignment(AssignmentId,SiteId,MachineId,RuntimeIdentityId,AssignmentGeneration,EffectiveFrom,EffectiveTo,OperationId)
                VALUES(@assignment,@site,@machine,@runtime,@generation,@from,@until,@op);
                INSERT configuration.RuntimeAssignmentIssuance(AssignmentId,SiteId,MachineId,SupersedesAssignmentId,ActorPrincipalId,OperationId)
                VALUES(@assignment,@site,@machine,@prior,@actor,@op);
                IF @prior IS NOT NULL AND NOT EXISTS(SELECT 1 FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=@prior)
                    INSERT configuration.RuntimeAssignmentRevocation(AssignmentId,OperationId) VALUES(@prior,@op);
                UPDATE configuration.RuntimeAssignmentState SET AssignmentId=@assignment,AssignmentGeneration=@generation,RuntimeIdentityId=@runtime WHERE SiteId=@site AND MachineId=@machine;
                IF @@ROWCOUNT=0 INSERT configuration.RuntimeAssignmentState(MachineId,SiteId,AssignmentId,AssignmentGeneration,RuntimeIdentityId) VALUES(@machine,@site,@assignment,@generation,@runtime);
                """,ct,("@assignment",request.AssignmentId),("@site",request.SiteId),("@machine",request.MachineId),("@runtime",request.RuntimeIdentityId),("@generation",next),
                ("@from",from),("@until",(object?)until??DBNull.Value),("@op",request.OperationId),("@prior",(object?)prior??DBNull.Value),("@actor",actor));
            return result;
        },ct);
    }

    public Task<byte[]> RevokeRuntimeAssignmentAsync(byte[] sessionHash,Guid operationId,Guid assignmentId,Guid siteId,Guid machineId,
        long expectedAssignmentGeneration,long expectedSecurityEpoch,string reason,CancellationToken ct=default)
    {
        ValidateIdentityChange(operationId,assignmentId,expectedSecurityEpoch,reason);var scope=AuthorizationScope.Machine(siteId,machineId);
        if(expectedAssignmentGeneration<=0)throw new ArgumentException("Exact assignment generation required.");var token=sessionHash.ToArray();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="revoke-runtime-assignment",assignmentId,siteId,machineId,
            expectedAssignmentGeneration=Decimal(expectedAssignmentGeneration),expectedSecurityEpoch=Decimal(expectedSecurityEpoch),reason});
        return Transaction(operationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.assignment.command","mutation");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);
            await Authorize(c,t,actor,"runtime.assign",scope,initial.Now,ct);
            var completed=await FindOperation(c,t,operationId,ct);
            if(completed is not null){if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            if(initial.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"runtime.assign",scope,final.Now,ct);
            if(final.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            await using(var q=Command(c,t,"""
                SELECT 1 FROM configuration.RuntimeAssignment WHERE AssignmentId=@assignment AND SiteId=@site AND MachineId=@machine AND AssignmentGeneration=@generation
                AND NOT EXISTS(SELECT 1 FROM configuration.RuntimeAssignmentRevocation WHERE AssignmentId=@assignment)
                """))
            {Add(q,("@assignment",assignmentId),("@site",siteId),("@machine",machineId),("@generation",expectedAssignmentGeneration));if(await q.ExecuteScalarAsync(ct) is null)throw new SecurityDeniedException();}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,assignmentId,status="assignment-revoked-not-runtime-stop"});
            await MachineAuthorityOperation(c,t,operationId,actor,scope,bytes,result,"revoke-runtime-assignment",reason,ct);
            await Exec(c,t,"INSERT configuration.RuntimeAssignmentRevocation(AssignmentId,OperationId) VALUES(@assignment,@op)",ct,("@assignment",assignmentId),("@op",operationId));return result;
        },ct);
    }

    public Task<byte[]> ReadRuntimeAssignmentOperationAsync(byte[] sessionHash,Guid operationId,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Exact operation required.");var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            Activity.Current?.SetTag("security.assignment.command","receipt");
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);var completed=await FindOperation(c,t,operationId,ct)??throw new SecurityDeniedException();
            if(completed.Actor!=actor)throw new SecurityDeniedException();using var parsed=JsonDocument.Parse(completed.Request);
            if(!parsed.RootElement.TryGetProperty("action",out var action)||action.GetString() is not ("assign-runtime" or "revoke-runtime-assignment"))throw new SecurityDeniedException();
            await Authorize(c,t,actor,"runtime.assign",completed.Scope,state.Now,ct);return completed.Result;
        },ct);
    }
}
