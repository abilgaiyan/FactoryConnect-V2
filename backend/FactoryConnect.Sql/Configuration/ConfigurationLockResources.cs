namespace FactoryConnect.Sql.Configuration;

/// <summary>Canonical resources for all configuration/policy/activation writers. Acquire policy before machine,
/// with ordinal ordering within each phase. Readers use compatible modes and identical resource keys.</summary>
public static class ConfigurationLockResources
{
    public static string Policy(Guid installation,string kind,Guid revision)
    {
        if(installation==Guid.Empty||revision==Guid.Empty||kind is not ("timezone" or "schedule" or "break"))throw new ArgumentException("Exact policy lock identity required.");
        return $"fc2:policy:{installation:N}:{kind}:{revision:N}";
    }
    public static string Machine(Guid installation,Guid machine)
    {
        if(installation==Guid.Empty||machine==Guid.Empty)throw new ArgumentException("Exact machine lock identity required.");
        return $"fc2:machine:{installation:N}:{machine:N}";
    }
}
