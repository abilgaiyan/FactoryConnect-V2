namespace FactoryConnect.Sql.Security;

public sealed record OperatorEvidenceMachine(Guid SiteId,Guid MachineId,string DisplayName);

public sealed partial class SqlSecurityAuthority
{
    /// <summary>Only currently authorized machine assignments are visible. The list is not an
    /// active-connection roster and carries no assertion of machine operational health.</summary>
    public Task<IReadOnlyList<OperatorEvidenceMachine>> ReadOperatorEvidenceMachinesAsync(
        byte[] sessionHash,CancellationToken ct=default)
    {
        var token=sessionHash.ToArray();
        return Transaction(null,false,async(c,t)=>
        {
            var state=await State(c,t,ct);
            var actor=await Session(c,t,token,state.Now,ct);
            var machines=new List<OperatorEvidenceMachine>();
            await using(var q=Command(c,t,"""
                SELECT TOP(101) m.SiteId,m.MachineId,m.DisplayName
                FROM [master].Machine m
                JOIN [master].Site s ON s.SiteId=m.SiteId
                WHERE m.IsRetired=0 AND s.IsRetired=0
                  AND EXISTS (SELECT 1 FROM security.PermissionGrant g WHERE
                    g.GranteeId=@actor AND g.Permission='commissioning.read' AND g.UseAllowed=1
                    AND g.EffectiveFrom<=@now AND g.ExpiresAt>@now
                    AND (g.ScopeKind='I' OR (g.ScopeKind='S' AND g.SiteId=m.SiteId)
                      OR (g.ScopeKind='M' AND g.SiteId=m.SiteId AND g.MachineId=m.MachineId)))
                ORDER BY m.SiteId,m.MachineId
                """))
            {
                Add(q,("@actor",actor),("@now",state.Now));
                await using var reader=await q.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                    machines.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2)));
            }
            if(machines.Count>100)throw new SecurityDeniedException();
            // Candidate query is only a bound. The shared grant-chain evaluator, which
            // checks parent revocation and delegation, decides every visible machine.
            var visible=new List<OperatorEvidenceMachine>();
            foreach(var machine in machines)
            {
                try
                {
                    await Authorize(c,t,actor,"commissioning.read",
                        AuthorizationScope.Machine(machine.SiteId,machine.MachineId),state.Now,ct);
                    visible.Add(machine);
                }
                catch(SecurityDeniedException) { }
            }
            return (IReadOnlyList<OperatorEvidenceMachine>)visible.AsReadOnly();
        },ct);
    }
}
