using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class GrantIssuanceAcceptance
{
    internal static async Task<int> Run(string connection,Guid installation)
    {
        var count=0;await using var c=new SqlConnection(connection);await c.OpenAsync();
        var admin=Guid.NewGuid();var member=Guid.NewGuid();var leaf=Guid.NewGuid();var runtime=Guid.NewGuid();var manager=Guid.NewGuid();
        var tokens=new Dictionary<Guid,byte[]>();
        var site=Guid.Parse("11111111-1111-1111-1111-111111111111");var machine=Guid.Parse("33333333-3333-3333-3333-333333333333");
        var scope=AuthorizationScope.Machine(site,machine);
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        foreach(var principal in new[]{admin,member,leaf,runtime,manager})
        {
            var token=RandomNumberGenerator.GetBytes(32);tokens.Add(principal,token);
            await Exec("""
                INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@p,@kind,0x6772616E7473,@subject);
                INSERT security.SessionAuthority VALUES(@token,@p,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',0);
                """,("@p",principal),("@kind",principal==runtime?"R":"H"),("@subject",principal.ToByteArray()),("@token",token));
        }
        var management=await Root(admin,"security.access.manage",AuthorizationScope.Installation,true,true);
        var parent=await Root(admin,"configuration.read",AuthorizationScope.Site(site),false,true);
        await Root(manager,"security.access.manage",AuthorizationScope.Installation,true,false);
        var service=new SqlSecurityAuthority(connection,installation);
        var issuer=new SqlSecurityAuthority(new SqlConnectionStringBuilder(connection){ApplicationName="fc2grant-issuer"}.ConnectionString,installation);
        var revoker=new SqlSecurityAuthority(new SqlConnectionStringBuilder(connection){ApplicationName="fc2grant-revoker"}.ConnectionString,installation);
        async Task<PermissionGrantRequest> Request(Guid? target=null,Guid? ancestor=null,string permission="configuration.read")
            =>new(Guid.NewGuid(),Guid.NewGuid(),target??member,ancestor??parent,permission,scope,true,false,now.AddSeconds(-1),now.AddMinutes(30),await Epoch(),"grant fixture");
        var request=await Request();
        await Denied(()=>service.GrantPermissionAsync(tokens[manager],request),"access management alone cannot grant business authority");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{GranteeId=admin}),"administrator cannot grant themselves extra use authority");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{GranteeId=runtime}),"runtime principal cannot receive a human grant");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{GranteeId=Guid.NewGuid()}),"grant does not create unknown identities");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{Scope=AuthorizationScope.Installation}),"child cannot widen site parent to installation");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{ExpiresAt=now.AddDays(3)}),"child expiry cannot exceed parent ceiling");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{EffectiveFrom=now.AddDays(-3)}),"child cannot predate parent interval");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{Permission="configuration.edit"}),"parent cannot delegate another action");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{Scope=AuthorizationScope.Machine(Guid.Parse("22222222-2222-2222-2222-222222222222"),machine)}),"grant rejects wrong machine-site identity");
        await Conflict(()=>service.GrantPermissionAsync(tokens[admin],request with{ExpectedEpoch=long.MaxValue}),"stale grant expected epoch rejects mutation");
        var readOnly=await Root(admin,"audit.read",scope,true,false);
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{ParentGrantId=readOnly,Permission="audit.read"}),"use permission without delegation cannot issue child");
        await Denied(()=>service.GrantPermissionAsync(tokens[member],request with{GranteeId=leaf}),"delegation attempt also requires scoped access management");
        var deepParent=await Root(admin,"configuration.review",scope,false,true);
        for(var level=1;level<64;level++)
        {
            var id=Guid.NewGuid();
            await Exec("""
                INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,ParentGrantId,Permission,ScopeKind,SiteId,MachineId,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                SELECT @id,GranteeId,GranteeId,GrantId,Permission,ScopeKind,SiteId,MachineId,0,1,EffectiveFrom,ExpiresAt
                FROM security.PermissionGrant WHERE GrantId=@parent;
                """,("@id",id),("@parent",deepParent));deepParent=id;
        }
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{ParentGrantId=deepParent,Permission="configuration.review"}),"grant issuance cannot exceed 64-node delegation ceiling");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{Permission="Configuration.Read"}),"grant action matching is case-sensitive");
        var futureParent=await Root(admin,"machine.suspend",scope,false,true,now.AddDays(1),now.AddDays(2));
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{ParentGrantId=futureParent,Permission="machine.suspend",EffectiveFrom=now.AddDays(1),ExpiresAt=now.AddDays(1).AddHours(1)}),"future delegation parent cannot issue before its start");
        var expiredParent=await Root(admin,"machine.enable",scope,false,true,now.AddDays(-2),now.AddDays(-1));
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request with{ParentGrantId=expiredParent,Permission="machine.enable"}),"expired delegation parent cannot issue new authority");
        var copies=await Task.WhenAll(service.GrantPermissionAsync(tokens[admin],request),service.GrantPermissionAsync(tokens[admin],request));
        Check(copies[0].SequenceEqual(copies[1]),"parallel exact grant retries return one immutable result");
        Check(await Epoch()==request.ExpectedEpoch+1,"exact retries advance authority epoch once");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@op",("@op",request.OperationId)))==1,"exact retries append one audit");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.GrantIssuance WHERE GrantId=@grant AND OperationId=@op AND ActorPrincipalId=@p",("@grant",request.GrantId),("@op",request.OperationId),("@p",admin)))==1,"grant operation identity is durably bound to grantor");
        var foreignOperation=Guid.NewGuid();
        await Exec("""
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
            VALUES(@op,@p,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
            """,("@op",foreignOperation),("@p",member));
        await ForeignKey(()=>Exec("INSERT security.GrantIssuance VALUES(@grant,@op,@actor)",("@grant",parent),("@op",foreignOperation),("@actor",admin)),"mapping cannot bind a different operation actor");
        await ForeignKey(()=>Exec("INSERT security.GrantIssuance VALUES(@grant,@op,@actor)",("@grant",parent),("@op",foreignOperation),("@actor",member)),"mapping cannot bind a different grantor");
        Check(await Read(member,"configuration.read"),"issued child authorizes its exact machine");
        Check((await service.ReadOperationResultAsync(tokens[admin],request.OperationId)).SequenceEqual(copies[0]),"authorized issuer can retrieve grant receipt");
        await Denied(()=>service.ReadAsync(tokens[member],"configuration.read",AuthorizationScope.Site(site),(_,_,_)=>Task.FromResult(true)),"issued machine grant cannot read entire site");
        await Conflict(()=>service.GrantPermissionAsync(tokens[admin],request with{Reason="changed"}),"grant operation ID rejects altered fingerprint");
        await Conflict(async()=>await service.GrantPermissionAsync(tokens[admin],request with{OperationId=Guid.NewGuid(),ExpectedEpoch=await Epoch()}),"grant identity cannot be reused in a new operation");
        var equivalent=request with{EffectiveFrom=request.EffectiveFrom.ToOffset(TimeSpan.FromHours(5.5)),ExpiresAt=request.ExpiresAt.ToOffset(TimeSpan.FromHours(5.5))};
        Check((await service.GrantPermissionAsync(tokens[admin],equivalent)).SequenceEqual(copies[0]),"equivalent UTC instants preserve grant retry fingerprint");
        // Issued grants can delegate only with both management use and their explicit delegable parent.
        var memberManagement=await Request(member,management,"security.access.manage");
        await service.GrantPermissionAsync(tokens[admin],memberManagement with{CanDelegate=true});
        var delegable=await Request(member);await service.GrantPermissionAsync(tokens[admin],delegable with{CanDelegate=true});
        var child=await Request(leaf,delegable.GrantId);
        await service.GrantPermissionAsync(tokens[member],child);
        Check(await Read(leaf,"configuration.read"),"scoped delegated access administrator issues a bounded grandchild");
        await Denied(async()=>await service.GrantPermissionAsync(tokens[member],(await Request(leaf,parent))),"grantor cannot select someone else's parent");
        // Failure must roll back grant, durable mapping, receipt, audit and epoch as one transaction.
        var rollback=await Request(leaf);var epoch=await Epoch();
        await Exec("CREATE TRIGGER security.Fixture_GrantAuditFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51103,'Grant fixture audit failure.',1; END;");
        try{await service.GrantPermissionAsync(tokens[admin],rollback);throw new InvalidOperationException("Expected audit failure.");}
        catch(SqlException e)when(e.Number==51103){Check(true,"grant audit failure aborts issuance");}
        await Exec("DROP TRIGGER security.Fixture_GrantAuditFailure");
        Check(Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM security.PermissionGrant WHERE GrantId=@grant)+(SELECT COUNT(*) FROM security.GrantIssuance WHERE GrantId=@grant)+(SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@op)",("@grant",rollback.GrantId),("@op",rollback.OperationId)))==0,"grant mapping and result roll back together");
        Check(await Epoch()==epoch,"failed grant preserves authority epoch");
        await service.GrantPermissionAsync(tokens[admin],rollback);Check(await Read(leaf,"configuration.read"),"identical failed grant can retry after rollback");
        await Immutable("DELETE security.GrantIssuance","grant-operation history cannot be erased");
        // Block the real operation at audit while it owns exclusive authority, then observe the competing operation waiting.
        await Exec("""
            CREATE TRIGGER security.Fixture_GrantBarrier ON security.SecurityAudit AFTER INSERT AS
            BEGIN
                DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:grant:test-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
                IF @r<0 THROW 51020,'Fixture barrier unavailable.',1;
            END;
            """);
        var raceParent=await Root(admin,"commissioning.read",scope,false,true);
        var raceRequest=await Request(leaf,raceParent,"commissioning.read");
        await using(var b=new SqlConnection(connection))
        {
            await b.OpenAsync();await using var tx=(SqlTransaction)await b.BeginTransactionAsync();await Barrier(b,tx);
            var issuing=issuer.GrantPermissionAsync(tokens[admin],raceRequest);
            Task<byte[]>? revoking=null;
            try
            {
                await WaitForLock("fc2grant-issuer","S");
                revoking=revoker.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),raceParent,raceRequest.ExpectedEpoch+1,"grant first race");
                await WaitForLock("fc2grant-revoker","X");Check(!revoking.IsCompleted,"revocation waits behind uncommitted grant issuance");
            }
            finally{await tx.CommitAsync();}
            var raceResult=await issuing;await (revoking??throw new InvalidOperationException("Missing revoker."));
            Check((await service.GrantPermissionAsync(tokens[admin],raceRequest)).SequenceEqual(raceResult),"completed issuance receipt survives parent revocation without reapplying grant");
            await Denied(()=>Read(leaf,"commissioning.read"),"revocation after issuance invalidates the new descendant");
        }
        var revokeFirstParent=await Root(admin,"configuration.submit",scope,false,true);
        var pending=await Request(leaf,revokeFirstParent,"configuration.submit");
        await using(var b=new SqlConnection(connection))
        {
            await b.OpenAsync();await using var tx=(SqlTransaction)await b.BeginTransactionAsync();await Barrier(b,tx);
            var revoking=revoker.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),revokeFirstParent,pending.ExpectedEpoch,"revoke first race");
            Task<byte[]>? issuing=null;
            try
            {
                await WaitForLock("fc2grant-revoker","S");
                // Even an anticipated epoch cannot retain authority after its parent is revoked.
                issuing=issuer.GrantPermissionAsync(tokens[admin],pending with{ExpectedEpoch=pending.ExpectedEpoch+1});
                await WaitForLock("fc2grant-issuer","X");Check(!issuing.IsCompleted,"new grant waits behind uncommitted parent revocation");
            }
            finally{await tx.CommitAsync();}
            await revoking;await Denied(()=>issuing??throw new InvalidOperationException("Missing issuer."),"grant revalidates parent after revocation commits first");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant WHERE GrantId=@grant",("@grant",pending.GrantId)))==0,"revoked parent produces no descendant grant");
        }
        await Exec("DROP TRIGGER security.Fixture_GrantBarrier");
        // Revoking a parent never resurrects its descendants via a replacement root/branch.
        await service.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),delegable.GrantId,await Epoch(),"revoke delegated branch");
        // leaf still has rollback grant, so inspect the issued descendant's authority through its specific delegation path.
        await Denied(async()=>await service.GrantPermissionAsync(tokens[member],(await Request(leaf,delegable.GrantId))),"revoked delegation parent cannot create new grants");
        var revokedTarget=Guid.NewGuid();
        await Exec("""
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
                VALUES(@op,@p,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
            INSERT security.PrincipalRevocation(PrincipalId,OperationId) VALUES(@target,@op);
            """,("@op",revokedTarget),("@p",admin),("@target",leaf));
        await Denied(async()=>await service.GrantPermissionAsync(tokens[admin],(await Request(leaf))),"revoked recipient cannot receive new authority");
        await service.RevokeGrantAsync(tokens[admin],Guid.NewGuid(),management,await Epoch(),"revoke access management");
        await Denied(()=>service.GrantPermissionAsync(tokens[admin],request),"completed grant retry requires current management authority");
        await Denied(()=>service.ReadOperationResultAsync(tokens[admin],request.OperationId),"revoked administrator cannot retrieve stored grant result");
        return count;

        Task<bool> Read(Guid actor,string permission)=>service.ReadAsync(tokens[actor],permission,scope,(_,_,_)=>Task.FromResult(true));
        async Task<long> Epoch()=>Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState WHERE Singleton=1"));
        async Task<Guid> Root(Guid grantee,string permission,AuthorizationScope resource,bool use,bool delegateAllowed,DateTimeOffset? from=null,DateTimeOffset? until=null)
        {
            var id=Guid.NewGuid();await Exec("""
                INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,SiteId,MachineId,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                VALUES(@id,@p,@p,'bootstrap',@permission,@kind,@site,@machine,@use,@delegate,@from,@until);
                """,("@id",id),("@p",grantee),("@permission",permission),("@kind",resource.Kind),("@site",(object?)resource.SiteId??DBNull.Value),
                ("@machine",(object?)resource.MachineId??DBNull.Value),("@use",use),("@delegate",delegateAllowed),("@from",from??now.AddDays(-1)),("@until",until??now.AddDays(1)));return id;
        }
        async Task Barrier(SqlConnection connection,SqlTransaction transaction)
        {await using var q=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:grant:test-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture barrier failed.',1;",connection,transaction);await q.ExecuteNonQueryAsync();}
        async Task WaitForLock(string app,string mode)
        {
            for(var i=0;i<150;i++)
            {
                if(Convert.ToInt32(await Scalar("""
                    SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id
                    WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode
                    """,("@app",app),("@mode",mode)))>0)return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("Expected lock wait was not observed: "+app+"/"+mode);
        }
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task ForeignKey(Func<Task> action,string name){try{await action();}catch(SqlException e)when(e.Number==547){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Immutable(string sql,string name){try{await Exec(sql);}catch(SqlException e)when(e.Number==51030){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
    }
    private static void Add(SqlCommand q,params (string Name,object Value)[] values)
    {
        foreach(var(name,value)in values)
            if(value is byte[] b)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=b;
            else if(name is "@site" or "@machine")q.Parameters.Add(name,SqlDbType.UniqueIdentifier).Value=value;
            else q.Parameters.AddWithValue(name,value);
    }
}
