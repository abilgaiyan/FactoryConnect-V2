using System.Xml;
using System.Xml.Linq;

namespace FactoryConnect.Api;

/// <summary>
/// Reads MTConnect /probe device metadata without making network requests or changing
/// machine configuration. The caller is responsible for using a trusted configured Agent.
/// </summary>
internal static class MtConnectProbeDevices
{
    internal sealed record DataItem(string Id, string? Name, string? Type, string? SubType);
    internal sealed record Device(string Id, string Name, string? Uuid, IReadOnlyList<DataItem> DataItems);

    internal static IReadOnlyList<Device> Parse(Stream xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(xml, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4_000_000,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "MTConnectDevices")
            throw new InvalidDataException("Response is not an MTConnect Devices document.");
        var devices = document.Descendants().Where(node => node.Name.LocalName == "Device")
            .Take(501).ToArray();
        if (devices.Length > 500) throw new InvalidDataException("Too many MTConnect devices.");
        var result = new List<Device>(devices.Length);
        foreach (var device in devices)
        {
            var id = (string?)device.Attribute("id");
            var name = (string?)device.Attribute("name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("MTConnect device is missing its ID or name.");
            var items = device.Descendants().Where(node => node.Name.LocalName == "DataItem")
                .Take(5001).ToArray();
            if (items.Length > 5000) throw new InvalidDataException("Too many data items in MTConnect device.");
            result.Add(new Device(id, name, (string?)device.Attribute("uuid"),
                items.Select(item => new DataItem((string?)item.Attribute("id") ?? "",
                    (string?)item.Attribute("name"), (string?)item.Attribute("type"),
                    (string?)item.Attribute("subType"))).ToArray()));
        }
        return result;
    }
}
