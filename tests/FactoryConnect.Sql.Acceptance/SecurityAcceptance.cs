using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using FactoryConnect.Sql.Inventory;
using Microsoft.Data.SqlClient;

internal static class SecurityAcceptance
{
    internal static async Task<int> Run(string connectionString,Guid installationId)
    {
        var count=0;
        var admin=Guid.NewGuid();var member=Guid.NewGuid();var descendant=Guid.NewGuid();var stranger=Guid.NewGuid();var runtime=Guid.NewGuid();
        var adminToken=RandomNumberGenerator.GetBytes(32);var memberToken=RandomNumberGenerator.GetBytes(32);
        var newMemberToken=RandomNumberGenerator.GetBytes(32);var descendantToken=RandomNumberGenerator.GetBytes(32);
        var strangerToken=RandomNumberGenerator.GetBytes(32);var runtimeToken=RandomNumberGenerator.GetBytes(32);var expiredToken=RandomNumberGenerator.GetBytes(32);
        var site=Guid.Parse("11111111-1111-1111-1111-111111111111");var machine=Guid.Parse("33333333-3333-3333-3333-333333333333");
        var scope=AuthorizationScope.Machine(site,machine);
        var readerString=new SqlConnectionStringBuilder(connectionString){ApplicationName="fc2sec-reader"}.ConnectionString;
        var writerString=new SqlConnectionStringBuilder(connectionString){ApplicationName="fc2sec-writer"}.ConnectionString;
        var reader=new SqlSecurityAuthority(readerString,installationId);var writer=new SqlSecurityAuthority(writerString,installationId);
        await using var c=new SqlConnection(connectionString);await c.OpenAsync();
        var fixtureTime=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        Check(Convert.ToBoolean(await Scalar("SELECT IsSealed FROM security.InstallationState WHERE Singleton=1")),"new security installation is sealed");
        foreach(var p in new[]{admin,member,descendant,stranger,runtime})
            await Exec("INSERT security.Principal(PrincipalId,Kind,IssuerBytes,SubjectBytes) VALUES(@id,@kind,0x66697874757265,@subject)",
                ("@id",p),("@kind",p==runtime?"R":"H"),("@subject",p.ToByteArray()));
        foreach(var (p,token) in new[]{(admin,adminToken),(member,memberToken),(member,newMemberToken),(descendant,descendantToken),(stranger,strangerToken),(runtime,runtimeToken)})
            await Session(p,token,false);
        await Session(member,expiredToken,true);
        await Denied(()=>Read(memberToken,scope),"sealed installation denies otherwise valid session");
        // Privileged fixture seeding only. No runtime/API bootstrap path exists in this increment.
        await Exec("UPDATE security.InstallationState SET IsSealed=0 WHERE Singleton=1");
        var adminGrant=Guid.NewGuid();var parent=Guid.NewGuid();var child=Guid.NewGuid();var grandchild=Guid.NewGuid();
        await Grant(adminGrant,admin,admin,null,"security.access.manage",AuthorizationScope.Installation,true,true);
        await Grant(parent,admin,admin,null,"configuration.read",AuthorizationScope.Site(site),false,true);
        await Grant(child,member,admin,parent,"configuration.read",scope,true,true);
        await Grant(grandchild,descendant,member,child,"configuration.read",scope,true,false);
        await Grant(Guid.NewGuid(),runtime,admin,null,"configuration.read",scope,true,false);
        await Grant(Guid.NewGuid(),stranger,admin,null,"configuration.edit",scope,true,false,startDays:1);
        await Grant(Guid.NewGuid(),stranger,admin,null,"configuration.submit",scope,true,false,expiryDays:-1,startDays:-2);
        Check(await Read(memberToken,scope)==1,"scoped direct grant authorizes");
        Check(await Read(descendantToken,scope)==1,"valid delegation ancestry authorizes");
        await Denied(()=>Read(strangerToken,scope),"ungranted principal denied");
        await Denied(()=>Read(runtimeToken,scope),"runtime principal cannot use human session authority");
        await Denied(()=>reader.ReadAsync(strangerToken,"configuration.edit",scope,(_,_,_)=>Task.FromResult(1)),"future grant is not active early");
        await Denied(()=>reader.ReadAsync(strangerToken,"configuration.submit",scope,(_,_,_)=>Task.FromResult(1)),"expired grant is not active");
        var runtimeAncestor=Guid.NewGuid();
        await Grant(runtimeAncestor,runtime,admin,null,"audit.read",scope,false,true);
        await Grant(Guid.NewGuid(),stranger,runtime,runtimeAncestor,"audit.read",scope,true,false);
        await Denied(()=>reader.ReadAsync(strangerToken,"audit.read",scope,(_,_,_)=>Task.FromResult(1)),"runtime ancestor cannot delegate human permission");
        await Denied(()=>Read(expiredToken,scope),"expired session denied despite active grant");
        await Denied(()=>Read(memberToken,AuthorizationScope.Site(site)),"machine permission does not widen to site");
        await Denied(()=>Read(memberToken,AuthorizationScope.Machine(Guid.Parse("22222222-2222-2222-2222-222222222222"),machine)),"wrong machine-site pairing denied");
        await Denied(()=>reader.ReadAsync(adminToken,"configuration.edit",scope,(_,_,_)=>Task.FromResult(1)),"security administrator has no implicit edit permission");
        // Protected inventory uses the same transaction and revocation gate as authorization.
        var siteReadGrant=Guid.NewGuid();
        await Grant(siteReadGrant,admin,admin,null,"configuration.read",AuthorizationScope.Site(site),true,false);
        var inventory=new SqlProtectedMachineInventoryReader(readerString,installationId);
        var siteMachines=await inventory.ReadSiteAsync(adminToken,site);
        Check(siteMachines.Any(item=>item.MachineId==machine && item.SiteId==site) &&
            siteMachines.All(item=>item.SiteId==site) &&
            siteMachines.Select(item=>item.MachineId).Distinct().Count()==siteMachines.Count,
            "protected inventory returns exact authorized site machines without cross-site leakage");
        await Denied(()=>inventory.ReadSiteAsync(adminToken,Guid.Parse("22222222-2222-2222-2222-222222222222")),
            "protected inventory denies unauthorized site");
        await Denied(()=>inventory.ReadSiteAsync(strangerToken,site),
            "protected inventory denies missing permission");
        // Disposable SQL 100-machine fixture. Insert in one set-based statement; no
        // machine-by-machine client writes and no V1 historical data dependencies.
        var otherSite=Guid.Parse("22222222-2222-2222-2222-222222222222");
        var existingOther=Convert.ToInt32(await Scalar(
            "SELECT COUNT(*) FROM [master].Machine WHERE SiteId=@site",("@site",otherSite)));
        await Exec("""
            ;WITH numbers AS (
                SELECT 1 AS n UNION ALL SELECT n+1 FROM numbers WHERE n<100
            )
            INSERT [master].Machine(MachineId,SiteId,DisplayName)
            SELECT NEWID(),@site,CONCAT(N'Load machine ',n)
            FROM numbers OPTION(MAXRECURSION 100);
            """,("@site",otherSite));
        await Grant(Guid.NewGuid(),admin,admin,null,"configuration.read",
            AuthorizationScope.Site(otherSite),true,false);
        // Instrument the actual authorized SQL path: a 100-machine inventory read
        // must remain one bounded data command rather than an N+1 loop.
        var observed = new List<(string? Result, int Rows, int Commands, double ElapsedMs)>();
        using var inventoryListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.V2.Inventory",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "inventory.read-site") return;
                observed.Add((
                    activity.GetTagItem("inventory.result") as string,
                    Convert.ToInt32(activity.GetTagItem("inventory.rows")),
                    Convert.ToInt32(activity.GetTagItem("inventory.reader.commands")),
                    Convert.ToDouble(activity.GetTagItem("inventory.elapsed.ms"))));
            }
        };
        ActivitySource.AddActivityListener(inventoryListener);
        var loadStart=Stopwatch.GetTimestamp();
        var loaded=await inventory.ReadSiteAsync(adminToken,otherSite);
        var loadMs=Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
        Check(observed.Count==1 && observed[0].Result=="completed" &&
            observed[0].Rows==loaded.Count && observed[0].Commands==1 &&
            observed[0].ElapsedMs>=0,
            "100-machine inventory emits one bounded data-command diagnostic span");
        Check(loaded.Count==existingOther+100 && loaded.All(item=>item.SiteId==otherSite) &&
            loaded.Select(item=>item.MachineId).Distinct().Count()==loaded.Count,
            "100-machine protected inventory remains site-scoped and complete");
        Check((await inventory.ReadSiteAsync(adminToken,site)).All(item=>item.SiteId==site),
            "100-machine fixture cannot leak into other authorized site");
        var concurrentStart=Stopwatch.GetTimestamp();
        var concurrentInventory=await Task.WhenAll(
            Enumerable.Range(0,8).Select(_=>inventory.ReadSiteAsync(adminToken,otherSite)));
        var concurrentMs=Stopwatch.GetElapsedTime(concurrentStart).TotalMilliseconds;
        Check(concurrentInventory.All(items=>items.Count==loaded.Count &&
            items.All(item=>item.SiteId==otherSite)),
            "eight concurrent protected reads return the same 100-machine site cut");
        Console.WriteLine($"MEASURE: synthetic inventory machines={loaded.Count} singleMs={loadMs:F2} " +
            $"concurrentReaders=8 concurrentWallMs={concurrentMs:F2}; CI timing is diagnostic, not an SLA");


        var badScope=Guid.NewGuid();
        await Grant(badScope,stranger,admin,parent,"configuration.read",AuthorizationScope.Installation,true,false);
        await Denied(()=>Read(strangerToken,scope),"grant wider than parent is wholly ineligible");
        var tooLong=Guid.NewGuid();
        await Grant(tooLong,stranger,admin,parent,"configuration.read",scope,true,false,expiryDays:30);
        await Denied(()=>Read(strangerToken,scope),"child expiry beyond parent ceiling denied");
        var noDelegateParent=Guid.NewGuid();var noDelegateChild=Guid.NewGuid();
        await Grant(noDelegateParent,admin,admin,null,"commissioning.read",scope,false,false);
        await Grant(noDelegateChild,stranger,admin,noDelegateParent,"commissioning.read",scope,true,false);
        await Denied(()=>reader.ReadAsync(strangerToken,"commissioning.read",scope,(_,_,_)=>Task.FromResult(1)),"nondelegable ancestor denies child");
        await Denied(()=>new SqlSecurityAuthority(readerString,Guid.NewGuid()).ReadAsync(memberToken,"configuration.read",scope,(_,_,_)=>Task.FromResult(1)),"wrong installation identity denied");
        await Conflict(()=>writer.RevokeGrantAsync(adminToken,Guid.NewGuid(),grandchild,99,"stale expected epoch"),"stale authority epoch rejected");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation"))==0,"denied/stale operation creates no authority change");

        // Read first: observe the writer actually waiting for the security gate, then release the read.
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight=reader.ReadAsync(descendantToken,"configuration.read",scope,async(_,_,ct)=>{entered.SetResult();await release.Task.WaitAsync(ct);return 7;});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revokeId=Guid.NewGuid();var revoking=writer.RevokeGrantAsync(adminToken,revokeId,child,0,"read first race");
        try {await WaitForLock("fc2sec-writer","X");Check(!revoking.IsCompleted,"revocation waits for protected read completion");}
        finally {release.TrySetResult();}
        Check(await inFlight==7,"already protected read finishes before revocation commit");
        var revokedResult=await revoking;
        await Denied(()=>Read(descendantToken,scope),"ancestor revocation invalidates descendants after commit");
        await Denied(()=>Read(memberToken,scope),"revoked direct grant denied");
        var replay=await writer.RevokeGrantAsync(adminToken,revokeId,child,0,"read first race");
        Check(replay.SequenceEqual(revokedResult),"exact replay retains stored result despite epoch advancement");
        Check((await writer.ReadOperationResultAsync(adminToken,revokeId)).SequenceEqual(revokedResult),"authorized stored-result retrieval");
        await Conflict(()=>writer.RevokeGrantAsync(adminToken,revokeId,child,0,"different payload"),"conflicting operation fingerprint rejected");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit"))==1,"replay produces one durable audit event");

        var replacement=Guid.NewGuid();await Grant(replacement,member,admin,parent,"configuration.read",scope,true,false);
        Check(await Read(newMemberToken,scope)==1,"new independent grant authorizes member");
        await Denied(()=>Read(descendantToken,scope),"regrant does not resurrect descendants of revoked grant");

        // Revoke first: block the actual kernel at its audit insert while it owns exclusive authority.
        await Exec("""
            CREATE TRIGGER security.Fixture_CommitBarrier ON security.SecurityAudit AFTER INSERT AS
            BEGIN
                DECLARE @r int;
                EXEC @r=sys.sp_getapplock @Resource='fc2:test:commit-barrier',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
                IF @r<0 THROW 51020,'Fixture barrier timed out.',1;
            END;
            """);
        await using(var barrierConnection=new SqlConnection(connectionString))
        {
            await barrierConnection.OpenAsync();await using var barrier=(SqlTransaction)await barrierConnection.BeginTransactionAsync();
            await using(var hold=new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='fc2:test:commit-barrier',@LockMode='Exclusive',@LockOwner='Transaction'; IF @r<0 THROW 51020,'Fixture lock failed.',1;",barrierConnection,barrier))await hold.ExecuteNonQueryAsync();
            var sessionOp=Guid.NewGuid();var revokeSession=writer.RevokeSessionAsync(adminToken,sessionOp,newMemberToken,1,"revoke first race");
            Task<int>? waitingRead=null;
            try
            {
                await WaitForLock("fc2sec-writer","S");
                waitingRead=Read(newMemberToken,scope);await WaitForLock("fc2sec-reader","S");
                Check(!waitingRead.IsCompleted,"new read waits behind uncommitted revocation");
            }
            finally{await barrier.CommitAsync();}
            await revokeSession;
            if(waitingRead is null)throw new InvalidOperationException("Race read did not start.");
            await Denied(()=>waitingRead,"read revalidates fresh committed session revocation after gate wait");
        }
        await Exec("DROP TRIGGER security.Fixture_CommitBarrier");
        Check(await Read(memberToken,scope)==1,"revoking one session does not revoke the principal's independent session");

        // Audit failure must roll back operation, revocation and authority epoch together.
        await Exec("CREATE TRIGGER security.Fixture_AuditFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51100,'Deliberate audit failure.',1; END;");
        var rollbackOp=Guid.NewGuid();
        try{await writer.RevokeGrantAsync(adminToken,rollbackOp,replacement,2,"atomic audit test");throw new InvalidOperationException("Expected failure.");}
        catch(SqlException error) when(error.Number==51100){Check(true,"audit failure rejects whole security mutation");}
        await Exec("DROP TRIGGER security.Fixture_AuditFailure");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityOperation WHERE OperationId=@id",("@id",rollbackOp)))==0,"failed operation result rolled back");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.GrantRevocation WHERE GrantId=@id",("@id",replacement)))==0,"failed grant revocation rolled back");
        Check(Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==2,"failed authority epoch rolled back");
        Check(await Read(memberToken,scope)==1,"failed revocation leaves prior grant usable");

        var duplicateOp=Guid.NewGuid();var copies=await Task.WhenAll(
            writer.RevokeGrantAsync(adminToken,duplicateOp,replacement,2,"concurrent retry"),
            writer.RevokeGrantAsync(adminToken,duplicateOp,replacement,2,"concurrent retry"));
        Check(copies[0].SequenceEqual(copies[1]),"concurrent exact retries return one result");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit WHERE OperationId=@id",("@id",duplicateOp)))==1,"concurrent retry appends one audit");
        await Denied(()=>Read(memberToken,scope),"committed grant revocation denies subsequent reads");
        await Immutable("UPDATE security.PermissionGrant SET CanDelegate=1","grant authority is append-only");
        await Immutable("DELETE security.SecurityAudit","security audit is append-only");
        await Immutable("UPDATE security.SessionAuthority SET ExpiresAt=DATEADD(day,30,ExpiresAt)","session credentials cannot be rewritten");
        var selfRevoke=Guid.NewGuid();await writer.RevokeSessionAsync(adminToken,selfRevoke,adminToken,3,"sign out administrator");
        await Denied(()=>writer.ReadOperationResultAsync(adminToken,revokeId),"revoked session cannot retrieve completed results");
        await Denied(()=>writer.RevokeSessionAsync(adminToken,selfRevoke,adminToken,3,"sign out administrator"),"completed replay cannot bypass session revocation");
        Check(Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==4,"only committed new operations advance authority epoch");
        return count;

        Task<int> Read(byte[] token,AuthorizationScope resource)=>reader.ReadAsync(token,"configuration.read",resource,(_,_,_)=>Task.FromResult(1));
        async Task Session(Guid p,byte[] token,bool expired)
            =>await Exec("INSERT security.SessionAuthority VALUES(@token,@p,DATEADD(day,-2,SYSUTCDATETIME()),DATEADD(day,@days,SYSUTCDATETIME()),N'fixture-only',0)",("@token",token),("@p",p),("@days",expired?-1:2));
        async Task Grant(Guid id,Guid grantee,Guid grantor,Guid? parentId,string permission,AuthorizationScope resource,bool use,bool delegation,int expiryDays=7,int startDays=-1)
            =>await Exec("""
                INSERT security.PermissionGrant(GrantId,GranteeId,GrantorId,ParentGrantId,RootOrigin,Permission,ScopeKind,SiteId,MachineId,UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                VALUES(@id,@to,@by,@parent,@origin,@permission,@kind,@site,@machine,@use,@delegate,@from,@until);
                """,("@id",id),("@to",grantee),("@by",grantor),("@parent",(object?)parentId??DBNull.Value),("@origin",parentId.HasValue?DBNull.Value:"bootstrap"),
                ("@permission",permission),("@kind",resource.Kind),("@site",(object?)resource.SiteId??DBNull.Value),("@machine",(object?)resource.MachineId??DBNull.Value),("@use",use),("@delegate",delegation),("@from",fixtureTime.AddDays(startDays)),("@until",fixtureTime.AddDays(expiryDays)));
        async Task WaitForLock(string app,string mode)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while(!timeout.IsCancellationRequested)
            {
                var n=Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE l.resource_type='APPLICATION' AND l.request_status='WAIT' AND l.request_mode=@mode AND s.program_name=@app",("@mode",mode),("@app",app)));
                if(n>0)return;await Task.Delay(25,timeout.Token);
            }
            throw new InvalidOperationException("Expected real SQL gate wait was not observed.");
        }
        async Task Denied(Func<Task> action,string label)
        {try{await action();}catch(SecurityDeniedException){Check(true,label);return;}throw new InvalidOperationException("Expected denial: "+label);}
        async Task Conflict(Func<Task> action,string label)
        {try{await action();}catch(SecurityConflictException){Check(true,label);return;}throw new InvalidOperationException("Expected conflict: "+label);}
        async Task Immutable(string sql,string label)
        {try{await Exec(sql);}catch(SqlException error)when(error.Number==51030){Check(true,label);return;}throw new InvalidOperationException(label);}
        void Check(bool success,string label){if(!success)throw new InvalidOperationException(label);count++;Console.WriteLine("PASS: "+label);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values)
        {await using var q=new SqlCommand(sql,c);Add(q,values);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values)
        {await using var q=new SqlCommand(sql,c);Add(q,values);await q.ExecuteNonQueryAsync();}
        static void Add(SqlCommand q,params (string Name,object Value)[] values)
        {
            foreach(var(name,value)in values)
                if(name is "@parent" or "@site" or "@machine")q.Parameters.Add(name,SqlDbType.UniqueIdentifier).Value=value;
                else if(value is DateTimeOffset instant)q.Parameters.Add(name,SqlDbType.DateTimeOffset).Value=instant;
                else q.Parameters.AddWithValue(name,value);
        }
    }
}
