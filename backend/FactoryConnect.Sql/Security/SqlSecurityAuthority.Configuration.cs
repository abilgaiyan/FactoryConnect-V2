using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

public sealed record PackageReviewRequest(Guid OperationId,Guid ReviewId,Guid SiteId,Guid PackageRevisionId,
    byte[] ContentHash,bool Approve,long ExpectedEpoch,string Reason);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Creator-only inline submission with authenticated provenance. Does not approve or activate.</summary>
    public Task<byte[]> SubmitConfigurationAsync(byte[] sessionHash,Guid operationId,PackageDefinition package,
        long expectedEpoch,string reason,CancellationToken ct=default)
    {
        ValidateIdentityChange(operationId,package.RevisionId,expectedEpoch,reason);
        if(package.Machines.Count>100||package.Machines.Sum(m=>(long)m.Signals.Count)>1000)
            throw new ArgumentException("A submission supports at most 100 machines and 1000 mappings.");
        var canonical=PackageCanonicalizer.Serialize(package);
        if(canonical.Length>1048576)throw new ArgumentException("Canonical submission exceeds the 1 MiB operation limit.");
        var hash=SHA256.HashData(canonical);
        using var parsed=JsonDocument.Parse(canonical);var root=parsed.RootElement.Clone();var site=root.GetProperty("siteId").GetGuid();
        var revision=root.GetProperty("revisionId").GetGuid();var token=sessionHash.ToArray();
        var request=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="submit-configuration",revisionId=revision,siteId=site,contentHash=Convert.ToHexString(hash),expectedEpoch,reason});
        return Transaction(operationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);var scope=AuthorizationScope.Site(site);
            await Authorize(c,t,actor,"configuration.edit",scope,state.Now,ct);
            await Authorize(c,t,actor,"configuration.submit",scope,state.Now,ct);
            var completed=await FindOperation(c,t,operationId,ct);
            if(completed is not null)
            {
                if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(request))throw new SecurityConflictException();
                await using var existing=Command(c,t,"SELECT CanonicalBytes FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@revision");
                Add(existing,("@revision",revision));
                if(await existing.ExecuteScalarAsync(ct) is not byte[] prior||!prior.AsSpan().SequenceEqual(canonical))throw new SecurityConflictException();
                return completed.Result;
            }
            if(state.Epoch!=expectedEpoch)throw new SecurityConflictException();
            await ConfigurationLocks(c,t,root,installationId,ct);
            var final=await State(c,t,ct);var finalActor=await Session(c,t,token,final.Now,ct);
            if(finalActor!=actor)throw new SecurityDeniedException();
            if(final.Epoch!=expectedEpoch)throw new SecurityConflictException();
            await Authorize(c,t,actor,"configuration.edit",scope,final.Now,ct);
            await Authorize(c,t,actor,"configuration.submit",scope,final.Now,ct);
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@revision OR (PackageId=@package AND RevisionNumber=@number)"))
            {
                Add(q,("@revision",revision),("@package",root.GetProperty("packageId").GetGuid()),("@number",root.GetProperty("revisionNumber").GetInt32()));
                if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();
            }
            await new SqlPackageRepository().InsertCanonicalAsync(c,t,canonical,ct);
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,packageRevisionId=revision,contentHash=Convert.ToHexString(hash).ToLowerInvariant(),status="submitted"});
            await ConfigurationOperation(c,t,operationId,actor,scope,request,result,"submit-configuration",reason,ct);
            await Exec(c,t,"""
                INSERT configuration.PackageSubmission(PackageRevisionId,SiteId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId,OperationId)
                VALUES(@revision,@site,@hash,@actor,@actor,@op);
                """,ct,("@revision",revision),("@site",site),("@hash",hash),("@actor",actor),("@op",operationId));
            return result;
        },ct);
    }

    /// <summary>One terminal independent review over authenticated immutable submission. No activation occurs.</summary>
    public Task<byte[]> ReviewConfigurationAsync(byte[] sessionHash,PackageReviewRequest request,CancellationToken ct=default)
    {
        ValidateIdentityChange(request.OperationId,request.PackageRevisionId,request.ExpectedEpoch,request.Reason);
        if(request.ReviewId==Guid.Empty||request.SiteId==Guid.Empty||request.ContentHash.Length!=32)throw new ArgumentException("Exact review identity and hash required.");
        var hash=request.ContentHash.ToArray();var token=sessionHash.ToArray();var scope=AuthorizationScope.Site(request.SiteId);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,action="review-configuration",request.ReviewId,request.SiteId,request.PackageRevisionId,
            contentHash=Convert.ToHexString(hash),request.Approve,request.ExpectedEpoch,request.Reason});
        return Transaction(request.OperationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            await Authorize(c,t,actor,"configuration.review",scope,state.Now,ct);
            var completed=await FindOperation(c,t,request.OperationId,ct);
            if(completed is not null)
            {if(completed.Actor!=actor||!completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();return completed.Result;}
            if(state.Epoch!=request.ExpectedEpoch)throw new SecurityConflictException();
            Guid author;Guid submitter;byte[] canonical;
            await using(var q=Command(c,t,"""
                SELECT s.AuthorPrincipalId,s.SubmitterPrincipalId,r.CanonicalBytes
                FROM configuration.PackageSubmission s JOIN configuration.ConfigurationPackageRevision r ON r.PackageRevisionId=s.PackageRevisionId
                WHERE s.PackageRevisionId=@revision AND s.SiteId=@site AND s.ContentHash=@hash AND r.ContentHash=@hash AND r.SubmittedAt IS NOT NULL;
                """))
            {
                Add(q,("@revision",request.PackageRevisionId),("@site",request.SiteId),("@hash",hash));await using var r=await q.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct))throw new SecurityDeniedException();author=r.GetGuid(0);submitter=r.GetGuid(1);canonical=(byte[])r[2];
            }
            if(actor==author||actor==submitter||!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(canonical)))throw new SecurityDeniedException();
            using var parsed=JsonDocument.Parse(canonical);await ConfigurationLocks(c,t,parsed.RootElement,installationId,ct);
            var final=await State(c,t,ct);var finalActor=await Session(c,t,token,final.Now,ct);
            if(finalActor!=actor)throw new SecurityDeniedException();
            if(final.Epoch!=request.ExpectedEpoch)throw new SecurityConflictException();
            await Authorize(c,t,actor,"configuration.review",scope,final.Now,ct);
            await using(var q=Command(c,t,"SELECT 1 FROM configuration.PackageReview WHERE PackageRevisionId=@revision OR ReviewId=@review"))
            {Add(q,("@revision",request.PackageRevisionId),("@review",request.ReviewId));if(await q.ExecuteScalarAsync(ct) is not null)throw new SecurityConflictException();}
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId=request.OperationId,reviewId=request.ReviewId,packageRevisionId=request.PackageRevisionId,status=request.Approve?"approved":"rejected"});
            await ConfigurationOperation(c,t,request.OperationId,actor,scope,bytes,result,"review-configuration",request.Reason,ct);
            await Exec(c,t,"""
                INSERT configuration.PackageReview(ReviewId,PackageRevisionId,SiteId,ContentHash,AuthorPrincipalId,SubmitterPrincipalId,ReviewerPrincipalId,Decision,OperationId)
                VALUES(@review,@revision,@site,@hash,@author,@submitter,@actor,@decision,@op);
                """,ct,("@review",request.ReviewId),("@revision",request.PackageRevisionId),("@site",request.SiteId),("@hash",hash),
                ("@author",author),("@submitter",submitter),("@actor",actor),("@decision",request.Approve?"approved":"rejected"),("@op",request.OperationId));
            return result;
        },ct);
    }

    /// <summary>Creator's own historical receipt, under the exact current workflow permissions; not security administration.</summary>
    public Task<byte[]> ReadConfigurationOperationAsync(byte[] sessionHash,Guid operationId,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Explicit operation required.");var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            var completed=await FindOperation(c,t,operationId,ct)??throw new SecurityDeniedException();
            if(completed.Actor!=actor)throw new SecurityDeniedException();
            using var request=JsonDocument.Parse(completed.Request);
            if(!request.RootElement.TryGetProperty("action",out var field)||field.ValueKind!=JsonValueKind.String)throw new SecurityDeniedException();
            var action=field.GetString();
            if(action=="submit-configuration")
            {await Authorize(c,t,actor,"configuration.edit",completed.Scope,state.Now,ct);await Authorize(c,t,actor,"configuration.submit",completed.Scope,state.Now,ct);}
            else if(action=="review-configuration")await Authorize(c,t,actor,"configuration.review",completed.Scope,state.Now,ct);
            else throw new SecurityDeniedException();
            return completed.Result;
        },ct);
    }

    private static async Task ConfigurationOperation(SqlConnection c,SqlTransaction t,Guid op,Guid actor,AuthorizationScope scope,
        byte[] request,byte[] result,string action,string reason,CancellationToken ct)
    {
        await Exec(c,t,"""
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,ResultBytes)
            VALUES(@op,@actor,@request,@hash,'S',@site,@result);
            INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@audit,@op,@actor,@action,@reason);
            UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
            """,ct,("@op",op),("@actor",actor),("@request",request),("@hash",SHA256.HashData(request)),("@site",scope.SiteId!.Value),
            ("@result",result),("@audit",Guid.NewGuid()),("@action",action),("@reason",reason));
    }

    private static async Task ConfigurationLocks(SqlConnection c,SqlTransaction t,JsonElement root,Guid installation,CancellationToken ct)
    {
        var resources=new Dictionary<string,(byte Phase,string Mode)>(StringComparer.Ordinal);
        void Policy(string kind,JsonElement pin)=>resources[ConfigurationLockResources.Policy(installation,kind,pin.GetProperty("revisionId").GetGuid())]=(0,"Shared");
        Policy("timezone",root.GetProperty("timezone"));
        foreach(var machine in root.GetProperty("machines").EnumerateArray())
        {
            Policy("schedule",machine.GetProperty("schedule"));Policy("break",machine.GetProperty("breakPolicy"));
            resources[ConfigurationLockResources.Machine(installation,machine.GetProperty("machineId").GetGuid())]=(1,"Exclusive");
        }
        using var batch=new DataTable();batch.Columns.Add("Phase",typeof(byte));batch.Columns.Add("Resource",typeof(string));batch.Columns.Add("Mode",typeof(string));
        foreach(var (resource,value)in resources)batch.Rows.Add(value.Phase,resource,value.Mode);
        await using var q=Command(c,t,"""
            DECLARE @resource nvarchar(255),@mode varchar(16),@result int;
            DECLARE gates CURSOR LOCAL FAST_FORWARD FOR SELECT Resource,Mode FROM @locks ORDER BY Phase,Resource;
            OPEN gates; FETCH NEXT FROM gates INTO @resource,@mode;
            WHILE @@FETCH_STATUS=0
            BEGIN
                EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode=@mode,@LockOwner='Transaction',@LockTimeout=10000;
                IF @result<0 THROW 51020,'Configuration authority lock unavailable.',1;
                FETCH NEXT FROM gates INTO @resource,@mode;
            END;
            CLOSE gates; DEALLOCATE gates;
            """);var p=q.Parameters.Add("@locks",SqlDbType.Structured);p.TypeName="configuration.AuthorityLockBatch";p.Value=batch;
        var started=Stopwatch.GetTimestamp();var activity=Activity.Current?.Source.Name=="FactoryConnect.V2.Security"?Activity.Current:null;
        try{await q.ExecuteNonQueryAsync(ct);}
        finally
        {
            activity?.SetTag("security.configuration.lock.ms",Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            activity?.SetTag("security.configuration.lock.count",resources.Count);
        }
    }
}
