using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectSampleObservation(ulong Sequence,string DataItemId,string Category,string ElementName,string RawValue,
    string SourceTimestamp,DateTimeOffset Timestamp,string Quality,string CanonicalJson,string ContentHash);
public sealed record MtConnectSampleFrame(ulong InstanceId,ulong FirstSequence,ulong LastSequence,ulong NextSequence,string DeviceUuid,
    IReadOnlyList<MtConnectSampleObservation> Observations,byte[] RawDocument);

// V1 header/sequence/category parsing adapted to a bounded, lossless V2 sample profile.
public static class MtConnectSampleParser
{
    public const int MaximumDocumentBytes=1048576;
    private static readonly UTF8Encoding Utf8=new(false,true);
    public static MtConnectSampleFrame Parse(byte[] documentBytes,string deviceUuid,ulong fromSequence,int maximumObservations=1000)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);ArgumentException.ThrowIfNullOrWhiteSpace(deviceUuid);
        if(documentBytes.Length is <1 or >MaximumDocumentBytes||maximumObservations is <1 or >1000||Utf8.GetByteCount(deviceUuid)>256)throw new InvalidDataException("MTConnect frame exceeds profile bounds.");
        var raw=documentBytes.ToArray();_=Utf8.GetString(raw);
        using var input=new MemoryStream(raw,false);using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumDocumentBytes});
        var structure=0;
        while(reader.Read()){structure+=1+reader.AttributeCount;if(reader.Depth>32||structure>20000)throw new InvalidDataException("MTConnect XML structure bound exceeded.");}
        input.Position=0;using var boundedReader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumDocumentBytes});
        var document=XDocument.Load(boundedReader,LoadOptions.PreserveWhitespace);
        if(document.Declaration?.Encoding is {} encoding&&!encoding.Equals("utf-8",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("UTF-8 MTConnect profile required.");
        if(document.Root?.Name.LocalName!="MTConnectStreams")throw new InvalidDataException("MTConnectStreams document required.");
        var headers=document.Root.Elements().Where(x=>x.Name.LocalName=="Header").ToArray();if(headers.Length!=1)throw new InvalidDataException("Exactly one source header required.");
        var header=headers[0];var instance=Number(header,"instanceId");var first=Number(header,"firstSequence");var last=Number(header,"lastSequence");var next=Number(header,"nextSequence");
        if(first>fromSequence||next<fromSequence||first>next||(last<ulong.MaxValue&&next>last+1))throw new InvalidDataException("Source range gap or cursor regression; explicit recovery required.");
        var devices=document.Descendants().Where(x=>x.Name.LocalName=="DeviceStream").ToArray();
        // First slice deliberately supports one device per response. Unknown/mixed identity is never a successful empty batch.
        if(devices.Length!=1||!string.Equals((string?)devices[0].Attribute("uuid"),deviceUuid,StringComparison.Ordinal))throw new InvalidDataException("Exact single DeviceStream UUID required.");
        var elements=devices[0].Descendants().Where(x=>x.Attribute("dataItemId") is not null).Take(maximumObservations+1).ToArray();
        if(elements.Length>maximumObservations)throw new InvalidDataException("Observation batch bound exceeded.");
        var rows=new List<MtConnectSampleObservation>();var seen=new HashSet<ulong>();var canonicalTotal=0;
        foreach(var element in elements)
        {
            var sequence=Number(element,"sequence");var address=(string)element.Attribute("dataItemId")!;var timestamp=(string?)element.Attribute("timestamp");
            var category=element.Ancestors().Select(x=>x.Name.LocalName).FirstOrDefault(x=>x is "Samples" or "Events" or "Condition");
            if(sequence<fromSequence||sequence>=next||sequence<first||sequence>last||!seen.Add(sequence)||element.HasElements||string.IsNullOrEmpty(address)||Utf8.GetByteCount(address)>512||timestamp is null||timestamp.Length>128||category is null)throw new InvalidDataException("Invalid source observation identity, interval or timestamp.");
            DateTimeOffset instant;
            try{if(!timestamp.Contains('T')||!(timestamp.EndsWith('Z')||(timestamp.Length>=6&&timestamp[^3]==':'&&timestamp[^6] is '+' or '-')))throw new FormatException();instant=XmlConvert.ToDateTimeOffset(timestamp);}
            catch(FormatException){throw new InvalidDataException("Explicit ISO source timestamp with offset required.");}
            if(element.Name.ToString().Length>512||element.Attributes().Any(a=>a.Name.ToString().Length>512)||element.Attributes().Count()>32)throw new InvalidDataException("MTConnect observation metadata bound exceeded.");
            var value=element.Value;var name=element.Name.ToString();var quality=value=="UNAVAILABLE"?"unavailable":"source-reported";
            var canonical=JsonSerializer.Serialize(new{sequence=sequence.ToString(CultureInfo.InvariantCulture),name,category,attributes=element.Attributes().Where(a=>!a.IsNamespaceDeclaration).OrderBy(a=>a.Name.ToString(),StringComparer.Ordinal).Select(a=>new{name=a.Name.ToString(),a.Value}),value});
            var bytes=Utf8.GetBytes(canonical);canonicalTotal=checked(canonicalTotal+bytes.Length);if(canonicalTotal>8388608)throw new InvalidDataException("Canonical observation byte bound exceeded.");if(bytes.Any(b=>b>127))throw new InvalidDataException("Canonical JSON must be ASCII escaped.");
            rows.Add(new(sequence,address,category,name,value,timestamp,instant.ToUniversalTime(),quality,canonical,Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return new(instance,first,last,next,deviceUuid,Array.AsReadOnly(rows.OrderBy(x=>x.Sequence).ToArray()),raw);
    }
    private static ulong Number(XElement element,string attribute)
        =>ulong.TryParse((string?)element.Attribute(attribute),NumberStyles.None,CultureInfo.InvariantCulture,out var value)?value:throw new InvalidDataException("Missing or invalid source sequence header.");
}
