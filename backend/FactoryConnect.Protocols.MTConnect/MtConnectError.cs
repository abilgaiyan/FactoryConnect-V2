namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectError
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}

