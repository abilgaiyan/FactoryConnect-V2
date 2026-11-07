using System.Net;

namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectProtocolException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public MtConnectErrorResult ErrorResult { get; }

    public MtConnectProtocolException(
        HttpStatusCode statusCode,
        MtConnectErrorResult errorResult)
        : base(BuildMessage(statusCode, errorResult))
    {
        StatusCode = statusCode;
        ErrorResult = errorResult;
    }

    private static string BuildMessage(
        HttpStatusCode statusCode,
        MtConnectErrorResult errorResult)
    {
        ArgumentNullException.ThrowIfNull(errorResult);

        // V2 exception text contains metadata only; the bounded error result retains source text for explicit inspection.
        return $"MTConnect protocol error ({(int)statusCode}); {errorResult.Errors.Count} error(s).";
    }
}
