using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SysWitness.Contracts;

namespace SysWitness.Core;

public static class WindowsProcessEventDecoder
{
    public const string SysmonChannel = "Microsoft-Windows-Sysmon/Operational";
    public const string SysmonProvider = "Microsoft-Windows-Sysmon";
    public const string SecurityChannel = "Security";
    public const string SecurityProvider = "Microsoft-Windows-Security-Auditing";

    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1_048_576
    };

    public static ProcessEvent? Decode(ObservedEvent evidence)
    {
        var sysmon = evidence.Channel == SysmonChannel && evidence.Provider == SysmonProvider;
        var security = evidence.Channel == SecurityChannel && evidence.Provider == SecurityProvider;
        var kind = (sysmon, security, evidence.SourceEventId) switch
        {
            (true, _, 1) or (_, true, 4688) => "start",
            (true, _, 5) or (_, true, 4689) => "exit",
            _ => null
        };
        if (kind is null)
            return null;

        var fields = ReadFields(evidence.RawXml);
        string? Field(string name) => fields?.GetValueOrDefault(name);
        var start = kind == "start";
        var processId = ParsePid(Field(start && security ? "NewProcessId" : "ProcessId"), security);
        var processGuid = sysmon ? ParseGuid(Field("ProcessGuid")) : null;
        var path = Field(security ? (start ? "NewProcessName" : "ProcessName") : "Image");

        ProcessEventParent? parent = null;
        if (start)
        {
            var parentId = ParsePid(Field(sysmon ? "ParentProcessId" : "ProcessId"), security);
            var parentGuid = sysmon ? ParseGuid(Field("ParentProcessGuid")) : null;
            var parentPath = Field(sysmon ? "ParentImage" : "ParentProcessName");
            if (parentId is not null || parentGuid is not null || parentPath is not null)
                parent = new ProcessEventParent(parentId, parentGuid, parentPath);
        }

        var commandLine = start ? Field("CommandLine") : null;
        var exitStatus = security && !start ? ParseHex(Field("Status")) : null;
        var hasProcessData = processId is not null || processGuid is not null || path is not null ||
            commandLine is not null || parent is not null || exitStatus is not null;
        return new ProcessEvent(evidence.EventId, evidence.OccurredAtUtc, evidence.ObservedAtUtc,
            kind, new ProcessEventSource(evidence.Channel, evidence.Provider, evidence.SourceEventId,
                evidence.SourceRecordId, evidence.Computer),
            hasProcessData ? "decoded" : "unavailable",
            processId, processGuid, path, commandLine, parent, exitStatus);
    }

    /// <summary>Read the subject GUID from a Sysmon payload already selected by provider and event ID.</summary>
    public static Guid? ReadSysmonProcessGuid(string xml) =>
        ParseGuid(ReadFields(xml)?.GetValueOrDefault("ProcessGuid"));

    private static Dictionary<string, string?>? ReadFields(string xml)
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

    private static Guid? ParseGuid(string? value)
    {
        if ((Guid.TryParseExact(value, "B", out var id) || Guid.TryParseExact(value, "D", out id)) &&
            id != Guid.Empty)
            return id;
        return null;
    }

    private static uint? ParsePid(string? value, bool hexadecimal)
    {
        if (hexadecimal)
            return ParseHex(value);
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;
    }

    private static uint? ParseHex(string? value) =>
        value is not null && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
}
