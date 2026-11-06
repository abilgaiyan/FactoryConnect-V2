namespace FactoryConnect.Sql.Security;

public sealed record AuthorizationScope
{
    public string Kind { get; }
    public Guid? SiteId { get; }
    public Guid? MachineId { get; }
    private AuthorizationScope(string kind, Guid? siteId, Guid? machineId)
    { Kind=kind; SiteId=siteId; MachineId=machineId; }
    public static AuthorizationScope Installation { get; } = new("I", null, null);
    public static AuthorizationScope Site(Guid site) => site == Guid.Empty ? throw new ArgumentException("Invalid site.") : new("S",site,null);
    public static AuthorizationScope Machine(Guid site,Guid machine) => site == Guid.Empty || machine == Guid.Empty
        ? throw new ArgumentException("Invalid machine scope.") : new("M",site,machine);
    internal bool Contains(AuthorizationScope other) => Kind == "I" ||
        SiteId == other.SiteId && (Kind == "S" && other.Kind is "S" or "M" || Kind == "M" && other.Kind == "M" && MachineId == other.MachineId);
    internal static AuthorizationScope From(string kind,Guid? site,Guid? machine) => kind switch
    {
        "I" when site is null && machine is null => Installation,
        "S" when site is { } s && machine is null => Site(s),
        "M" when site is { } s && machine is { } m => Machine(s,m),
        _ => throw new InvalidOperationException("Corrupt authorization scope.")
    };
}
public sealed class SecurityDeniedException() : Exception("Current session or scoped authority does not permit this operation.");
public sealed class SecurityConflictException() : Exception("Security operation fingerprint or expected authority epoch conflicts.");
