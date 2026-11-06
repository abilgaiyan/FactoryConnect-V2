using System.Data;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Sql.Configuration;
using Microsoft.Data.SqlClient;

internal static class ConfigurationAcceptance
{
    private static readonly Guid Site = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Machine = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Line = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly byte[] PolicyBytes = Encoding.UTF8.GetBytes("{}");
    private static readonly byte[] PolicyHash = SHA256.HashData(PolicyBytes);
    private static readonly Guid Timezone = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Schedule = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Breaks = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    internal static PackageDefinition Example() => new(
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), 1, Site,
        new(Timezone, PolicyHash), [new(Machine,Line,
            new DateTimeOffset(2026,11,6,0,0,0,TimeSpan.Zero), null, new(Schedule,PolicyHash),new(Breaks,PolicyHash),
            [new(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),"counter","fixture","mt:Demo","opaque",[0,65,255,32],"identity-v1")], ["counter-reading"])]);

    internal static int CanonicalChecks()
    {
        var count=0; var package=Example(); var machine=package.Machines[0]; var signal=machine.Signals[0];
        var canonical=PackageCanonicalizer.Serialize(package);
        Check(Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()=="79a1babd57dfa05e54ebea67153352ca9f0db80163e5f9d6542a7c29dc2285a3", "canonical v1 golden digest");
        Check(canonical.SequenceEqual(PackageCanonicalizer.Serialize(package with { Machines=[machine with {
            EffectiveFrom=machine.EffectiveFrom.ToOffset(TimeSpan.FromHours(5.5)) }] })),"equivalent UTC instants canonicalize identically");
        var extra=signal with { MappingId=Guid.Parse("99999999-9999-9999-9999-999999999999"),SemanticKey="counter " };
        var first=package with { Machines=[machine with {Signals=[signal,extra],CapabilityIntent=["z","a"]}]};
        var reordered=package with { Machines=[machine with {Signals=[extra,signal],CapabilityIntent=["a","z"]}]};
        Check(PackageCanonicalizer.Serialize(first).SequenceEqual(PackageCanonicalizer.Serialize(reordered)),"signal and capability order independent");
        foreach(var changed in new[]{signal with { StreamKey="mt:demo" },signal with {StreamKey="mt:Demo "},
            signal with {AddressBytes=[0,65,255]},signal with {AddressEncoding="utf-8"}})
            Check(!canonical.SequenceEqual(PackageCanonicalizer.Serialize(package with {Machines=[machine with {Signals=[changed]}]})),"exact source mutation changes package bytes");
        var composed=signal with {StreamKey="\u00e9"};var decomposed=signal with {StreamKey="e\u0301"};
        Check(!PackageCanonicalizer.Hash(package with {Machines=[machine with {Signals=[composed]}]}).SequenceEqual(
            PackageCanonicalizer.Hash(package with {Machines=[machine with {Signals=[decomposed]}]})),"Unicode source identity is not normalized");
        Reject(package with {Machines=[machine,machine]},"duplicate machine rejected");
        Reject(package with {Machines=[machine with {EffectiveTo=machine.EffectiveFrom}]},"empty interval rejected");
        Reject(package with {Machines=[machine with {Signals=[signal with {StreamKey="\ud800"}]}]},"unpaired surrogate rejected");
        Reject(package with {Timezone=new(Timezone,new byte[31])},"invalid pin hash rejected");
        return count;
        void Check(bool pass,string label) {if(!pass)throw new InvalidOperationException(label);count++;Console.WriteLine("PASS: "+label);}
        void Reject(PackageDefinition value,string label)
        {try {PackageCanonicalizer.Serialize(value);}catch(ArgumentException){Check(true,label);return;}throw new InvalidOperationException(label);}
    }

    internal static async Task<int> Run(string connectionString)
    {
        var count=0;var package=Example();
        await using var c=new SqlConnection(connectionString);await c.OpenAsync();
        await Exec("INSERT [master].Machine(MachineId,SiteId,DisplayName) VALUES('77777777-7777-7777-7777-777777777777',@site,N'Extra fixture machine')",("@site",Site));
        await Exec("INSERT [master].ProductionLine(ProductionLineId,SiteId,DisplayName) VALUES('55555555-5555-5555-5555-555555555555','22222222-2222-2222-2222-222222222222',N'Other site line')");
        await Exec("INSERT [master].ProductionLine(ProductionLineId,SiteId,DisplayName) VALUES(@line,@site,N'Fixture line')",("@line",Line),("@site",Site));
        await Exec("""
            INSERT configuration.SiteTimezoneRevision(TimezoneRevisionId,SiteId,IanaZoneId,TzdbRelease,ResolverVersion,GapPolicy,FoldPolicy,
                RulesBytes,RulesSha256,CanonicalBytes,ContentHash)
            VALUES(@id,@site,N'Etc/UTC','fixture','fixture','reject','reject',@bytes,@hash,@bytes,@hash);
            """,("@id",Timezone),("@site",Site),("@bytes",PolicyBytes),("@hash",PolicyHash));
        await Exec("INSERT configuration.SharedScheduleRevision VALUES(@id,@site,@bytes,@hash)",("@id",Schedule),("@site",Site),("@bytes",PolicyBytes),("@hash",PolicyHash));
        await Exec("INSERT configuration.SharedBreakPolicyRevision VALUES(@id,@site,@bytes,@hash)",("@id",Breaks),("@site",Site),("@bytes",PolicyBytes),("@hash",PolicyHash));
        await Submit(package);Check(true,"submitted package stored atomically");
        await using(var q=new SqlCommand("SELECT CanonicalBytes,ContentHash FROM configuration.ConfigurationPackageRevision WHERE PackageRevisionId=@id",c))
        {
            q.Parameters.AddWithValue("@id",package.RevisionId);await using var r=await q.ExecuteReaderAsync();
            Check(await r.ReadAsync()&&((byte[])r[0]).SequenceEqual(PackageCanonicalizer.Serialize(package))&&((byte[])r[1]).SequenceEqual(PackageCanonicalizer.Hash(package)),"stored package bytes and hash match canonical fixture");
        }
        await using(var q=new SqlCommand("SELECT AddressBytes FROM configuration.PackageSignalMapping",c))
            Check(((byte[])(await q.ExecuteScalarAsync())!).SequenceEqual(package.Machines[0].Signals[0].AddressBytes),"opaque source bytes round-trip");
        await RejectSql(()=>Exec("UPDATE configuration.ConfigurationPackageRevision SET RevisionNumber=2"),"submitted revision update rejected");
        await RejectSql(()=>Exec("DELETE configuration.ConfigurationPackageRevision"),"submitted revision deletion rejected");
        await RejectSql(()=>Exec("INSERT configuration.PackageMachineScope VALUES(@revision,@site,'77777777-7777-7777-7777-777777777777')",("@revision",package.RevisionId),("@site",Site)),"late submitted scope insertion rejected");
        await RejectSql(()=>Exec("DELETE configuration.PackageMachineScope"),"submitted scope deletion rejected");
        await RejectSql(()=>Exec("UPDATE configuration.PackageMachineAssignment SET EffectiveTo=DATEADD(day,1,EffectiveFrom)"),"submitted assignment update rejected");
        await RejectSql(()=>Exec("UPDATE configuration.PackageSignalMapping SET StreamKey=N'changed'"),"submitted mapping update rejected");
        await RejectSql(()=>Exec("UPDATE configuration.SharedScheduleRevision SET CanonicalBytes=0x01"),"shared schedule mutation rejected");
        await RejectSql(()=>Exec("DELETE configuration.SharedBreakPolicyRevision"),"shared break deletion rejected");
        await RejectSql(()=>Exec("UPDATE configuration.SiteTimezoneRevision SET TzdbRelease='latest'"),"pinned timezone provenance mutation rejected");
        var wrongHash=package with {RevisionId=Guid.NewGuid(),RevisionNumber=2,Timezone=new(Timezone,new byte[32])};
        await RejectSql(()=>Submit(wrongHash),"wrong pinned timezone hash rejected");
        var wrongSite=package with {PackageId=Guid.NewGuid(),RevisionId=Guid.NewGuid(),SiteId=Guid.Parse("22222222-2222-2222-2222-222222222222")};
        await RejectSql(()=>Submit(wrongSite),"cross-site policy or machine rejected");
        var wrongLine=package with {RevisionId=Guid.NewGuid(),RevisionNumber=2,Machines=[package.Machines[0] with {ProductionLineId=Guid.Parse("55555555-5555-5555-5555-555555555555")}]};
        await RejectSql(()=>Submit(wrongLine),"cross-site line assignment rejected");
        await using(var q=new SqlCommand("SELECT COUNT(*) FROM configuration.ConfigurationPackageRevision",c))
            Check(Convert.ToInt32(await q.ExecuteScalarAsync())==1,"failed submissions leave no partial revisions");
        return count;

        async Task Submit(PackageDefinition value)
        {
            await using var tx=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable);
            try{await new SqlPackageRepository().InsertSubmittedAsync(c,tx,value);await tx.CommitAsync();}
            catch{try{await tx.RollbackAsync();}catch(InvalidOperationException){}throw;}
        }
        async Task Exec(string sql,params (string Name,object Value)[] parameters)
        {await using var q=new SqlCommand(sql,c);foreach(var (name,value) in parameters)q.Parameters.AddWithValue(name,value);await q.ExecuteNonQueryAsync();}
        void Check(bool pass,string label){if(!pass)throw new InvalidOperationException(label);count++;Console.WriteLine("PASS: "+label);}
        async Task RejectSql(Func<Task> action,string label)
        {try{await action();}catch(SqlException){Check(true,label);return;}throw new InvalidOperationException("Expected SQL rejection: "+label);}
    }
}
