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

            // Site-wide grants are limited in SQL. For machine-only grants, pass
            // only validated machine IDs to SQL; never walk unrelated site inventory.
            // JSON is parameterized, not concatenated into the SQL text. No new
            // schema type or migration is required.
            var sql=allSite ? """
                SELECT TOP (101) m.SiteId,m.MachineId,m.DisplayName
                FROM [master].Machine m JOIN [master].Site s ON s.SiteId=m.SiteId
                WHERE m.SiteId=@site AND m.IsRetired=0 AND s.IsRetired=0
                ORDER BY m.MachineId
                """ : """
                SELECT TOP (101) m.SiteId,m.MachineId,m.DisplayName
                FROM OPENJSON(@machineIds) WITH (MachineId uniqueidentifier '$') AS authorized
                JOIN [master].Machine m ON m.MachineId=authorized.MachineId
                JOIN [master].Site s ON s.SiteId=m.SiteId
                WHERE m.SiteId=@site AND m.IsRetired=0 AND s.IsRetired=0
                ORDER BY m.MachineId
                """;
            var visible=new List<OperatorEvidenceMachine>();
            await using(var q=Command(c,t,sql))
            {
                Add(q,("@site",siteId));
                if(!allSite)
                {
                    var ids=System.Text.Json.JsonSerializer.Serialize(authorizedIds.Order());
                    q.Parameters.Add("@machineIds",System.Data.SqlDbType.NVarChar,-1).Value=ids;
                }
                await using var reader=await q.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                    visible.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2)));
            }
            if(visible.Count>100)throw new SecurityDeniedException();
            return (IReadOnlyList<OperatorEvidenceMachine>)visible.AsReadOnly();
        },ct);
    }
}
