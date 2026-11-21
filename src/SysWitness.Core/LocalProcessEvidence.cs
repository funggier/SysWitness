using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SysWitness.Contracts;

namespace SysWitness.Core;

/// <summary>Encodes the captured Windows API fields in a source-specific evidence envelope.</summary>
public static class LocalProcessEvidence
{
    public const string SnapshotChannel = "WindowsProcessSnapshot";
    public const string SnapshotProvider = "System.Diagnostics.Process";
    public const string WmiChannel = "WindowsProcessWmi";
    public const string WmiProvider = "Microsoft-Windows-WMI-ProcessTrace";
    public const int Start = 1;
    public const int Exit = 2;

    private const string Category = "windows.process";
    private static readonly XNamespace Namespace = "urn:syswitness:process-observation:v1";

    public static ObservedEvent Create(string channel, int kind, string computer,
        DateTimeOffset occurredAt, DateTimeOffset observedAt, uint pid, string? name,
        uint? parentPid = null, uint? exitStatus = null, DateTimeOffset? startedAt = null)
    {
        var provider = channel switch
        {
            SnapshotChannel => SnapshotProvider,
            WmiChannel => WmiProvider,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };
        var xml = new XElement(Namespace + "ProcessObservation",
            new XAttribute("computer", computer),
            new XAttribute("kind", kind),
            new XAttribute("time", occurredAt.ToUniversalTime()),
            new XElement(Namespace + "ProcessId", pid),
            name is null ? null : new XElement(Namespace + "ProcessName", name),
            parentPid is null ? null : new XElement(Namespace + "ParentProcessId", parentPid.Value),
            exitStatus is null ? null : new XElement(Namespace + "ExitStatus", exitStatus.Value),
            startedAt is null ? null : new XElement(Namespace + "StartedAtUtc", startedAt.Value.ToUniversalTime()))
            .ToString(SaveOptions.DisableFormatting);
        return new ObservedEvent(EventIdentity.FromXml(channel, xml),
            occurredAt, observedAt, Category, channel, provider, kind, null, computer, xml);
    }

    public static ProcessEvent? Decode(ObservedEvent evidence)
    {
        var snapshot = evidence.Channel == SnapshotChannel && evidence.Provider == SnapshotProvider;
        var wmi = evidence.Channel == WmiChannel && evidence.Provider == WmiProvider;
        if ((!snapshot && !wmi) || evidence.SourceEventId is not (Start or Exit))
            return null;

        var fields = Read(evidence.RawXml);
        var pid = ReadNumber("ProcessId");
        var parentId = ReadNumber("ParentProcessId");
        var startedAt = DateTimeOffset.TryParse(fields?.Element(Namespace + "StartedAtUtc")?.Value,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var startTime) ? startTime : (DateTimeOffset?)null;
        var instanceId = pid is uint number && startedAt is { } start && evidence.Computer is { } host
            ? EventIdentity.FromXml(SnapshotChannel,
                new XElement("Instance", new XAttribute("computer", host),
                    new XAttribute("pid", number), new XAttribute("start", start)).ToString(SaveOptions.DisableFormatting))
            : null;

        return new ProcessEvent(evidence.EventId, evidence.OccurredAtUtc, evidence.ObservedAtUtc,
            evidence.SourceEventId == Start ? "start" : "exit",
            new ProcessEventSource(evidence.Channel, evidence.Provider, evidence.SourceEventId, null, evidence.Computer),
            pid is null ? "unavailable" : "decoded",
            pid, null, null, null, parentId is null ? null : new ProcessEventParent(parentId, null, null),
            ReadNumber("ExitStatus"),
            ObservationMethod: snapshot ? "snapshot" : "wmi",
            Quality: snapshot ? "derived" : "observed",
            ProcessName: fields?.Element(Namespace + "ProcessName")?.Value,
            ProcessStartedAtUtc: startedAt,
            LocalProcessInstanceId: instanceId);

        uint? ReadNumber(string field) =>
            uint.TryParse(fields?.Element(Namespace + field)?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static XElement? Read(string xml)
    {
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 65_536
            });
            var root = XElement.Load(reader);
            return root.Name == Namespace + "ProcessObservation" ? root : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
