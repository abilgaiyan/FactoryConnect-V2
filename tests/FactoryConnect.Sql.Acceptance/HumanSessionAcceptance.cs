using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class HumanSessionAcceptance
{
    private sealed class Verifier(VerifiedHumanIdentity identity):IHumanIdentityVerifier
    {
        public Task<VerifiedHumanIdentity> VerifyAsync(string assertion,CancellationToken cancellationToken)
            =>assertion=="fixture-valid"?Task.FromResult(identity):throw new SecurityDeniedException();
    }
    internal static async Task<int> Run(string connectionString,Guid installation)
    {
        var count=0;await using var c=new SqlConnection(connectionString);await c.OpenAsync();
        var principal=Guid.NewGuid();var runtime=Guid.NewGuid();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        await Exec("""
            INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES
                (@p,'H',CONVERT(varbinary(2048),'https://session.fixture'),CONVERT(varbinary(2048),'human')),
                (@r,'R',CONVERT(varbinary(2048),'https://session.fixture'),CONVERT(varbinary(2048),'runtime'));
            """,("@p",principal),("@r",runtime));
        var proof=new VerifiedHumanIdentity("https://session.fixture","human","proof-1","fixture-mfa",now.AddSeconds(-1),now.AddHours(2));
        SqlHumanSessionAuthority Service(VerifiedHumanIdentity p)=>new(connectionString,installation,new Verifier(p));
        await Denied(()=>Service(proof).IssueAsync("invalid"),"unverified authentication never creates a session");
        await Exec("UPDATE security.InstallationState SET IsSealed=1 WHERE Singleton=1");
        await Denied(()=>Service(proof).IssueAsync("fixture-valid"),"sealed installation refuses verified sign-in");
        await Exec("UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1");
        await Denied(()=>Service(proof with{Subject="Human"}).IssueAsync("fixture-valid"),"identity matching preserves exact case");
        await Denied(()=>Service(proof with{Subject="human\0"}).IssueAsync("fixture-valid"),"identity matching preserves exact byte length");
        await Denied(()=>Service(proof with{Subject="runtime"}).IssueAsync("fixture-valid"),"runtime cannot receive a human session");
        await Denied(()=>Service(proof with{Subject="unknown"}).IssueAsync("fixture-valid"),"sign-in never provisions an unknown principal");
        await Denied(()=>Service(proof with{ExpiresAt=now.AddSeconds(-1)}).IssueAsync("fixture-valid"),"expired identity proof rejected at SQL time");
        await Denied(()=>Service(proof with{AuthenticatedAt=now.AddHours(1)}).IssueAsync("fixture-valid"),"future authentication rejected");
        await Denied(()=>Service(proof with{AuthenticatedAt=now.AddMinutes(-6)}).IssueAsync("fixture-valid"),"stale authentication rejected");
        await Denied(()=>new SqlHumanSessionAuthority(connectionString,Guid.NewGuid(),new Verifier(proof)).IssueAsync("fixture-valid"),"session issuance checks installation identity");
        var before=Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority"));
        var sessions=await Task.WhenAll(Attempt(proof),Attempt(proof));var issued=sessions.Single(s=>s is not null)!;
        Check(sessions.Count(s=>s is not null)==1,"parallel proof replays issue exactly one session");
        Check(issued.Token.Length==32&&issued.PrincipalId==principal,"issued random bearer is bound to exact principal");
        Check(issued.ExpiresAt<=now.AddHours(1).AddSeconds(20),"session lifetime capped at one hour");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority"))==before+1,"replay appends no extra session");
        var tokenHash=SHA256.HashData(issued.Token);
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority WHERE TokenHash=@t",("@t",tokenHash)))==1,"only SHA-256 bearer hash is persisted");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE CHARINDEX(@secret,ResultBytes)>0 OR CHARINDEX(@secret,RequestBytes)>0",("@secret",issued.Token)))==0,"operation history contains no bearer secret");
        var auth=new SqlSecurityAuthority(connectionString,installation);
        await Denied(()=>auth.ReadAsync(tokenHash,"configuration.edit",AuthorizationScope.Installation,(_,_,_)=>Task.FromResult(true)),"authentication grants no implicit business permission");
        var grant=Guid.NewGuid();
        await Exec("""
            INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
            VALUES(@grant,@p,@p,'bootstrap','configuration.read','I',1,0,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()));
            """,("@grant",grant),("@p",principal));
        Check(await auth.ReadAsync(tokenHash,"configuration.read",AuthorizationScope.Installation,(_,_,_)=>Task.FromResult(true)),"issued bearer hash works with authoritative grants");
        var bounded=await Service(proof with{AssertionId="short-proof",ExpiresAt=now.AddMinutes(2)}).IssueAsync("fixture-valid");
        Check(bounded.ExpiresAt==now.AddMinutes(2),"session cannot outlive verified assertion");
        var rollback=proof with{AssertionId="rollback-proof"};
        await Exec("CREATE TRIGGER security.Fixture_SessionFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51102,'Session audit fixture failure.',1; END;");
        try{await Service(rollback).IssueAsync("fixture-valid");throw new InvalidOperationException("Expected failure");}
        catch(SqlException e)when(e.Number==51102){Check(true,"session audit failure aborts issuance");}
        await Exec("DROP TRIGGER security.Fixture_SessionFailure");
        var retried=await Service(rollback).IssueAsync("fixture-valid");
        Check(retried.Token.Length==32,"rollback does not consume authentication proof");
        // An exclusive revocation gate acquired first must delay sign-in and make its fresh authority check fail.
        await using(var gate=new SqlConnection(connectionString))
        {
            await gate.OpenAsync();await using var tx=(SqlTransaction)await gate.BeginTransactionAsync();
            await using(var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@gate,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51020,'fixture gate',1;",gate,tx))
            {q.Parameters.AddWithValue("@gate",SqlSecurityAuthority.GateResource(installation));await q.ExecuteNonQueryAsync();}
            var waiting=Service(proof with{AssertionId="revocation-race"}).IssueAsync("fixture-valid");
            await Task.Delay(150);Check(!waiting.IsCompleted,"exclusive authority gate delays session issuance");
            var op=Guid.NewGuid();await using(var q=new SqlCommand("""
                INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                    VALUES(@op,@p,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
                INSERT security.PrincipalRevocation(PrincipalId,OperationId) VALUES(@p,@op);
                UPDATE security.InstallationState SET AuthorityEpoch=AuthorityEpoch+1 WHERE Singleton=1;
                """,gate,tx))
            {q.Parameters.AddWithValue("@op",op);q.Parameters.AddWithValue("@p",principal);await q.ExecuteNonQueryAsync();}
            await tx.CommitAsync();await Denied(()=>waiting,"revocation committed first blocks pending sign-in");
        }
        await Denied(()=>auth.ReadAsync(tokenHash,"configuration.read",AuthorizationScope.Installation,(_,_,_)=>Task.FromResult(true)),"revocation also denies previously issued session");
        try{await Exec("DELETE security.HumanSessionIssuance");throw new InvalidOperationException("Expected immutability");}
        catch(SqlException e)when(e.Number==51030){Check(true,"session issuance history is immutable");}
        return count;
        async Task<IssuedHumanSession?> Attempt(VerifiedHumanIdentity p){try{return await Service(p).IssueAsync("fixture-valid");}catch(SecurityDeniedException){return null;}}
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {foreach(var(name,value)in values)if(value is byte[] b)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=b;else q.Parameters.AddWithValue(name,value);}
}
