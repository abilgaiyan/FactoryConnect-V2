using System.Data;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class BootstrapAcceptance
{
    private const string Issuer="https://idp.fixture/tenant";
    internal static int PureChecks()
    {
        var count=0;using var a=RSA.Create(3072);using var b=RSA.Create(3072);using var alias=RSA.Create(3072);
        var now=new DateTimeOffset(2026,11,6,0,0,0,TimeSpan.Zero);var installation=Guid.NewGuid();
        var fixture=Fixture(a,b,installation,now);var trust=fixture.Trust;var p=fixture.Payload;
        trust.Verify(p,Sign(p,a,b),now);Check(true,"two independent custodians verify RSA-PSS bootstrap");
        var golden=new BootstrapPayload(Guid.Parse("55555555-5555-5555-5555-555555555555"),Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),Guid.Parse("22222222-2222-2222-2222-222222222222"),now,now.AddMinutes(10),now.AddDays(30),0,
            Enumerable.Range(0,32).Select(i=>(byte)i).ToArray(),Enumerable.Repeat((byte)6,32).ToArray(),"initialization");
        Check(Convert.ToHexString(SHA256.HashData(BootstrapTrustManifest.Canonicalize(golden))).ToLowerInvariant()=="539fce88b0e55479613804ec4c5ae5fc537312172c03863e52e15c205fb4ee9d","bootstrap canonical v1 golden digest");
        Check(BootstrapTrustManifest.Canonicalize(golden).SequenceEqual(BootstrapTrustManifest.Canonicalize(golden with{IssuedAt=golden.IssuedAt.ToOffset(TimeSpan.FromHours(5.5))})),"bootstrap instants use canonical UTC");
        Denied(()=>trust.Verify(p,Sign(p,a,b).Take(1).ToArray(),now),"single custodian cannot bootstrap");
        var proofs=Sign(p,a,b);Denied(()=>trust.Verify(p with{Reason="changed"},proofs,now),"signature binds exact request content");
        Denied(()=>trust.Verify(p,[proofs[0],proofs[0]],now),"duplicate signature cannot satisfy threshold");
        var bad=proofs[1].Signature.ToArray();bad[0]^=1;
        Denied(()=>trust.Verify(p,[proofs[0],new(p.ExecutorKeyId,bad)],now),"invalid second signature rejected");
        var expired=p with{IssuedAt=now.AddMinutes(-10),ExpiresAt=now};
        Denied(()=>trust.Verify(expired,Sign(expired,a,b),now),"expired signed bootstrap rejected");
        var future=p with{IssuedAt=now.AddMinutes(1),ExpiresAt=now.AddMinutes(6)};
        Denied(()=>trust.Verify(future,Sign(future,a,b),now),"future-dated signed bootstrap rejected");
        var other=p with{InstallationId=Guid.NewGuid()};Denied(()=>trust.Verify(other,Sign(other,a,b),now),"signature does not override installation binding");
        var wrongHash=p with{TrustManifestHash=new byte[32]};Denied(()=>trust.Verify(wrongHash,Sign(wrongHash,a,b),now),"trust manifest hash mismatch rejected");
        var self=p with{AdministratorSubject="custodian-a"};Denied(()=>trust.Verify(self,Sign(self,a,b),now),"administrator cannot approve their own bootstrap");
        var evilIssuer=p with{AdministratorIssuer="https://untrusted.fixture"};Denied(()=>trust.Verify(evilIssuer,Sign(evilIssuer,a,b),now),"administrator issuer must be pinned");
        var unknown=p with{ApproverKeyId=Guid.NewGuid()};Denied(()=>trust.Verify(unknown,Sign(unknown,a,b),now),"request cannot choose an unpinned public key");
        Check(trust.Hash.SequenceEqual(new BootstrapTrustManifest(fixture.File with{Keys=fixture.File.Keys.Reverse().ToArray()}).Hash),"public manifest order is canonical");
        var first=fixture.File.Keys[0];var extra=first with{KeyId=Guid.NewGuid(),PublicKeySpki=alias.ExportSubjectPublicKeyInfo()};
        var aliasTrust=new BootstrapTrustManifest(fixture.File with{Keys=[..fixture.File.Keys,extra]});
        var samePerson=p with{ExecutorKeyId=extra.KeyId,TrustManifestHash=aliasTrust.Hash};
        Denied(()=>aliasTrust.Verify(samePerson,Sign(samePerson,a,alias),now),"two keys held by one declared custodian do not satisfy threshold");
        Invalid(()=>new BootstrapTrustManifest(fixture.File with{Keys=[first,fixture.File.Keys[1] with{PublicKeySpki=first.PublicKeySpki}]}),"same RSA key under two identities rejected");
        using var exponentKey=RSA.Create();var exponentParameters=a.ExportParameters(false);exponentParameters.Exponent=[3];exponentKey.ImportParameters(exponentParameters);
        Invalid(()=>new BootstrapTrustManifest(fixture.File with{Keys=[first,fixture.File.Keys[1] with{PublicKeySpki=exponentKey.ExportSubjectPublicKeyInfo()}]}),"non-profile RSA exponent rejected");
        using var weak=RSA.Create(2048);
        Invalid(()=>new BootstrapTrustManifest(fixture.File with{Keys=[first,fixture.File.Keys[1] with{PublicKeySpki=weak.ExportSubjectPublicKeyInfo()}]}),"weak bootstrap RSA key rejected");
        Invalid(()=>BootstrapTrustManifest.Canonicalize(p with{Profile="recovery-v1"}),"initial-bootstrap profile cannot act as recovery");
        Invalid(()=>BootstrapTrustManifest.Canonicalize(p with{ExpiresAt=p.IssuedAt.AddMinutes(16)}),"overlong bootstrap capability rejected");
        Invalid(()=>BootstrapTrustManifest.Canonicalize(p with{AdministratorSubject="\ud800"}),"lossy external identity encoding rejected");
        Invalid(()=>BootstrapTrustManifest.Canonicalize(p with{Nonce=new byte[31]}),"nonce length is fixed");
        return count;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        void Denied(Action action,string name){try{action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        void Invalid(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new InvalidOperationException(name);}
    }

    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;using var a=RSA.Create(3072);using var b=RSA.Create(3072);
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var fixture=Fixture(a,b,target.InstallationId,now);var p=fixture.Payload;var trust=fixture.Trust;
        var service=new SqlBootstrapAuthority(target,trust);var signed=new SignedBootstrapRequest(p,Sign(p,a,b));
        var expired=p with{IssuedAt=now.AddMinutes(-10),ExpiresAt=now.AddMinutes(-1)};
        await Denied(()=>service.ApplyAsync(new(expired,Sign(expired,a,b))),"expired bootstrap changes no authority");
        var signature=signed.Signatures[1].Signature.ToArray();signature[0]^=1;
        await Denied(()=>service.ApplyAsync(new(p,[signed.Signatures[0],new(p.ExecutorKeyId,signature)])),"invalid custody proof changes no authority");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.BootstrapTrust"))==0,"failed proof does not bind trust");
        Check(Convert.ToBoolean(await Scalar("SELECT IsSealed FROM security.InstallationState")),"failed bootstrap remains sealed");
        var stale=p with{ExpectedEpoch=1};await Conflict(()=>service.ApplyAsync(new(stale,Sign(stale,a,b))),"bootstrap expected epoch checked");
        var wrongServer=new SqlBootstrapAuthority(target with{ExpectedServer="wrong-server"},trust);
        await Denied(()=>wrongServer.ApplyAsync(signed),"bootstrap refuses wrong SQL server identity");
        await Exec("CREATE TRIGGER security.Fixture_BootstrapFailure ON security.SecurityAudit AFTER INSERT AS BEGIN THROW 51101,'Deliberate bootstrap audit failure.',1; END;");
        try{await service.ApplyAsync(signed);throw new InvalidOperationException("Expected audit failure.");}
        catch(SqlException e)when(e.Number==51101){Check(true,"bootstrap audit failure aborts whole ceremony");}
        await Exec("DROP TRIGGER security.Fixture_BootstrapFailure");
        Check(Convert.ToInt32(await Scalar("SELECT (SELECT COUNT(*) FROM security.Principal)+(SELECT COUNT(*) FROM security.PermissionGrant)+(SELECT COUNT(*) FROM security.SecurityOperation)+(SELECT COUNT(*) FROM security.InitialBootstrap)+(SELECT COUNT(*) FROM security.BootstrapTrust)+(SELECT COUNT(*) FROM security.BootstrapCustodianKey)"))==0,"bootstrap principal grants trust proofs and result roll back together");
        Check(Convert.ToBoolean(await Scalar("SELECT IsSealed FROM security.InstallationState"))&&Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==0,"failed bootstrap preserves sealed epoch zero");
        var receipts=await Task.WhenAll(service.ApplyAsync(signed),service.ApplyAsync(signed));
        Check(receipts[0].SequenceEqual(receipts[1]),"concurrent exact bootstrap retries return identical receipt");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.InitialBootstrap"))==1,"one immutable initial ceremony");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SecurityAudit"))==1,"bootstrap retry appends one audit");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant WHERE UseAllowed=1"))==2,"bootstrap use permissions limited to security management and audit");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.PermissionGrant WHERE UseAllowed=0 AND CanDelegate=1"))==11,"business permissions are delegable authority not implicit use");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.SessionAuthority"))==0,"bootstrap does not fabricate an authenticated login session");
        var token=RandomNumberGenerator.GetBytes(32);
        await Exec("INSERT security.SessionAuthority VALUES(@token,@p,DATEADD(second,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",token),("@p",p.PrincipalId));
        var auth=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        Check(await auth.ReadAsync(token,"security.access.manage",AuthorizationScope.Installation,(_,_,_)=>Task.FromResult(true)),"bootstrapped administrator passes current scoped authority");
        await Denied(()=>auth.ReadAsync(token,"configuration.edit",AuthorizationScope.Installation,(_,_,_)=>Task.FromResult(true)),"bootstrapped administrator cannot implicitly edit configuration");
        var changed=p with{Reason="different reason"};await Conflict(()=>service.ApplyAsync(new(changed,Sign(changed,a,b))),"bootstrap operation fingerprint is immutable");
        var second=p with{OperationId=Guid.NewGuid(),PrincipalId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32)};
        await Denied(()=>service.ApplyAsync(new(second,Sign(second,a,b))),"new ceremony cannot reset initialized installation");
        await Exec("UPDATE security.InstallationState SET IsSealed=1,AuthorityEpoch=0 WHERE Singleton=1");
        await Denied(()=>service.ApplyAsync(new(second,Sign(second,a,b))),"manually resealing cannot re-enable initial bootstrap");
        await Exec("UPDATE security.InstallationState SET IsSealed=0,AuthorityEpoch=1 WHERE Singleton=1");
        await Immutable("DELETE security.InitialBootstrap","bootstrap ceremony history cannot be erased");
        await Immutable("UPDATE security.BootstrapTrust SET InstallationId=NEWID()","bootstrap trust cannot be rewritten");
        var differentTrust=new BootstrapTrustManifest(fixture.File with{AllowedAdministratorIssuer="https://other.fixture/tenant"});
        var moved=p with{AdministratorIssuer=differentTrust.AllowedAdministratorIssuer,TrustManifestHash=differentTrust.Hash};
        await Denied(()=>new SqlBootstrapAuthority(target,differentTrust).ApplyAsync(new(moved,Sign(moved,a,b))),"replacement host manifest cannot overwrite pinned SQL trust");
        // Privileged fixture simulates a separately authorized custodian revocation; no online revocation API is shipped here.
        var revokeOp=Guid.NewGuid();await Exec("""
            INSERT security.SecurityOperation(OperationId,ActorPrincipalId,RequestBytes,RequestHash,ScopeKind,ResultBytes)
            VALUES(@op,@p,0x7B7D,HASHBYTES('SHA2_256',0x7B7D),'I',0x7B7D);
            INSERT security.BootstrapCustodianRevocation(KeyId,OperationId) VALUES(@key,@op);
            """,("@op",revokeOp),("@p",p.PrincipalId),("@key",p.ApproverKeyId));
        await Denied(()=>service.ApplyAsync(signed),"completed bootstrap replay rechecks custodian revocation");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.InitialBootstrap"))==1,"rejected retries never create another ceremony");
        return count;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Immutable(string sql,string name){try{await Exec(sql);}catch(SqlException e)when(e.Number==51030){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);foreach(var(name,value)in values)q.Parameters.AddWithValue(name,value);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string Name,object Value)[] values){await using var q=new SqlCommand(sql,c);foreach(var(name,value)in values)q.Parameters.AddWithValue(name,value);await q.ExecuteNonQueryAsync();}
    }

    internal static async Task<int> CompetingRequests(InstallationTarget target)
    {
        using var a=RSA.Create(3072);using var b=RSA.Create(3072);await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        await using var q=new SqlCommand("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')",c);var now=(DateTimeOffset)(await q.ExecuteScalarAsync())!;
        var fixture=Fixture(a,b,target.InstallationId,now);var first=fixture.Payload;
        var second=first with{OperationId=Guid.NewGuid(),PrincipalId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32)};
        var service=new SqlBootstrapAuthority(target,fixture.Trust);
        var results=await Task.WhenAll(Attempt(first),Attempt(second));
        if(results.Count(x=>x)==1){Console.WriteLine("PASS: competing bootstrap requests initialize exactly one administrator");return 1;}
        throw new InvalidOperationException("Competing bootstrap requests did not have exactly one winner.");
        async Task<bool> Attempt(BootstrapPayload p){try{await service.ApplyAsync(new(p,Sign(p,a,b)));return true;}catch(SecurityDeniedException){return false;}}
    }
    private sealed record TestFixture(BootstrapTrustFile File,BootstrapTrustManifest Trust,BootstrapPayload Payload);
    private static TestFixture Fixture(RSA first,RSA second,Guid installation,DateTimeOffset now)
    {
        var keys=new[]{new BootstrapCustodian(Guid.NewGuid(),Guid.NewGuid(),Issuer,"custodian-a",first.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(2)),
            new BootstrapCustodian(Guid.NewGuid(),Guid.NewGuid(),Issuer,"custodian-b",second.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(2))};
        var file=new BootstrapTrustFile(installation,Issuer,keys);var trust=new BootstrapTrustManifest(file);
        return new(file,trust,new(installation,Guid.NewGuid(),Guid.NewGuid(),Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,
            keys[0].KeyId,keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),trust.Hash,"initialization"));
    }
    private static BootstrapSignature[] Sign(BootstrapPayload p,RSA first,RSA second)
    {var bytes=BootstrapTrustManifest.Canonicalize(p);return [new(p.ApproverKeyId,first.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),new(p.ExecutorKeyId,second.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))];}
}
