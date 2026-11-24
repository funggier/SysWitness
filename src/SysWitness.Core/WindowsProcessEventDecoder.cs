using System.Globalization;
using SysWitness.Contracts;

namespace SysWitness.Core;

public static class WindowsProcessEventDecoder
{
    public const string SysmonChannel = "Microsoft-Windows-Sysmon/Operational";
    public const string SysmonProvider = "Microsoft-Windows-Sysmon";
    public const string SecurityChannel = "Security";
    public const string SecurityProvider = "Microsoft-Windows-Security-Auditing";

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

        var fields = WindowsEventXml.ReadFields(evidence.RawXml);
        string? Field(string name) => fields?.GetValueOrDefault(name);
        var start = kind == "start";
        var processId = ParsePid(Field(start && security ? "NewProcessId" : "ProcessId"), security);
        var processGuid = sysmon ? WindowsEventXml.ParseGuid(Field("ProcessGuid")) : null;
        var path = Field(security ? (start ? "NewProcessName" : "ProcessName") : "Image");

        ProcessEventParent? parent = null;
        if (start)
        {
            var parentId = ParsePid(Field(sysmon ? "ParentProcessId" : "ProcessId"), security);
            var parentGuid = sysmon ? WindowsEventXml.ParseGuid(Field("ParentProcessGuid")) : null;
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
        WindowsEventXml.ParseGuid(WindowsEventXml.ReadFields(xml)?.GetValueOrDefault("ProcessGuid"));

    /// <summary>Read the reported parent GUID; it is never a substitute for the subject identity.</summary>
    public static Guid? ReadSysmonParentProcessGuid(string xml) =>
        WindowsEventXml.ParseGuid(WindowsEventXml.ReadFields(xml)?.GetValueOrDefault("ParentProcessGuid"));

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
