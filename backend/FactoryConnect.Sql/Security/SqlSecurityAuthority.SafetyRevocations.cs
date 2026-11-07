using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed record PolicyRevocationProposalRequest(Guid OperationId,Guid ProposalId,Guid SiteId,string PolicyKind,Guid PolicyRevisionId,byte[] ContentHash,
    string Classification,DateTimeOffset? RequestedEffectiveFrom,DateTimeOffset ExpiresAt,long ExpectedSecurityEpoch,string Reason);
public sealed record PolicyRevocationProposalSnapshot(Guid ProposalId,Guid SiteId,string PolicyKind,Guid PolicyRevisionId,byte[] ContentHash,
    Guid ProposerId,DateTimeOffset? RequestedEffectiveFrom,DateTimeOffset ExpiresAt,string Reason);
public sealed record ApprovalWithdrawalRequest(Guid OperationId,Guid SiteId,Guid PackageRevisionId,byte[] ContentHash,Guid ReviewId,long ExpectedSecurityEpoch,string Reason);

public sealed partial class SqlSecurityAuthority
{
    public Task<byte[]> ProposePolicyRevocationAsync(byte[] sessionHash,PolicyRevocationProposalRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.ProposalId,request.ExpectedSecurityEpoch,request.Reason);_=PolicySource(request.PolicyKind);
        if(request.SiteId==Guid.Empty||request.PolicyRevisionId==Guid.Empty||request.ContentHash.Length!=32||request.Classification!="safety-critical")throw new ArgumentException("Exact classified safety revocation required.");
        var hash=request.ContentHash.ToArray();var token=sessionHash.ToArray();var scope=AuthorizationScope.Site(request.SiteId);
        var scheduled=request.RequestedEffectiveFrom?.ToUniversalTime();var expires=request.ExpiresAt.ToUniversalTime();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="policy-revoke-propose",request.ProposalId,request.SiteId,request.PolicyKind,request.PolicyRevisionId,
            contentHash=Convert.ToHexString(hash),classification=request.Classification,requestedEffectiveFrom=scheduled,expiresAt=expires,expectedSecurityEpoch=Decimal(request.ExpectedSecurityEpoch),request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.safety.command","propose-policy-revocation");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"policy.revoke",scope,initial.Now,ct);
            var done=await OperationalRetry(c,t,request.OperationId,actor,bytes,ct);if(done is not null)return done;
            if(initial.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            await Lock(c,t,ConfigurationLockResources.Policy(installationId,request.PolicyKind,request.PolicyRevisionId),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"policy.revoke",scope,final.Now,ct);
            if(final.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            if(expires<=final.Now||expires>final.Now.AddMinutes(15)||scheduled is {} instant&&instant<=final.Now)throw new SecurityDeniedException();
            await ValidatePolicyRevision(c,t,request.SiteId,request.PolicyKind,request.PolicyRevisionId,hash,ct);
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,proposalId=request.ProposalId,status="safety-revocation-proposed-not-effective"});
            await SiteAuthorityOperation(c,t,request.OperationId,actor,request.SiteId,bytes,result,ct);
            await Exec(c,t,"""
                INSERT configuration.PolicyRevocationProposal(ProposalId,SiteId,PolicyKind,TimezoneRevisionId,ScheduleRevisionId,BreakPolicyRevisionId,ContentHash,Classification,
                    RequestedEffectiveFrom,IssuedAt,ExpiresAt,ProposerId,ProposerSessionHash,OperationId)
                VALUES(@proposal,@site,@kind,@timezone,@schedule,@break,@hash,'safety-critical',@scheduled,@now,@expires,@actor,@token,@op);
                """,ct,("@proposal",request.ProposalId),("@site",request.SiteId),("@kind",request.PolicyKind),("@timezone",request.PolicyKind=="timezone"?request.PolicyRevisionId:DBNull.Value),
                ("@schedule",request.PolicyKind=="schedule"?request.PolicyRevisionId:DBNull.Value),("@break",request.PolicyKind=="break"?request.PolicyRevisionId:DBNull.Value),("@hash",hash),
                ("@scheduled",(object?)scheduled??DBNull.Value),("@now",final.Now),("@expires",expires),("@actor",actor),("@token",token),("@op",request.OperationId));
            await OperationalAudit(c,t,request.OperationId,actor,"policy-revoke-propose",request.Reason,ct);return result;
        },ct);
    }
    public Task<byte[]> ApprovePolicyRevocationAsync(byte[] sessionHash,Guid operationId,Guid proposalId,Guid revocationId,Guid siteId,long expectedSecurityEpoch,string reason,CancellationToken ct=default)
    {
        ValidateIdentityChange(operationId,revocationId,expectedSecurityEpoch,reason);if(proposalId==Guid.Empty)throw new ArgumentException("Exact proposal required.");
        var token=sessionHash.ToArray();var scope=AuthorizationScope.Site(siteId);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="policy-revoke-approve",proposalId,revocationId,siteId,expectedSecurityEpoch=Decimal(expectedSecurityEpoch),reason});
        return Transaction(operationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.safety.command","approve-policy-revocation");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"policy.revoke",scope,initial.Now,ct);
            var done=await OperationalRetry(c,t,operationId,actor,bytes,ct);if(done is not null)return done;
            if(initial.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            var proposal=await LoadPolicyProposal(c,t,proposalId,siteId,ct);
            await Lock(c,t,ConfigurationLockResources.Policy(installationId,proposal.Kind,proposal.Revision),true,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"policy.revoke",scope,final.Now,ct);
            if(final.Epoch!=expectedSecurityEpoch)throw new SecurityConflictException();
            if(actor==proposal.Proposer||proposal.Expires<=final.Now||await Session(c,t,proposal.Session,final.Now,ct)!=proposal.Proposer)throw new SecurityDeniedException();
            await Authorize(c,t,proposal.Proposer,"policy.revoke",scope,final.Now,ct);
            await ValidatePolicyRevision(c,t,siteId,proposal.Kind,proposal.Revision,proposal.Hash,ct);
            var effective=proposal.Scheduled is {} scheduled&&scheduled>final.Now?scheduled:final.Now;
            var (_,column)=PolicySource(proposal.Kind);
            await using(var q=Command(c,t,$"SELECT MIN(EffectiveFrom) FROM configuration.PolicySafetyRevocation WHERE SiteId=@site AND PolicyKind=@kind AND {column}=@revision AND ContentHash=@hash"))
            {
                Add(q,("@site",siteId),("@revision",proposal.Revision));q.Parameters.Add("@kind",SqlDbType.VarChar,16).Value=proposal.Kind;q.Parameters.Add("@hash",SqlDbType.Binary,32).Value=proposal.Hash;var earliest=await q.ExecuteScalarAsync(ct);
                if(earliest is DateTimeOffset prior&&prior<=effective)throw new SecurityConflictException();
            }
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.PolicyRevocationAuthority WHERE ProposalId=@proposal"))
            {Add(q,("@proposal",proposalId));if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,revocationId,effectiveFrom=effective,recordedAt=final.Now,status="policy-revocation-recorded-not-runtime-stop"});
            await SiteAuthorityOperation(c,t,operationId,actor,siteId,bytes,result,ct);
            await Exec(c,t,"""
                INSERT configuration.PolicySafetyRevocation(RevocationId,SiteId,PolicyKind,TimezoneRevisionId,ScheduleRevisionId,BreakPolicyRevisionId,ContentHash,EffectiveFrom,OperationId)
                    VALUES(@revocation,@site,@kind,@timezone,@schedule,@break,@hash,@effective,@op);
                INSERT configuration.PolicyRevocationAuthority(RevocationId,ProposalId,ProposerId,ApproverId,RecordedAt,OperationId)
                    VALUES(@revocation,@proposal,@proposer,@actor,@now,@op);
                """,ct,("@revocation",revocationId),("@site",siteId),("@kind",proposal.Kind),("@timezone",proposal.Kind=="timezone"?proposal.Revision:DBNull.Value),
                ("@schedule",proposal.Kind=="schedule"?proposal.Revision:DBNull.Value),("@break",proposal.Kind=="break"?proposal.Revision:DBNull.Value),("@hash",proposal.Hash),
                ("@effective",effective),("@op",operationId),("@proposal",proposalId),("@proposer",proposal.Proposer),("@actor",actor),("@now",final.Now));
            await OperationalAudit(c,t,operationId,actor,"policy-revoke-approve",reason,ct);return result;
        },ct);
    }
    public Task<byte[]> WithdrawConfigurationApprovalAsync(byte[] sessionHash,ApprovalWithdrawalRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.ReviewId,request.ExpectedSecurityEpoch,request.Reason);
        if(request.PackageRevisionId==Guid.Empty||request.ContentHash.Length!=32)throw new ArgumentException("Exact reviewed package/hash required.");
        var token=sessionHash.ToArray();var hash=request.ContentHash.ToArray();var scope=AuthorizationScope.Site(request.SiteId);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="approval-withdraw",request.SiteId,request.PackageRevisionId,request.ReviewId,contentHash=Convert.ToHexString(hash),expectedSecurityEpoch=Decimal(request.ExpectedSecurityEpoch),request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            Activity.Current?.SetTag("security.safety.command","withdraw-approval");
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"configuration.review",scope,initial.Now,ct);
            var done=await OperationalRetry(c,t,request.OperationId,actor,bytes,ct);if(done is not null)return done;
            if(initial.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            byte[] canonical;Guid author,submitter;
            await using(var q=Command(c,t,"""
                SELECT p.CanonicalBytes,r.AuthorPrincipalId,r.SubmitterPrincipalId FROM configuration.PackageReview r
                JOIN configuration.ConfigurationPackageRevision p ON p.SiteId=r.SiteId AND p.PackageRevisionId=r.PackageRevisionId AND p.ContentHash=r.ContentHash
                WHERE r.ReviewId=@review AND r.SiteId=@site AND r.PackageRevisionId=@revision AND r.ContentHash=@hash AND r.Decision='approved' AND p.SubmittedAt IS NOT NULL;
                """))
            {
                Add(q,("@review",request.ReviewId),("@site",request.SiteId),("@revision",request.PackageRevisionId),("@hash",hash));await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct))throw new SecurityDeniedException();canonical=(byte[])r[0];author=r.GetGuid(1);submitter=r.GetGuid(2);
            }
            if(actor==author||actor==submitter||!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(canonical)))throw new SecurityDeniedException();
            using var parsed=JsonDocument.Parse(canonical);await ConfigurationLocks(c,t,parsed.RootElement,installationId,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"configuration.review",scope,final.Now,ct);
            if(final.Epoch!=request.ExpectedSecurityEpoch)throw new SecurityConflictException();
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.PackageApprovalRevocation WHERE ReviewId=@review"))
            {Add(q,("@review",request.ReviewId));if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,reviewId=request.ReviewId,recordedAt=final.Now,status="approval-withdrawn-not-runtime-stop"});
            await SiteAuthorityOperation(c,t,request.OperationId,actor,request.SiteId,bytes,result,ct);
            await Exec(c,t,"""
                INSERT configuration.PackageApprovalRevocation(ReviewId,OperationId) VALUES(@review,@op);
                INSERT configuration.ApprovalWithdrawalAuthority(ReviewId,SiteId,PackageRevisionId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId,ActorPrincipalId,RecordedAt,OperationId)
                    VALUES(@review,@site,@revision,@hash,@author,@submitter,@actor,@now,@op);
                """,ct,("@review",request.ReviewId),("@op",request.OperationId),("@site",request.SiteId),("@revision",request.PackageRevisionId),("@hash",hash),
                ("@author",author),("@submitter",submitter),("@actor",actor),("@now",final.Now));
            await OperationalAudit(c,t,request.OperationId,actor,"approval-withdraw",request.Reason,ct);return result;
        },ct);
    }
    public Task<PolicyRevocationProposalSnapshot> ReadPolicyRevocationProposalAsync(byte[] sessionHash,Guid proposalId,Guid siteId,CancellationToken ct=default)
    {
        var token=sessionHash.ToArray();var scope=AuthorizationScope.Site(siteId);
        return Transaction(null,false,async(c,t)=>
        {
            var initial=await State(c,t,ct);var actor=await Session(c,t,token,initial.Now,ct);await Authorize(c,t,actor,"policy.revoke",scope,initial.Now,ct);
            var p=await LoadPolicyProposal(c,t,proposalId,siteId,ct);await Lock(c,t,ConfigurationLockResources.Policy(installationId,p.Kind,p.Revision),false,ct);
            var final=await State(c,t,ct);await Session(c,t,token,final.Now,ct);await Authorize(c,t,actor,"policy.revoke",scope,final.Now,ct);
            await using var q=Command(c,t,"SELECT Reason FROM security.SecurityAudit WHERE OperationId=(SELECT OperationId FROM configuration.PolicyRevocationProposal WHERE ProposalId=@proposal)");Add(q,("@proposal",proposalId));
            var reason=(string?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();return new PolicyRevocationProposalSnapshot(proposalId,siteId,p.Kind,p.Revision,p.Hash,p.Proposer,p.Scheduled,p.Expires,reason);
        },ct);
    }
    public Task<byte[]> ReadSafetyRevocationOperationAsync(byte[] sessionHash,Guid operationId,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Exact operation required.");var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);var done=await FindOperation(c,t,operationId,ct)??throw new SecurityDeniedException();
            if(done.Actor!=actor)throw new SecurityDeniedException();using var doc=JsonDocument.Parse(done.Request);
            var permission=doc.RootElement.GetProperty("action").GetString() switch{"policy-revoke-propose" or "policy-revoke-approve"=>"policy.revoke","approval-withdraw"=>"configuration.review",_=>throw new SecurityDeniedException()};
            await Authorize(c,t,actor,permission,done.Scope,state.Now,ct);return done.Result;
        },ct);
    }
    private sealed record PendingPolicy(string Kind,Guid Revision,byte[] Hash,Guid Proposer,byte[] Session,DateTimeOffset? Scheduled,DateTimeOffset Expires);
    private static async Task<PendingPolicy> LoadPolicyProposal(SqlConnection c,SqlTransaction t,Guid id,Guid site,CancellationToken ct)
    {
        await using var q=Command(c,t,"SELECT PolicyKind,COALESCE(TimezoneRevisionId,ScheduleRevisionId,BreakPolicyRevisionId),ContentHash,ProposerId,ProposerSessionHash,RequestedEffectiveFrom,ExpiresAt FROM configuration.PolicyRevocationProposal WHERE ProposalId=@proposal AND SiteId=@site AND Classification='safety-critical'");
        Add(q,("@proposal",id),("@site",site));await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
        return new(r.GetString(0),r.GetGuid(1),(byte[])r[2],r.GetGuid(3),(byte[])r[4],r.IsDBNull(5)?null:r.GetDateTimeOffset(5),r.GetDateTimeOffset(6));
    }
    private static (string Table,string Column) PolicySource(string kind)=>kind switch
    {"timezone"=>("SiteTimezoneRevision","TimezoneRevisionId"),"schedule"=>("SharedScheduleRevision","ScheduleRevisionId"),"break"=>("SharedBreakPolicyRevision","BreakPolicyRevisionId"),_=>throw new ArgumentException("Exact policy kind required.")};
    private static async Task ValidatePolicyRevision(SqlConnection c,SqlTransaction t,Guid site,string kind,Guid revision,byte[] hash,CancellationToken ct)
    {
        var (table,column)=PolicySource(kind);await using var q=Command(c,t,$"SELECT CanonicalBytes FROM configuration.{table} WHERE SiteId=@site AND {column}=@revision AND ContentHash=@hash");
        Add(q,("@site",site),("@revision",revision));q.Parameters.Add("@hash",SqlDbType.Binary,32).Value=hash;var bytes=(byte[]?)(await q.ExecuteScalarAsync(ct))??throw new SecurityDeniedException();
        if(!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(bytes)))throw new SecurityDeniedException();
    }
    private static async Task SiteAuthorityOperation(SqlConnection c,SqlTransaction t,Guid operationId,Guid actor,Guid site,byte[] bytes,byte[] result,CancellationToken ct)
        =>await Exec(c,t,"INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,ResultBytes) VALUES(@op,@actor,@bytes,@hash,'S',@site,@result)",ct,
            ("@op",operationId),("@actor",actor),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),("@site",site),("@result",result));
}
