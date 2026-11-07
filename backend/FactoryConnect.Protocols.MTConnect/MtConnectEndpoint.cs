using System.Globalization;
namespace FactoryConnect.Protocols.MTConnect;

// Adapted from FactoryConnect 088bf524: explicit count and URI profile bounds for V2.
public sealed record MtConnectEndpoint
{
    public Uri BaseUri { get; }
    public MtConnectEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if(!baseUri.IsAbsoluteUri||baseUri.Scheme is not ("http" or "https")||baseUri.UserInfo.Length!=0||baseUri.Query.Length!=0||baseUri.Fragment.Length!=0)
            throw new ArgumentException("Explicit HTTP(S) agent/device base URI without credentials, query or fragment required.");
        BaseUri=new Uri(baseUri.AbsoluteUri.EndsWith('/')?baseUri.AbsoluteUri:baseUri.AbsoluteUri+"/",UriKind.Absolute);
    }
    public Uri SampleUri(ulong fromSequence,int count=1000)
    {
        if(count is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(count));
        return new(BaseUri,$"sample?from={fromSequence.ToString(CultureInfo.InvariantCulture)}&count={count.ToString(CultureInfo.InvariantCulture)}");
    }
}
