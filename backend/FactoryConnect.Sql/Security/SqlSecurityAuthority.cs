using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Sql.Security;

/// <summary>Server-side authority kernel. No sign-in, bootstrap or HTTP write endpoint is registered by this type.</summary>
public sealed partial class SqlSecurityAuthority(string connectionString, Guid installationId)
{
    private static readonly ActivitySource Diagnostics=new("FactoryConnect.V2.Security");

    public static string GateResource(Guid installation) => $"fc2:security:{installation:N}";

    /// <summary>The trusted handler must read only the authorized scope and finish materializing its result before returning.</summary>
    public Task<T> ReadAsync<T>(byte[] sessionHash, string permission, AuthorizationScope scope,
        Func<SqlConnection,SqlTransaction,CancellationToken,Task<T>> handler, CancellationToken ct=default)
    {
        var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var authorizationStart=Stopwatch.GetTimestamp();
            var authorityActivity=Activity.Current?.Source.Name=="FactoryConnect.V2.Security"?Activity.Current:null;
            try
            {
                var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
                await Authorize(c,t,actor,permission,scope,state.Now,ct);
            }
            finally{authorityActivity?.SetTag("security.authorization.elapsed.ms",Stopwatch.GetElapsedTime(authorizationStart).TotalMilliseconds);}
            return await handler(c,t,ct);
        },ct);
    }

    public Task<byte[]> RevokeGrantAsync(byte[] sessionHash,Guid operationId,Guid grantId,long expectedEpoch,string reason,CancellationToken ct=default)
        => Revoke(sessionHash,operationId,grantId,null,expectedEpoch,reason,ct);
    public Task<byte[]> RevokeSessionAsync(byte[] sessionHash,Guid operationId,byte[] targetSessionHash,long expectedEpoch,string reason,CancellationToken ct=default)
        => Revoke(sessionHash,operationId,null,targetSessionHash,expectedEpoch,reason,ct);

    public Task<byte[]> ReadOperationResultAsync(byte[] sessionHash,Guid operationId,CancellationToken ct=default)
    {
        var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            var result=await FindOperation(c,t,operationId,ct) ?? throw new SecurityDeniedException();
            if(result.Actor!=actor)throw new SecurityDeniedException();
            await Authorize(c,t,actor,"security.access.manage",result.Scope,state.Now,ct);
            return result.Result;
        },ct);
    }

    private Task<byte[]> Revoke(byte[] sessionHash,Guid operationId,Guid? grantId,byte[]? targetHash,long expectedEpoch,string reason,CancellationToken ct)
    {
        if(operationId==Guid.Empty || grantId==Guid.Empty || expectedEpoch<0 || string.IsNullOrWhiteSpace(reason) || reason.Length>500)
            throw new ArgumentException("Invalid security change.");
        _=new UTF8Encoding(false,true).GetBytes(reason); // Reject lossy fingerprint encoding.
        if(targetHash is not null && targetHash.Length!=32)throw new ArgumentException("Invalid session hash.");
        // Snapshot caller-owned buffers before asynchronous work or fingerprint creation.
        var token=sessionHash.ToArray();var target=targetHash?.ToArray();
        var action=grantId.HasValue ? "revoke-grant":"revoke-session";
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new { version=1,action,grantId,targetSessionHash=target is null?null:Convert.ToHexString(target),expectedEpoch,reason });
        return Transaction(operationId,true,async(c,t)=>
        {
            var state=await State(c,t,ct);var actor=await Session(c,t,token,state.Now,ct);
            var completed=await FindOperation(c,t,operationId,ct);
            var scope=completed?.Scope ?? await TargetScope(c,t,grantId,target,ct);
            await Authorize(c,t,actor,"security.access.manage",scope,state.Now,ct);
            if(completed is not null)
            {
                if(completed.Actor!=actor || !completed.Request.AsSpan().SequenceEqual(bytes))throw new SecurityConflictException();
                return completed.Result;
            }
            if(state.Epoch!=expectedEpoch)throw new SecurityConflictException();
            var result=JsonSerializer.SerializeToUtf8Bytes(new{operationId,status="revoked"});
            await Exec(c,t,"""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,SiteId,MachineId,ResultBytes)
                VALUES(@op,@actor,@bytes,@hash,@kind,@site,@machine,@result);
                """,ct,("@op",operationId),("@actor",actor),("@bytes",bytes),("@hash",SHA256.HashData(bytes)),
                ("@kind",scope.Kind),("@site",(object?)scope.SiteId??DBNull.Value),("@machine",(object?)scope.MachineId??DBNull.Value),("@result",result));
            if(grantId.HasValue)
                await Exec(c,t,"IF NOT EXISTS(SELECT 1 FROM security.GrantRevocation WHERE GrantId=@target) INSERT security.GrantRevocation(GrantId,OperationId) VALUES(@target,@op)",ct,("@target",grantId.Value),("@op",operationId));
            else
                await Exec(c,t,"IF NOT EXISTS(SELECT 1 FROM security.SessionRevocation WHERE TokenHash=@target) INSERT security.SessionRevocation(TokenHash,OperationId) VALUES(@target,@op)",ct,("@target",target!),("@op",operationId));
            await Exec(c,t,"""
                INSERT security.SecurityAudit(AuditId,OperationId,ActorPrincipalId,Action,Reason) VALUES(@id,@op,@actor,@action,@reason);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,ct,("@id",Guid.NewGuid()),("@op",operationId),("@actor",actor),("@action",action),("@reason",reason));
            return result;
        },ct);
    }

    private async Task<T> Transaction<T>(Guid? operationId,bool exclusive,Func<SqlConnection,SqlTransaction,Task<T>> execute,CancellationToken ct)
    {
        if(installationId==Guid.Empty)throw new ArgumentException("Explicit installation identity is required.");
        using var activity=Diagnostics.StartActivity("security.transaction",ActivityKind.Internal);
        var started=Stopwatch.GetTimestamp();long transactionStarted=0;var outcome="unavailable";
        await using var c=new SqlConnection(connectionString);
        // Client statistics are opt-in with a sampled trace, and require no extra SQL commands.
        if(activity is not null)c.StatisticsEnabled=true;
        try
        {
            var opening=Stopwatch.GetTimestamp();
            try{await c.OpenAsync(ct);}
            finally{activity?.SetTag("security.connection.open.ms",Stopwatch.GetElapsedTime(opening).TotalMilliseconds);}
            if(activity is not null)c.ResetStatistics(); // Count the transaction, not login/pool handshakes.
            transactionStarted=Stopwatch.GetTimestamp();
            // Read committed and the mandatory gate give a fresh authority read after waiting.
            await using var t=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
            try
            {
                if(operationId is { } op)
                {
                    var waiting=Stopwatch.GetTimestamp();
                    try{await Lock(c,t,$"fc2:operation:{installationId:N}:{op:N}",true,ct);}
                    finally{activity?.SetTag("security.operation.lock.wait.ms",Stopwatch.GetElapsedTime(waiting).TotalMilliseconds);}
                }
                var gateWaiting=Stopwatch.GetTimestamp();
                try{await Lock(c,t,GateResource(installationId),exclusive,ct);}
                finally{activity?.SetTag("security.gate.wait.ms",Stopwatch.GetElapsedTime(gateWaiting).TotalMilliseconds);}
                await using(var identity=Command(c,t,"SELECT InstallationId FROM platform.InstallationIdentity WHERE Singleton=1"))
                    if((Guid?)(await identity.ExecuteScalarAsync(ct))!=installationId)throw new SecurityDeniedException();
                var value=await execute(c,t);await t.CommitAsync(ct);outcome="completed";return value;
            }
            catch
            {try{await t.RollbackAsync(CancellationToken.None);}catch(Exception e)when(e is InvalidOperationException or SqlException){}throw;}
        }
        catch(Exception e)
        {
            outcome=ct.IsCancellationRequested||e is OperationCanceledException?"cancelled":
                e is SecurityDeniedException?"denied":e is SecurityConflictException?"conflict":"unavailable";
            throw;
        }
        finally
        {
            if(activity is not null)
            {
                activity.SetTag("security.result",outcome);
                activity.SetTag("security.gate.mode",exclusive?"exclusive":"shared");
                activity.SetTag("security.connection.open.attempts",1);
                activity.SetTag("security.elapsed.ms",Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if(transactionStarted!=0)activity.SetTag("security.transaction.elapsed.ms",Stopwatch.GetElapsedTime(transactionStarted).TotalMilliseconds);
                if(c.State==ConnectionState.Open)
                {
                    var statistics=c.RetrieveStatistics();
                    activity.SetTag("security.sql.roundtrips",statistics["ServerRoundtrips"]);
                    activity.SetTag("security.sql.bytes.received",statistics["BytesReceived"]);
                }
                activity.SetStatus(outcome=="completed"?ActivityStatusCode.Ok:ActivityStatusCode.Error);
                // Never tag identities, SQL text, token material, values, or exception messages.
            }
        }
    }

    private static async Task Lock(SqlConnection c,SqlTransaction t,string resource,bool exclusive,CancellationToken ct)
        => await Exec(c,t,"""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode=@mode,@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51020,'Security transaction lock unavailable.',1;
            """,ct,("@resource",resource),("@mode",exclusive?"Exclusive":"Shared"));

    private sealed record AuthorityState(long Epoch,DateTimeOffset Now);
    private static async Task<AuthorityState> State(SqlConnection c,SqlTransaction t,CancellationToken ct)
    {
        await using var q=Command(c,t,"SELECT IsSealed,AuthorityEpoch,TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00') FROM security.InstallationState WHERE Singleton=1");
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)||r.GetBoolean(0))throw new SecurityDeniedException();
        return new(r.GetInt64(1),r.GetDateTimeOffset(2));
    }
    private static async Task<Guid> Session(SqlConnection c,SqlTransaction t,byte[] hash,DateTimeOffset now,CancellationToken ct)
    {
        if(hash.Length!=32)throw new SecurityDeniedException();
        await using var q=Command(c,t,"""
            SELECT s.PrincipalId FROM security.SessionAuthority s JOIN security.Principal p ON p.PrincipalId=s.PrincipalId
            WHERE s.TokenHash=@token AND p.Kind='H' AND s.IssuedAt<=@now AND s.ExpiresAt>@now
            AND NOT EXISTS(SELECT 1 FROM security.SessionRevocation r WHERE r.TokenHash=s.TokenHash)
            AND NOT EXISTS(SELECT 1 FROM security.PrincipalRevocation r WHERE r.PrincipalId=s.PrincipalId);
            """);Add(q,("@token",hash),("@now",now));
        return (Guid?)(await q.ExecuteScalarAsync(ct)) ?? throw new SecurityDeniedException();
    }
    private sealed record Grant(Guid Id,Guid Grantee,Guid Grantor,Guid? Parent,string? Origin,string Permission,
        AuthorizationScope Scope,bool Use,bool Delegate,DateTimeOffset From,DateTimeOffset To,bool Revoked);
    private static async Task<int> Authorize(SqlConnection c,SqlTransaction t,Guid actor,string permission,AuthorizationScope scope,DateTimeOffset now,CancellationToken ct,bool delegation=false,Guid? requiredGrant=null,Guid? excludedPrincipal=null)
    {
        if(string.IsNullOrEmpty(permission)||permission.Length>100||permission.Any(ch=>ch>127))throw new SecurityDeniedException();
        if(scope.Kind!="I")
        {
            await using var resource=Command(c,t,scope.Kind=="S" ? "SELECT 1 FROM [master].Site WHERE SiteId=@site" :
                "SELECT 1 FROM [master].Machine WHERE SiteId=@site AND MachineId=@machine");
            Add(resource,("@site",scope.SiteId!.Value),("@machine",(object?)scope.MachineId??DBNull.Value));
            if(await resource.ExecuteScalarAsync(ct) is null)throw new SecurityDeniedException();
        }
        var grants=new Dictionary<Guid,Grant>();
        await using(var q=Command(c,t,"""
            WITH ancestors AS (
                SELECT * FROM security.PermissionGrant WHERE GranteeId=@actor AND Permission=@permission
                    AND ((@delegation=0 AND UseAllowed=1) OR (@delegation=1 AND CanDelegate=1))
                    AND (@requiredGrant IS NULL OR GrantId=@requiredGrant)
                UNION ALL
                SELECT p.* FROM security.PermissionGrant p JOIN ancestors a ON p.GrantId=a.ParentGrantId
            )
            SELECT a.*,CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM security.GrantRevocation r WHERE r.GrantId=a.GrantId)
                OR EXISTS(SELECT 1 FROM security.PrincipalRevocation r WHERE r.PrincipalId=a.GranteeId OR r.PrincipalId=a.GrantorId)
                OR EXISTS(SELECT 1 FROM security.Principal p WHERE (p.PrincipalId=a.GranteeId OR p.PrincipalId=a.GrantorId) AND p.Kind<>'H')
                OR a.GranteeId=@excludedPrincipal OR a.GrantorId=@excludedPrincipal
                THEN 1 ELSE 0 END) AS IsRevoked
            FROM ancestors a OPTION(MAXRECURSION 64);
            """))
        {
            Add(q,("@actor",actor),("@permission",permission),("@delegation",delegation),("@requiredGrant",(object?)requiredGrant??DBNull.Value),("@excludedPrincipal",(object?)excludedPrincipal??DBNull.Value));
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                Guid? NullableGuid(string name)=>r[name] is DBNull?null:(Guid)r[name];
                var id=(Guid)r["GrantId"];
                grants.TryAdd(id,new(id,(Guid)r["GranteeId"],(Guid)r["GrantorId"],NullableGuid("ParentGrantId"),r["RootOrigin"] as string,
                    (string)r["Permission"],AuthorizationScope.From((string)r["ScopeKind"],NullableGuid("SiteId"),NullableGuid("MachineId")),
                    (bool)r["UseAllowed"],(bool)r["CanDelegate"],(DateTimeOffset)r["EffectiveFrom"],(DateTimeOffset)r["ExpiresAt"],(bool)r["IsRevoked"]));
            }
        }
        foreach(var candidate in grants.Values.Where(g=>g.Grantee==actor&&(delegation?g.Delegate:g.Use)&&g.Permission==permission&&(!requiredGrant.HasValue||g.Id==requiredGrant.Value)))
        {
            var current=candidate;var seen=new HashSet<Guid>();var eligible=true;
            while(true)
            {
                if(!seen.Add(current.Id)||current.Revoked||current.Permission!=permission||current.From>now||current.To<=now||!current.Scope.Contains(scope)) {eligible=false;break;}
                if(current.Parent is null) {eligible=current.Origin=="bootstrap";break;}
                if(!grants.TryGetValue(current.Parent.Value,out var parent)||!parent.Delegate||parent.Grantee!=current.Grantor||
                    !parent.Scope.Contains(current.Scope)||current.From<parent.From||current.To>parent.To) {eligible=false;break;}
                current=parent;
            }
            if(eligible)return seen.Count;
        }
        throw new SecurityDeniedException();
    }
    private static async Task<AuthorizationScope> TargetScope(SqlConnection c,SqlTransaction t,Guid? grant,byte[]? session,CancellationToken ct)
    {
        await using var q=Command(c,t,grant.HasValue ? "SELECT ScopeKind,SiteId,MachineId FROM security.PermissionGrant WHERE GrantId=@target" :
            "SELECT 'I',CAST(NULL AS uniqueidentifier),CAST(NULL AS uniqueidentifier) FROM security.SessionAuthority WHERE TokenHash=@target");
        Add(q,("@target",grant.HasValue?(object)grant.Value:session!));await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))throw new SecurityDeniedException();
        return AuthorizationScope.From(r.GetString(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2));
    }
    private sealed record Completed(Guid Actor,byte[] Request,byte[] Result,AuthorizationScope Scope);
    private static async Task<Completed?> FindOperation(SqlConnection c,SqlTransaction t,Guid operation,CancellationToken ct)
    {
        await using var q=Command(c,t,"SELECT ActorPrincipalId,RequestBytes,ResultBytes,ScopeKind,SiteId,MachineId FROM security.SecurityOperation WHERE OperationId=@op");
        Add(q,("@op",operation));await using var r=await q.ExecuteReaderAsync(ct);
        return !await r.ReadAsync(ct)?null:new(r.GetGuid(0),(byte[])r[1],(byte[])r[2],AuthorizationScope.From(r.GetString(3),r.IsDBNull(4)?null:r.GetGuid(4),r.IsDBNull(5)?null:r.GetGuid(5)));
    }
    private static SqlCommand Command(SqlConnection c,SqlTransaction t,string sql)=>new(sql,c,t){CommandTimeout=30};
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {
        foreach(var (name,value) in values)
            if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;
            else if(value is DateTimeOffset instant)q.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=instant;
            else if(name is "@site" or "@machine" or "@requiredGrant" or "@excludedPrincipal")q.Parameters.Add(name,SqlDbType.UniqueIdentifier).Value=value;
            else q.Parameters.AddWithValue(name,value);
    }
    private static async Task Exec(SqlConnection c,SqlTransaction t,string sql,CancellationToken ct,params (string Name,object Value)[] values)
    {await using var q=Command(c,t,sql);Add(q,values);await q.ExecuteNonQueryAsync(ct);}
}
