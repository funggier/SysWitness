using System.Xml;
using System.Xml.Linq;

namespace SysWitness.Core;

internal static class WindowsEventXml
{
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1_048_576
    };

    public static Dictionary<string, string?>? ReadFields(string xml)
    {
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, XmlSettings);
            var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
            if (root?.Name != EventNamespace + "Event")
                return null;
            var payloads = root.Elements(EventNamespace + "EventData").Take(2).ToArray();
            if (payloads.Length != 1)
                return null;

            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var element in payloads[0].Elements(EventNamespace + "Data"))
            {
                if (element.Attribute("Name")?.Value is not { Length: > 0 } name)
                    continue;
                var value = !element.HasElements && !string.IsNullOrWhiteSpace(element.Value) &&
                    element.Value.Trim() != "-" ? element.Value : null;
                if (!fields.TryAdd(name, value))
                    fields[name] = null;
            }
            return fields;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static Guid? ParseGuid(string? value)
    {
        if ((Guid.TryParseExact(value, "B", out var id) || Guid.TryParseExact(value, "D", out id)) &&
            id != Guid.Empty)
            return id;
        return null;
    }
}
