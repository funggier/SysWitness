using System.Globalization;
using System.Net;
using SysWitness.Contracts;
using static SysWitness.Core.WindowsProcessEventDecoder;

namespace SysWitness.Core;

/// <summary>Projects network observations from the matching channel, provider and source event ID.</summary>
public static class WindowsNetworkEventDecoder
{
    public const int SysmonNetworkConnect = 3;
    public const int SysmonDnsQuery = 22;
    public const int FilteringPlatformAllowed = 5156;
    public const int FilteringPlatformBlocked = 5157;

    public static NetworkConnectionEvent? DecodeConnection(ObservedEvent evidence)
    {
        var sysmon = IsSysmon(evidence) && evidence.SourceEventId == SysmonNetworkConnect;
        var wfp = evidence.Channel == SecurityChannel && evidence.Provider == SecurityProvider &&
            evidence.SourceEventId is FilteringPlatformAllowed or FilteringPlatformBlocked;
        if (!sysmon && !wfp)
            return null;

        var fields = WindowsEventXml.ReadFields(evidence.RawXml);
        string? Field(string key) => fields?.GetValueOrDefault(key);
        var process = ReadProcess(fields, sysmon);
        var protocol = ReadProtocol(Field("Protocol"), sysmon);
        var direction = sysmon
            ? Field("Initiated")?.ToLowerInvariant() switch { "true" => "outbound", "false" => "inbound", _ => null }
            : Field("Direction") switch { "%%14592" => "inbound", "%%14593" => "outbound", _ => null };

        // The WFP audit uses a local/remote pair. Sysmon reports the connection's source/destination.
        string? sourceRole = wfp ? "local" : direction switch
        {
            "outbound" => "local",
            "inbound" => "remote",
            _ => null
        };
        var destinationRole = sourceRole switch { "local" => "remote", "remote" => "local", _ => null };
        var hasPorts = sysmon || protocol is 6 or 17;
        var source = new NetworkEndpoint(ReadAddress(Field(sysmon ? "SourceIp" : "SourceAddress")),
            hasPorts ? ReadPort(Field("SourcePort")) : null, sysmon ? Field("SourceHostname") : null, sourceRole);
        var destination = new NetworkEndpoint(ReadAddress(Field(sysmon ? "DestinationIp" : "DestAddress")),
            hasPorts ? ReadPort(Field(sysmon ? "DestinationPort" : "DestPort")) : null,
            sysmon ? Field("DestinationHostname") : null, destinationRole);
        var hasDetails = HasProcessData(process) || HasEndpointData(source) || HasEndpointData(destination) ||
            protocol is not null || direction is not null;
        var decision = wfp ? evidence.SourceEventId == FilteringPlatformAllowed ? "allowed" : "blocked" : null;
        return new NetworkConnectionEvent(evidence.EventId, evidence.OccurredAtUtc, evidence.ObservedAtUtc,
            Source(evidence), hasDetails ? "decoded" : "unavailable", process, ProtocolName(protocol),
            protocol, direction, decision, source, destination);
    }

    public static DnsQueryEvent? DecodeDnsQuery(ObservedEvent evidence)
    {
        if (!IsSysmon(evidence) || evidence.SourceEventId != SysmonDnsQuery)
            return null;

        var fields = WindowsEventXml.ReadFields(evidence.RawXml);
        var process = ReadProcess(fields, sysmon: true);
        var name = fields?.GetValueOrDefault("QueryName");
        var results = fields?.GetValueOrDefault("QueryResults");
        long? status = long.TryParse(fields?.GetValueOrDefault("QueryStatus"), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value) ? value : null;
        var hasDetails = HasProcessData(process) || name is not null || results is not null || status is not null;
        return new DnsQueryEvent(evidence.EventId, evidence.OccurredAtUtc, evidence.ObservedAtUtc,
            Source(evidence), hasDetails ? "decoded" : "unavailable", process, name, status, results);
    }

    private static NetworkProcess ReadProcess(Dictionary<string, string?>? fields, bool sysmon)
    {
        var pid = fields?.GetValueOrDefault(sysmon ? "ProcessId" : "ProcessID");
        var processId = ReadProcessId(pid);
        var guid = sysmon ? WindowsEventXml.ParseGuid(fields?.GetValueOrDefault("ProcessGuid")) : null;
        var image = fields?.GetValueOrDefault(sysmon ? "Image" : "Application");
        return new NetworkProcess(processId, guid, image,
            guid is not null ? "processGuid" : processId is not null ? "pidOnly" : "unavailable");
    }

    private static uint? ReadProcessId(string? input)
    {
        if (input?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
            return uint.TryParse(input.AsSpan(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var hex) ? hex : null;
        return uint.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    private static byte? ReadProtocol(string? input, bool sysmon)
    {
        if (sysmon)
            return input?.ToLowerInvariant() switch { "tcp" => 6, "udp" => 17, _ => null };
        return byte.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static string? ProtocolName(byte? value) =>
        value switch { 1 => "icmp", 6 => "tcp", 17 => "udp", 58 => "icmpv6", _ => null };

    private static ushort? ReadPort(string? value) =>
        ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;

    private static string? ReadAddress(string? value)
    {
        if (value is null || (!value.Contains(':') && value.Count(c => c == '.') != 3))
            return null;
        return IPAddress.TryParse(value, out var address) ? address.ToString() : null;
    }

    private static bool HasEndpointData(NetworkEndpoint endpoint) =>
        endpoint.Address is not null || endpoint.Port is not null || endpoint.HostName is not null;

    private static bool HasProcessData(NetworkProcess process) =>
        process.ProcessGuid is not null || process.ProcessId is not null || process.ImagePath is not null;

    private static bool IsSysmon(ObservedEvent evidence) =>
        evidence.Channel == SysmonChannel && evidence.Provider == SysmonProvider;

    private static NetworkEventSource Source(ObservedEvent evidence) =>
        new(evidence.Channel, evidence.Provider, evidence.SourceEventId, evidence.SourceRecordId, evidence.Computer);
}
