namespace FactoryConnect.Sql.Security;

public sealed record OperatorEvidenceMachine(Guid SiteId,Guid MachineId,string DisplayName);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>List only currently authorized machines. Grant ancestry is loaded once
    /// inside the shared security transaction and the limit applies to visible machines.</summary>
    public Task<IReadOnlyList<OperatorEvidenceMachine>> ReadOperatorEvidenceMachinesAsync(
        byte[] sessionHash,Guid siteId,CancellationToken ct=default)
    {
        if(siteId==Guid.Empty)throw new ArgumentException("Explicit evidence site required.",nameof(siteId));
        var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);
            var actor=await Session(c,t,token,state.Now,ct);
            var grants=await LoadAuthorizationGrants(c,t,actor,"commissioning.read",ct);
            var validScopes=new List<AuthorizationScope>();
            foreach(var grant in grants.Values.Where(g=>g.Grantee==actor&&g.Use&&g.Permission=="commissioning.read"))
                if(TryAuthorizeLoadedGrants(grants,actor,"commissioning.read",grant.Scope,
                    state.Now,false,grant.Id,out _))
                    validScopes.Add(grant.Scope);

            var allSite=validScopes.Any(s=>s.Kind=="I"||
                s.Kind=="S"&&s.SiteId==siteId);
            var authorizedIds=validScopes.Where(s=>s.Kind=="M"&&s.SiteId==siteId)
                .Select(s=>s.MachineId!.Value).Distinct().ToHashSet();
            if(!allSite&&authorizedIds.Count==0)
                return (IReadOnlyList<OperatorEvidenceMachine>)Array.Empty<OperatorEvidenceMachine>();

            // Read the site's inventory in bounded pages. The authorization limit is
            // applied only after exact machine-scoped eligibility, so revoked historical
            // grants do not consume the 100 visible-machine budget.
            var visible=new List<OperatorEvidenceMachine>();
            var after=Guid.Empty;
            while(true)
            {
                await using var q=Command(c,t,"""
                    SELECT TOP(101) m.SiteId,m.MachineId,m.DisplayName
                    FROM [master].Machine m JOIN [master].Site s ON s.SiteId=m.SiteId
                    WHERE m.SiteId=@site AND m.MachineId>@after AND m.IsRetired=0 AND s.IsRetired=0
                    ORDER BY m.MachineId
                    """);
                Add(q,("@site",siteId),("@after",after));
                var found=0;
                await using(var reader=await q.ExecuteReaderAsync(ct))
                {
                    while(await reader.ReadAsync(ct))
                    {
                        var site=reader.GetGuid(0);
                        var machine=reader.GetGuid(1);
                        after=machine;
                        found++;
                        if(allSite||authorizedIds.Contains(machine))
                            visible.Add(new(site,machine,reader.GetString(2)));
                        if(visible.Count>100)throw new SecurityDeniedException();
                    }
                }
                if(found<101)break;
                ct.ThrowIfCancellationRequested();
            }
            return (IReadOnlyList<OperatorEvidenceMachine>)visible.AsReadOnly();
        },ct);
    }
}
