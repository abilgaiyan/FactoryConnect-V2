using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FactoryConnect.Sql;
using FactoryConnect.Sql.Security;
using Microsoft.Data.SqlClient;

internal static class RuntimeCertificateAcceptance
{
    const string Issuer="https://certificate.fixture/tenant";
    internal sealed class Fixture:IDisposable
    {
        internal readonly RSA A=RSA.Create(3072),B=RSA.Create(3072),Ca=RSA.Create(3072),Runtime=RSA.Create(3072),Other=RSA.Create(3072);
        internal readonly X509Certificate2 Root;
        internal readonly BootstrapTrustFile File;
        internal readonly BootstrapTrustManifest Trust;
        internal readonly RuntimeCredentialPayload Payload;
        internal readonly byte[] Certificate;
        internal Fixture(Guid installation,Guid actor,DateTimeOffset now,long epoch)
        {
            BootstrapCustodian Key(RSA rsa,string subject)=>new(Guid.NewGuid(),Guid.NewGuid(),Issuer,subject,rsa.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(10));
            File=new(installation,Issuer,[Key(A,"custodian-a"),Key(B,"custodian-b")]);Trust=new(File);
            Root=CreateRoot(Ca,now);Certificate=Leaf(Runtime,now);
            Payload=new(installation,Guid.NewGuid(),actor,Issuer,"certificate-manager",Guid.NewGuid(),"https://certificate.fixture/edge","edge-one","enroll",0,
                Runtime.ExportSubjectPublicKeyInfo(),now.AddMinutes(-2),now.AddDays(60),File.Keys[0].KeyId,File.Keys[1].KeyId,now.AddSeconds(-1),now.AddMinutes(5),epoch,
                RandomNumberGenerator.GetBytes(32),Trust.Hash,"register exact runtime client certificate");
        }
        internal static X509Certificate2 CreateRoot(RSA rsa,DateTimeOffset now)
        {
            var request=new CertificateRequest("CN=FC2 Fixture Root "+Guid.NewGuid().ToString("N"),rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,true,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
            return request.CreateSelfSigned(now.AddDays(-1),now.AddDays(365));
        }
        internal byte[] Leaf(RSA rsa,DateTimeOffset now,string eku="1.3.6.1.5.5.7.3.2",bool ca=false,X509KeyUsageFlags usage=X509KeyUsageFlags.DigitalSignature,X509Certificate2? issuer=null,DateTimeOffset? until=null,Uri? crlUri=null)
        {
            var request=new CertificateRequest("CN=FC2 Fixture Client",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca,false,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage,true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new(eku)},false));
            if(crlUri is not null)request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUri.AbsoluteUri],false));
            using var certificate=request.Create(issuer??Root,now.AddMinutes(-1),until??now.AddDays(30),RandomNumberGenerator.GetBytes(16));
            return certificate.Export(X509ContentType.Cert);
        }
        internal SignedRuntimeCertificate Sign(RuntimeCredentialPayload p,byte[]? certificate=null,byte[]? root=null,RSA? key=null)
        {
            var cert=certificate??Certificate;var anchor=root??Root.RawData;var bytes=RuntimeCertificateProfile.Canonicalize(p,new(cert,anchor));
            return new(p,cert,anchor,[Proof(p.ApproverKeyId,A,bytes),Proof(p.ExecutorKeyId,B,bytes)],(key??Runtime).SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        }
        internal static BootstrapSignature Proof(Guid id,RSA rsa,byte[] bytes)=>new(id,rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        public void Dispose(){Root.Dispose();A.Dispose();B.Dispose();Ca.Dispose();Runtime.Dispose();Other.Dispose();}
    }
    internal static int PureChecks()
    {
        var count=0;var now=new DateTimeOffset(2026,11,6,0,0,0,TimeSpan.Zero);using var f=new Fixture(Guid.NewGuid(),Guid.NewGuid(),now,3);
        var p=f.Payload;var registration=new RuntimeCertificateRegistration(f.Certificate,f.Root.RawData);
        RuntimeCertificateProfile.ValidateRegistration(p,registration,now);Check(true,"offline certificate registration validates exact pinned root and client profile");
        Check(!RuntimeCertificateProfile.Canonicalize(p,registration).SequenceEqual(RuntimeCredentialProof.Canonicalize(p)),"certificate registration has its own signing domain");
        Check(RuntimeCertificateProfile.Canonicalize(p,registration).SequenceEqual(RuntimeCertificateProfile.Canonicalize(p with{IssuedAt=p.IssuedAt.ToOffset(TimeSpan.FromHours(5.5))},registration)),"certificate ceremony normalizes UTC signing fields");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new(f.Leaf(f.Other,now),f.Root.RawData),now),"certificate must contain exact enrolled runtime key");
        using var wrongRoot=Fixture.CreateRoot(f.Other,now);
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new(f.Certificate,wrongRoot.RawData),now),"certificate must chain to exact proposed trust anchor");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new(f.Leaf(f.Runtime,now,eku:"1.3.6.1.5.5.7.3.1"),f.Root.RawData),now),"server-only certificate cannot enroll as runtime client");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new(f.Leaf(f.Runtime,now,eku:"2.5.29.37.0"),f.Root.RawData),now),"any-purpose EKU cannot enroll as runtime client");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new(f.Leaf(f.Runtime,now,ca:true),f.Root.RawData),now),"CA certificate cannot enroll as runtime leaf");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new(f.Leaf(f.Runtime,now,usage:X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyCertSign),f.Root.RawData),now),"runtime leaf cannot carry CA signing usage");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p with{ValidFrom=now.AddSeconds(-30)},registration,now),"certificate cannot extend before enrolled credential interval");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p with{CredentialExpiresAt=now.AddDays(10)},registration,now),"certificate cannot extend beyond credential expiry");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,registration,now.AddDays(31)),"expired certificate cannot be registered");
        Denied(()=>RuntimeCertificateProfile.ValidateRegistration(p,new([..f.Certificate,0],f.Root.RawData),now),"nonexact DER trailing bytes reject");
        Invalid(()=>RuntimeCertificateProfile.Canonicalize(p,new(new byte[8193],f.Root.RawData)),"oversized certificate bytes reject before SQL");
        Invalid(()=>RuntimeCertificateProfile.Canonicalize(p with{Action="revoke",ExpectedGeneration=1},registration),"certificate registration cannot invoke credential revocation");
        return count;
        void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        void Denied(Action action,string name){try{action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        void Invalid(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new InvalidOperationException(name);}
    }
    internal static async Task<int> Run(InstallationTarget target)
    {
        var count=0;await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();
        var now=(DateTimeOffset)(await Scalar("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')"))!;
        var admin=Guid.NewGuid();var manager=Guid.NewGuid();using var f=new Fixture(target.InstallationId,manager,now,3);
        var initial=new BootstrapPayload(target.InstallationId,Guid.NewGuid(),admin,Issuer,"administrator",BootstrapTrustManifest.AdministratorProfile,f.File.Keys[0].KeyId,f.File.Keys[1].KeyId,
            now.AddSeconds(-1),now.AddMinutes(5),now.AddDays(30),0,RandomNumberGenerator.GetBytes(32),f.Trust.Hash,"certificate acceptance bootstrap");
        var bytes=BootstrapTrustManifest.Canonicalize(initial);
        await new SqlBootstrapAuthority(target,f.Trust).ApplyAsync(new(initial,[Fixture.Proof(initial.ApproverKeyId,f.A,bytes),Fixture.Proof(initial.ExecutorKeyId,f.B,bytes)]));
        var rootToken=RandomNumberGenerator.GetBytes(32);var token=RandomNumberGenerator.GetBytes(32);
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',1)",("@token",rootToken),("@actor",admin));
        var service=new SqlSecurityAuthority(target.ConnectionString,target.InstallationId);
        await service.EnrollHumanAsync(rootToken,new(Guid.NewGuid(),manager,Issuer,"certificate-manager",1,"enroll independent certificate manager"));
        var parent=(Guid)(await Scalar("SELECT GrantId FROM security.PermissionGrant WHERE Permission='runtime.credential.manage'"))!;var grant=Guid.NewGuid();
        await service.GrantPermissionAsync(rootToken,new(Guid.NewGuid(),grant,manager,parent,"runtime.credential.manage",AuthorizationScope.Installation,true,false,now,now.AddDays(1),2,"grant controlled certificate enrollment"));
        await Exec("INSERT security.SessionAuthority VALUES(@token,@actor,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),N'fixture-only',3)",("@token",token),("@actor",manager));
        var signed=f.Sign(f.Payload);var p=signed.Payload;
        await Denied(()=>service.EnrollRuntimeCertificateAsync(rootToken,signed),"security administration cannot bypass certificate management grant");
        await Denied(()=>service.EnrollRuntimeCertificateAsync(token,signed with{Signatures=[signed.Signatures[0]]}),"certificate enrollment requires two custody signatures");
        await Denied(()=>service.EnrollRuntimeCertificateAsync(token,signed with{PossessionSignature=f.Other.SignData(RuntimeCertificateProfile.Canonicalize(p,new(signed.CertificateDer,signed.TrustAnchorDer)),HashAlgorithmName.SHA256,RSASignaturePadding.Pss)}),"certificate ceremony requires runtime private key possession");
        var ordinary=RuntimeCredentialProof.Canonicalize(p);
        await Denied(()=>service.EnrollRuntimeCertificateAsync(token,signed with{Signatures=[Fixture.Proof(p.ApproverKeyId,f.A,ordinary),Fixture.Proof(p.ExecutorKeyId,f.B,ordinary)],PossessionSignature=f.Runtime.SignData(ordinary,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)}),"ordinary key signatures cannot authorize certificate registration domain");
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,new(p,signed.Signatures,signed.PossessionSignature)),"certificate signatures cannot authorize ordinary credential operation domain");
        await Denied(()=>service.EnrollRuntimeCertificateAsync(token,f.Sign(p,certificate:f.Leaf(f.Other,now))),"signed certificate substitution cannot change runtime key binding");
        await Denied(()=>service.EnrollRuntimeCertificateAsync(token,signed with{CertificateDer=f.Leaf(f.Runtime,now)}),"custody signatures bind exact certificate DER");
        await Conflict(()=>service.EnrollRuntimeCertificateAsync(token,f.Sign(p with{ExpectedEpoch=99})),"certificate registration checks security epoch");
        using(var custodyCa=Fixture.CreateRoot(f.A,now))
            await Denied(()=>service.EnrollRuntimeCertificateAsync(token,f.Sign(p,certificate:f.Leaf(f.Runtime,now,issuer:custodyCa),root:custodyCa.RawData)),"runtime CA cannot reuse bootstrap custodian key material");
        Check(await Rows()==0,"rejected certificates create no bindings");
        // Fails after binding insert, to prove rollback across all credential and certificate rows.
        await Exec("CREATE TRIGGER security.Fixture_CertificateFailure ON security.RuntimeCertificateBinding AFTER INSERT AS BEGIN THROW 51108,'certificate binding fixture',1; END;");
        try{await service.EnrollRuntimeCertificateAsync(token,signed);throw new InvalidOperationException("expected certificate failure");}catch(SqlException e)when(e.Number==51108){Check(true,"certificate binding write failure aborts whole enrollment");}
        await Exec("DROP TRIGGER security.Fixture_CertificateFailure");
        Check(await Rows()==0&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCredentialPublicKey"))==0&&Convert.ToInt64(await Scalar("SELECT AuthorityEpoch FROM security.InstallationState"))==3,"certificate generation ceremony audit and epoch roll back atomically");
        var activities=new List<Activity>();using var listener=new ActivityListener{ShouldListenTo=source=>source.Name=="FactoryConnect.V2.Security",Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,ActivityStopped=activity=>{lock(activities)activities.Add(activity);}};ActivitySource.AddActivityListener(listener);
        var results=await Task.WhenAll(service.EnrollRuntimeCertificateAsync(token,signed),service.EnrollRuntimeCertificateAsync(token,signed));
        Check(results[0].SequenceEqual(results[1])&&await Rows()==1,"concurrent exact certificate retries persist one binding");
        Check(Encoding.UTF8.GetString(results[0]).Contains("not-runtime-authentication",StringComparison.Ordinal),"certificate receipt does not claim runtime authentication");
        Check(((byte[])(await Scalar("SELECT CertificateHash FROM security.RuntimeCertificateBinding"))!).SequenceEqual(SHA256.HashData(signed.CertificateDer)),"SQL retains exact SHA256 certificate pin");
        Check(((byte[])(await Scalar("SELECT TrustAnchorHash FROM security.RuntimeCertificateBinding"))!).SequenceEqual(SHA256.HashData(signed.TrustAnchorDer)),"SQL retains exact installation ceremony approved CA pin");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.RuntimeAssignment"))==0&&Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM configuration.ActivationRequest"))==0,"certificate enrollment creates no assignment activation or acknowledgement");
        await Conflict(()=>service.EnrollRuntimeCertificateAsync(token,f.Sign(p with{Reason="changed"})),"completed certificate operation rejects changed request");
        var next=p with{Action="rotate",ExpectedGeneration=1,ExpectedEpoch=4,OperationId=Guid.NewGuid(),Nonce=RandomNumberGenerator.GetBytes(32),PublicKeySpki=f.Other.ExportSubjectPublicKeyInfo()};
        var nextSigned=f.Sign(next,certificate:f.Leaf(f.Other,now),key:f.Other);
        await service.EnrollRuntimeCertificateAsync(token,nextSigned);
        Check(await Rows()==2&&Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM security.RuntimeCredentialState"))==2,"certificate rotation appends next independent credential generation");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCredentialRevocation WHERE CredentialGeneration=1"))==1,"certificate rotation atomically invalidates prior generation");
        Check((await service.EnrollRuntimeCertificateAsync(token,signed)).SequenceEqual(results[0])&&Convert.ToInt64(await Scalar("SELECT CredentialGeneration FROM security.RuntimeCredentialState"))==2,"old certificate receipt cannot restore prior generation");
        try{await Exec("DELETE security.RuntimeCertificateBinding");throw new InvalidOperationException("mutable certificate binding");}catch(SqlException e)when(e.Number==51040){Check(true,"certificate binding and trust anchor history are immutable");}
        var caAsRuntime=p with{OperationId=Guid.NewGuid(),RuntimeIdentityId=Guid.NewGuid(),RuntimeSubject="ca-key-reuse",PublicKeySpki=f.Ca.ExportSubjectPublicKeyInfo(),ExpectedEpoch=5,Nonce=RandomNumberGenerator.GetBytes(32)};
        var caAsRuntimeBytes=RuntimeCredentialProof.Canonicalize(caAsRuntime);
        await Denied(()=>service.ChangeRuntimeCredentialAsync(token,new(caAsRuntime,[Fixture.Proof(p.ApproverKeyId,f.A,caAsRuntimeBytes),Fixture.Proof(p.ExecutorKeyId,f.B,caAsRuntimeBytes)],f.Ca.SignData(caAsRuntimeBytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))),"later runtime enrollment cannot adopt a registered runtime CA key");
        using var independentRecoveryKey=RSA.Create(3072);
        foreach(var reuse in new[]{(Key:f.Ca,Name:"runtime CA"),(Key:f.Other,Name:"runtime credential")})
        {
            var recoveryKeys=new[]{new BootstrapCustodian(Guid.NewGuid(),Guid.NewGuid(),Issuer,"recovery-reuse",reuse.Key.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(10)),
                new BootstrapCustodian(Guid.NewGuid(),Guid.NewGuid(),Issuer,"independent-recovery",independentRecoveryKey.ExportSubjectPublicKeyInfo(),now.AddDays(-1),now.AddDays(10))};
            var recoveryFile=new RecoveryTrustFile(target.InstallationId,Issuer,recoveryKeys);var recovery=new RecoveryTrustManifest(recoveryFile);
            var recoveryPayload=new RecoveryTrustProvisionPayload(target.InstallationId,Guid.NewGuid(),Guid.NewGuid(),admin,Issuer,"administrator",p.ApproverKeyId,p.ExecutorKeyId,
                now.AddSeconds(-1),now.AddMinutes(5),5,RandomNumberGenerator.GetBytes(32),f.Trust.Hash,recovery.Hash,"reject cross-role private key reuse");
            var recoveryBytes=RecoveryTrustManifest.Canonicalize(recoveryPayload);
            await Denied(()=>service.ProvisionRecoveryTrustAsync(rootToken,recoveryFile,new(recoveryPayload,[Fixture.Proof(p.ApproverKeyId,f.A,recoveryBytes),Fixture.Proof(p.ExecutorKeyId,f.B,recoveryBytes)],
                [Fixture.Proof(recoveryKeys[0].KeyId,reuse.Key,recoveryBytes),Fixture.Proof(recoveryKeys[1].KeyId,independentRecoveryKey,recoveryBytes)])),"later recovery provisioning cannot adopt "+reuse.Name+" key");
        }
        var measured=activities.Where(activity=>activity.GetTagItem("security.certificate.registration") is true&&activity.GetTagItem("security.result")?.ToString()=="completed").ToArray();
        var maxRoundtrips=measured.Max(activity=>Convert.ToInt64(activity.GetTagItem("security.sql.roundtrips")));
        Check(maxRoundtrips<=30&&measured.All(activity=>Convert.ToDouble(activity.GetTagItem("security.elapsed.ms"))<5000),"certificate registration retains fixed SQL and protected-duration budgets");
        Console.WriteLine($"MEASURE: certificate registration maxRoundtrips={maxRoundtrips}; offline registration, not mTLS authentication capacity");
        await service.RevokeGrantAsync(rootToken,Guid.NewGuid(),grant,5,"remove certificate enrollment permission");
        await Denied(()=>service.EnrollRuntimeCertificateAsync(token,signed),"completed certificate retry rechecks current management authority");
        return count;
        async Task<int> Rows()=>Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM security.RuntimeCertificateBinding"));
        void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);count++;Console.WriteLine("PASS: "+name);}
        async Task Denied(Func<Task> action,string name){try{await action();}catch(SecurityDeniedException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task Conflict(Func<Task> action,string name){try{await action();}catch(SecurityConflictException){Check(true,name);return;}throw new InvalidOperationException(name);}
        async Task<object?> Scalar(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);return await q.ExecuteScalarAsync();}
        async Task Exec(string sql,params (string,object)[] args){await using var q=new SqlCommand(sql,c);Add(q,args);await q.ExecuteNonQueryAsync();}
    }
    static void Add(SqlCommand q,params (string Name,object Value)[] args)
    {foreach(var(name,value)in args)if(value is byte[] bytes)q.Parameters.Add(name,SqlDbType.VarBinary,-1).Value=bytes;else q.Parameters.AddWithValue(name,value);}
}
