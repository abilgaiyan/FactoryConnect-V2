using System.Text;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
namespace FactoryConnect.Protocols.MTConnect;

// V1 ResponseHeadersRead/error handling retained; body now bounded before parsing and values remain lossless.
public sealed class MtConnectSampleClient(HttpClient client)
{
    public async Task<MtConnectSampleFrame> AcquireAsync(MtConnectEndpoint endpoint,string deviceUuid,ulong fromSequence,int count=1000,CancellationToken ct=default)
    {
        using var response=await client.GetAsync(endpoint.SampleUri(fromSequence,count),HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.Content.Headers.ContentLength>MtConnectSampleParser.MaximumDocumentBytes)throw new InvalidDataException("MTConnect response byte bound exceeded.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();var chunk=new byte[8192];
        while(true){var read=await stream.ReadAsync(chunk,ct);if(read==0)break;if(buffer.Length+read>MtConnectSampleParser.MaximumDocumentBytes)throw new InvalidDataException("MTConnect response byte bound exceeded.");await buffer.WriteAsync(chunk.AsMemory(0,read),ct);}
        var bytes=buffer.ToArray();
        if(!response.IsSuccessStatusCode)
        {
            using var xmlInput=new MemoryStream(bytes,false);using var xmlReader=XmlReader.Create(xmlInput,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MtConnectSampleParser.MaximumDocumentBytes});
            var document=XDocument.Load(xmlReader);if(document.Root?.Name.LocalName=="MTConnectError")
            {
                var errors=document.Descendants().Where(x=>x.Name.LocalName=="Error").Take(33).Select(x=>new MtConnectError{Code=(string?)x.Attribute("errorCode")??"UNKNOWN",Message=x.Value}).ToArray();
                if(errors.Length>32)throw new InvalidDataException("MTConnect error list bound exceeded.");
                var instanceText=(string?)document.Descendants().SingleOrDefault(x=>x.Name.LocalName=="Header")?.Attribute("instanceId");ulong? instance=null;
                if(instanceText is not null){if(!ulong.TryParse(instanceText,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed))throw new InvalidDataException("Invalid MTConnect error instance identity.");instance=parsed;}
                throw new MtConnectProtocolException(response.StatusCode,new MtConnectErrorResult{InstanceId=instance,Errors=errors});
            }
            response.EnsureSuccessStatusCode();
        }
        return MtConnectSampleParser.Parse(bytes,deviceUuid,fromSequence,count);
    }
}
