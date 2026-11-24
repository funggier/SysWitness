using SysWitness.Contracts;

namespace SysWitness.Core;

/// <summary>Describes exact source identity matching without inventing cross-source or causal links.</summary>
public static class ProcessActivityProjection
{
    public const string CorrelationMethod = "sysmon_subject_guid_and_computer_v1";

    public static IReadOnlyList<string> Limitations { get; } = Array.AsReadOnly(new[]
    {
        "Only retained Sysmon events with an exact subject ProcessGuid and Computer match are included.",
        "The grouping is derived from reported identity fields; it does not independently verify the process.",
        "PID-only Security, WMI and snapshot records are not joined.",
        "DNS queries and connections in one process do not establish causality or connection success.",
        "Missing lifecycle events, collection gaps and retention can leave this timeline incomplete.",
        "Ordering uses source occurrence times; it does not prove execution order."
    });

    public static ProcessActivityEvent Decode(ObservedEvent evidence)
    {
        if (evidence.Channel != WindowsProcessEventDecoder.SysmonChannel ||
            evidence.Provider != WindowsProcessEventDecoder.SysmonProvider)
            throw new ArgumentException("Activity requires Sysmon evidence.", nameof(evidence));

        return evidence.SourceEventId switch
        {
            1 or 5 => new(evidence.EventId, evidence.OccurredAtUtc, evidence.ObservedAtUtc,
                evidence.SourceEventId == 1 ? "process.start" : "process.exit",
                Lifecycle: WindowsProcessEventDecoder.Decode(evidence)),
            WindowsNetworkEventDecoder.SysmonNetworkConnect => new(evidence.EventId,
                evidence.OccurredAtUtc, evidence.ObservedAtUtc, "network.connection",
                Connection: WindowsNetworkEventDecoder.DecodeConnection(evidence)),
            WindowsNetworkEventDecoder.SysmonDnsQuery => new(evidence.EventId,
                evidence.OccurredAtUtc, evidence.ObservedAtUtc, "dns.query",
                DnsQuery: WindowsNetworkEventDecoder.DecodeDnsQuery(evidence)),
            _ => throw new ArgumentException("Unsupported process activity event.", nameof(evidence))
        };
    }
}
