namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectErrorResult
{
    public ulong? InstanceId { get; init; }

    public required IReadOnlyList<MtConnectError> Errors
    {
        get;
        init;
    }
}

