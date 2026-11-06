namespace FactoryConnect.Sql.Configuration;

public sealed record PolicyReference(Guid RevisionId, byte[] ContentHash);
public sealed record SignalDefinition(Guid MappingId, string SemanticKey, string Protocol,
    string StreamKey, string AddressEncoding, byte[] AddressBytes, string Normalization);
public sealed record MachineConfiguration(Guid MachineId, Guid ProductionLineId,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo, PolicyReference Schedule,
    PolicyReference BreakPolicy, IReadOnlyList<SignalDefinition> Signals,
    IReadOnlyList<string> CapabilityIntent);
public sealed record PackageDefinition(Guid PackageId, Guid RevisionId, int RevisionNumber,
    Guid SiteId, PolicyReference Timezone, IReadOnlyList<MachineConfiguration> Machines);
