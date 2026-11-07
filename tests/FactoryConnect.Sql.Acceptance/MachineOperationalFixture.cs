using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// Labelled privileged upstream fixtures for existing feature suites, not proof of the operational command workflow.
internal static class MachineOperationalFixture
{
    internal static async Task Enable(SqlConnection connection,Guid site,IReadOnlyList<Guid> machines,Guid proposer,byte[] proposerSession,Guid approver,DateTimeOffset now)
    {
        var rows=machines.Select(machine=>new{machine,proposal=Guid.NewGuid(),state=Guid.NewGuid(),proposalOp=Guid.NewGuid(),enableOp=Guid.NewGuid()}).ToArray();
        await using var q=new SqlCommand("""
            DECLARE @rows TABLE(Machine uniqueidentifier,Proposal uniqueidentifier,StateId uniqueidentifier,ProposalOp uniqueidentifier,EnableOp uniqueidentifier);
            INSERT @rows SELECT machine,proposal,stateId,proposalOp,enableOp FROM OPENJSON(@jsonRows)
                WITH(machine uniqueidentifier '$.machine',proposal uniqueidentifier '$.proposal',stateId uniqueidentifier '$.state',proposalOp uniqueidentifier '$.proposalOp',enableOp uniqueidentifier '$.enableOp') r
                WHERE NOT EXISTS(SELECT 1 FROM configuration.MachineOperationalState s WHERE s.MachineId=r.machine);
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                SELECT ProposalOp,@proposer,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'M',@site,Machine,0x7B7D FROM @rows
                UNION ALL SELECT EnableOp,@approver,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'M',@site,Machine,0x7B7D FROM @rows;
            INSERT configuration.MachineEnableProposal(ProposalId,SiteId,MachineId,ExpectedGeneration,ProposerId,ProposerSessionHash,IssuedAt,ExpiresAt,OperationId)
                SELECT Proposal,@site,Machine,0,@proposer,@session,@now,DATEADD(minute,15,@now),ProposalOp FROM @rows;
            INSERT configuration.MachineOperationalTransition(StateId,SiteId,MachineId,Generation,Status,ProposalId,EnablingProposerId,ActorPrincipalId,EffectiveFrom,OperationId)
                SELECT StateId,@site,Machine,1,'enabled',Proposal,@proposer,@approver,@now,EnableOp FROM @rows;
            INSERT configuration.MachineOperationalState SELECT @site,Machine,StateId,1 FROM @rows;
            INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason)
                SELECT NEWID(),ProposalOp,@proposer,'fixture-enable-propose',N'Synthetic upstream operational fixture only' FROM @rows
                UNION ALL SELECT NEWID(),EnableOp,@approver,'fixture-enable-approve',N'Synthetic upstream operational fixture only' FROM @rows;
            """,connection);
        q.Parameters.AddWithValue("@jsonRows",JsonSerializer.Serialize(rows));q.Parameters.AddWithValue("@site",site);q.Parameters.AddWithValue("@proposer",proposer);q.Parameters.AddWithValue("@approver",approver);
        q.Parameters.Add("@session",SqlDbType.Binary,32).Value=proposerSession;q.Parameters.Add("@now",SqlDbType.DateTimeOffset).Value=now;await q.ExecuteNonQueryAsync();
    }
}
