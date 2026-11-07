using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed record MachineEnableProposalRequest(Guid OperationId,Guid ProposalId,Guid SiteId,Guid MachineId,Guid? ExpectedStateId,
    long ExpectedGeneration,long ExpectedSecurityEpoch,DateTimeOffset ExpiresAt,string Reason);
public sealed record MachineSuspensionRequest(Guid OperationId,Guid StateId,Guid SiteId,Guid MachineId,Guid? ExpectedStateId,
    long ExpectedGeneration,long ExpectedSecurityEpoch,string Reason);
public sealed record MachineEnableProposalSnapshot(Guid ProposalId,Guid SiteId,Guid MachineId,Guid ProposerId,Guid? ExpectedStateId,
    long ExpectedGeneration,DateTimeOffset ExpiresAt,string Reason,long CurrentGeneration);
public sealed record MachineOperationalSnapshot(Guid SiteId,Guid MachineId,string Status,long Generation,Guid? StateId,
    DateTimeOffset? ChangedAt,bool MachineRetired,bool SiteRetired);

public sealed partial class SqlSecurityAuthority
{
    public Task<byte[]> ProposeMachineEnableAsync(byte[] sessionHash,MachineEnableProposalRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.ProposalId,request.ExpectedSecurityEpoch,request.Reason);
        ValidateOperationalVersion(request.ExpectedStateId,request.ExpectedGeneration);
        var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(request.SiteId,request.MachineId);var expires=request.ExpiresAt.ToUniversalTime();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="machine-enable-propose",request.ProposalId,request.SiteId,request.MachineId,request.ExpectedStateId,
            expectedGeneration=Decimal(request.ExpectedGeneration),expectedSecurityEpoch=Decimal(request.ExpectedSecurityEpoch),expiresAt=expires,request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.machine.operation","propose-enable");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"machine.enable",scope,initial.Now,ct);
            var done=await OperationalRetry(c,t,request.OperationId,actor,bytes,ct);if(done is not null)return done;
            if(initial.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,request.MachineId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"machine.enable",scope,final.Now,ct);
            if(final.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            var current=await OperationalSnapshot(c,t,request.SiteId,request.MachineId,ct);MatchOperationalVersion(current,request.ExpectedStateId,request.ExpectedGeneration);
            if(current.Status=="enabled")throw new SecurityConflictException();
            if(current.MachineRetired||current.SiteRetired||expires<=final.Now||expires>final.Now.AddMinutes(15))throw new SecurityDeniedException();
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,proposalId=request.ProposalId,expiresAt=expires,status="enable-proposed-not-enabled"});
            await OperationalOperation(c,t,request.OperationId,actor,scope,bytes,result,ct);
            await Exec(c,t,"""
                INSERT configuration.MachineEnableProposal(ProposalId,SiteId,MachineId,ExpectedStateId,ExpectedGeneration,ProposerId,ProposerSessionHash,IssuedAt,ExpiresAt,OperationId)
                VALUES(@proposal,@site,@machine,@prior,@generation,@actor,@token,@now,@expires,@op);
                """,ct,("@proposal",request.ProposalId),("@site",request.SiteId),("@machine",request.MachineId),("@prior",(object?)request.ExpectedStateId??DBNull.Value),
                ("@generation",request.ExpectedGeneration),("@actor",actor),("@token",token),("@now",final.Now),("@expires",expires),("@op",request.OperationId));
            await OperationalAudit(c,t,request.OperationId,actor,"machine-enable-propose",request.Reason,ct);return result;
        },ct);
    }

    public Task<byte[]> ApproveMachineEnableAsync(byte[] sessionHash,Guid operationId,Guid proposalId,Guid stateId,Guid siteId,Guid machineId,
        long expectedSecurityEpoch,string reason,CancellationToken ct=default)
    {
        ValidateIdentityChange(operationId,stateId,expectedSecurityEpoch,reason);if(proposalId==Guid.Empty)throw new ArgumentException("Exact enable proposal required.");
        var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(siteId,machineId);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="machine-enable-approve",proposalId,stateId,siteId,machineId,expectedSecurityEpoch=Decimal(expectedSecurityEpoch),reason});
        return Transaction(operationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.machine.operation","approve-enable");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"machine.enable",scope,initial.Now,ct);
            var done=await OperationalRetry(c,t,operationId,actor,bytes,ct);if(done is not null)return done;
            if(initial.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"machine.enable",scope,final.Now,ct);
            if(final.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            Guid proposer;byte[] proposerSession;Guid? prior;long generation;
            await using(var q=Command(c,t,"SELECT ProposerId,ProposerSessionHash,ExpectedStateId,ExpectedGeneration FROM configuration.MachineEnableProposal WHERE ProposalId=@proposal AND SiteId=@site AND MachineId=@machine AND ExpiresAt>@now"))
            {
                Add(q,("@proposal",proposalId),("@site",siteId),("@machine",machineId),("@now",final.Now));await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct))throw new SecurityDeniedException();proposer=r.GetGuid(0);proposerSession=(byte[])r[1];prior=r.IsDBNull(2)?null:r.GetGuid(2);generation=r.GetInt64(3);
            }
            if(actor==proposer||await Session(c,t,proposerSession,final.Now,ct)!=proposer)throw new SecurityDeniedException();
            await Authorize(c,t,proposer,"machine.enable",scope,final.Now,ct);
            var current=await OperationalSnapshot(c,t,siteId,machineId,ct);MatchOperationalVersion(current,prior,generation);
            if(current.Status=="enabled")throw new SecurityConflictException();if(current.MachineRetired||current.SiteRetired)throw new SecurityDeniedException();
            var next=generation+1;var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,stateId,operationalGeneration=Decimal(next),status="enabled-not-activated"});
            await OperationalOperation(c,t,operationId,actor,scope,bytes,result,ct);
            await WriteOperationalTransition(c,t,operationId,stateId,siteId,machineId,next,"enabled",prior,proposalId,proposer,actor,final.Now,ct);
            await OperationalAudit(c,t,operationId,actor,"machine-enable-approve",reason,ct);return result;
        },ct);
    }

    public Task<byte[]> SuspendMachineAsync(byte[] sessionHash,MachineSuspensionRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.StateId,request.ExpectedSecurityEpoch,request.Reason);ValidateOperationalVersion(request.ExpectedStateId,request.ExpectedGeneration);
        var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(request.SiteId,request.MachineId);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="machine-suspend",request.StateId,request.SiteId,request.MachineId,request.ExpectedStateId,
            expectedGeneration=Decimal(request.ExpectedGeneration),expectedSecurityEpoch=Decimal(request.ExpectedSecurityEpoch),request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.machine.operation","suspend");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"machine.suspend",scope,initial.Now,ct);
            var done=await OperationalRetry(c,t,request.OperationId,actor,bytes,ct);if(done is not null)return done;
            if(initial.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,request.MachineId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"machine.suspend",scope,final.Now,ct);
            if(final.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            var current=await OperationalSnapshot(c,t,request.SiteId,request.MachineId,ct);MatchOperationalVersion(current,request.ExpectedStateId,request.ExpectedGeneration);
            var next=current.Generation+1;var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,stateId=request.StateId,operationalGeneration=Decimal(next),status="machine-suspended-not-runtime-stop"});
            await OperationalOperation(c,t,request.OperationId,actor,scope,bytes,result,ct);
            await WriteOperationalTransition(c,t,request.OperationId,request.StateId,request.SiteId,request.MachineId,next,"suspended",current.StateId,null,null,actor,final.Now,ct);
            await OperationalAudit(c,t,request.OperationId,actor,"machine-suspend",request.Reason,ct);return result;
        },ct);
    }

    public Task<MachineOperationalSnapshot> ReadMachineOperationalStateAsync(byte[] sessionHash,Guid siteId,Guid machineId,CancellationToken ct=default)
    {
        var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(siteId,machineId);
        return Transaction(null,false,async(c,t)=>
        {
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),false,ct);
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);await Authorize(c,t,actor,"commissioning.read",scope,state.Now,ct);
            return await OperationalSnapshot(c,t,siteId,machineId,ct);
        },ct);
    }
    public Task<MachineEnableProposalSnapshot> ReadMachineEnableProposalAsync(byte[] sessionHash,Guid proposalId,Guid siteId,Guid machineId,CancellationToken ct=default)
    {
        if(proposalId==Guid.Empty)throw new ArgumentException("Exact enable proposal required.");var token=sessionHash.ToArray();var scope=AuthorizationScope.Machine(siteId,machineId);
        return Transaction(null,false,async(c,t)=>
        {
            await Lock(c,t,ConfigurationLockResources.Machine(installationId,machineId),false,ct);
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);await Authorize(c,t,actor,"machine.enable",scope,state.Now,ct);
            var current=await OperationalSnapshot(c,t,siteId,machineId,ct);
            await using var q=Command(c,t,"SELECT p.ProposerId,p.ExpectedStateId,p.ExpectedGeneration,p.ExpiresAt,a.Reason FROM configuration.MachineEnableProposal p JOIN security.SecurityAudit a ON a.OperationId=p.OperationId WHERE p.ProposalId=@proposal AND p.SiteId=@site AND p.MachineId=@machine");
            Add(q,("@proposal",proposalId),("@site",siteId),("@machine",machineId));await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
            return new MachineEnableProposalSnapshot(proposalId,siteId,machineId,r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetInt64(2),r.GetDateTimeOffset(3),r.GetString(4),current.Generation);
        },ct);
    }
    public Task<byte[]> ReadMachineOperationalOperationAsync(byte[] sessionHash,Guid operationId,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Exact operation required.");var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);var done=await FindOperation(c,t,operationId,ct)??throw new SecurityDeniedException();
            if(done.Actor!=actor)throw new SecurityDeniedException();using var doc=JsonDocument.Parse(done.Request);
            var action=doc.RootElement.GetProperty("action").GetString();var permission=action switch{"machine-suspend"=>"machine.suspend","machine-enable-propose" or "machine-enable-approve"=>"machine.enable",_=>throw new SecurityDeniedException()};
            await Authorize(c,t,actor,permission,done.Scope,state.Now,ct);return done.Result;
        },ct);
    }
    private static void ValidateOperationalVersion(Guid? stateId,long generation)
    {if(generation<0||generation==long.MaxValue||stateId==Guid.Empty||(generation==0)!=(stateId is null))throw new ArgumentException("Exact bounded operational version required.");}
    private static void MatchOperationalVersion(MachineOperationalSnapshot current,Guid? stateId,long generation)
    {if(current.StateId!=stateId||current.Generation!=generation)throw new SecurityConflictException();}
    private static async Task<MachineOperationalSnapshot> OperationalSnapshot(SqlConnection c,SqlTransaction t,Guid site,Guid machine,CancellationToken ct)
    {
        await using var q=Command(c,t,"""
            SELECT m.IsRetired,site.IsRetired,s.StateId,o.Generation,o.Status,o.EffectiveFrom,
                CASE WHEN s.StateId IS NULL AND EXISTS(SELECT 1 FROM configuration.MachineOperationalTransition h WHERE h.SiteId=m.SiteId AND h.MachineId=m.MachineId) THEN 1 ELSE 0 END
            FROM [master].Machine m JOIN [master].Site site ON site.SiteId=m.SiteId
            LEFT JOIN configuration.MachineOperationalState s ON s.SiteId=m.SiteId AND s.MachineId=m.MachineId
            LEFT JOIN configuration.MachineOperationalTransition o ON o.StateId=s.StateId AND o.Generation=s.Generation
            WHERE m.SiteId=@site AND m.MachineId=@machine;
            """);Add(q,("@site",site),("@machine",machine));await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
        if(r.GetInt32(6)!=0)throw new SecurityDeniedException();
        if(r.IsDBNull(2))return new(site,machine,"unknown",0,null,null,r.GetBoolean(0),r.GetBoolean(1));
        if(r.IsDBNull(3))throw new SecurityDeniedException();return new(site,machine,r.GetString(4),r.GetInt64(3),r.GetGuid(2),r.GetDateTimeOffset(5),r.GetBoolean(0),r.GetBoolean(1));
    }
    private static async Task<long> RequireMachineEnabled(SqlConnection c,SqlTransaction t,Guid site,Guid machine,DateTimeOffset now,CancellationToken ct)
    {var current=await OperationalSnapshot(c,t,site,machine,ct);if(current.Status!="enabled"||current.MachineRetired||current.SiteRetired||current.ChangedAt>now)throw new SecurityDeniedException();return current.Generation;}
    private static async Task<byte[]?> OperationalRetry(SqlConnection c,SqlTransaction t,Guid operationId,Guid actor,byte[] bytes,CancellationToken ct)
    {var done=await FindOperation(c,t,operationId,ct);if(done is null)return null;if(done.Actor!=actor||!done.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return done.Result;}
    private static async Task OperationalOperation(SqlConnection c,SqlTransaction t,Guid operationId,Guid actor,AuthorizationScope scope,byte[] bytes,byte[] result,CancellationToken ct)
        =>await Exec(c,t,"INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes) VALUES(@op,@actor,@bytes,@hash,'M',@site,@machine,@result)",ct,
            ("@op",operationId),("@actor",actor),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),("@site",scope.SiteId!.Value),("@machine",scope.MachineId!.Value),("@result",result));
    private static async Task OperationalAudit(SqlConnection c,SqlTransaction t,Guid operationId,Guid actor,string action,string reason,CancellationToken ct)
        =>await Exec(c,t,"INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@audit,@op,@actor,@action,@reason); UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1",ct,
            ("@audit",Guid.NewGuid()),("@op",operationId),("@actor",actor),("@action",action),("@reason",reason));
    private static async Task WriteOperationalTransition(SqlConnection c,SqlTransaction t,Guid operationId,Guid stateId,Guid site,Guid machine,long generation,string status,Guid? prior,Guid? proposal,Guid? proposer,Guid actor,DateTimeOffset now,CancellationToken ct)
        =>await Exec(c,t,"""
            INSERT configuration.MachineOperationalTransition(StateId,SiteId,MachineId,Generation,Status,PriorStateId,ProposalId,EnablingProposerId,ActorPrincipalId,EffectiveFrom,OperationId)
            VALUES(@state,@site,@machine,@generation,@status,@prior,@proposal,@proposer,@actor,@now,@op);
            UPDATE configuration.MachineOperationalState SET StateId=@state,Generation=@generation WHERE SiteId=@site AND MachineId=@machine;
            IF @@ROWCOUNT=0 INSERT configuration.MachineOperationalState(SiteId,MachineId,StateId,Generation) VALUES(@site,@machine,@state,@generation);
            """,ct,("@state",stateId),("@site",site),("@machine",machine),("@generation",generation),("@status",status),("@prior",(object?)prior??DBNull.Value),
            ("@proposal",(object?)proposal??DBNull.Value),("@proposer",(object?)proposer??DBNull.Value),("@actor",actor),("@now",now),("@op",operationId));
}
