using System.Xml.Linq;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;
using static SysWitness.Core.WindowsProcessEventDecoder;

internal static class NetworkMonitoringSmoke
{
    private static readonly Guid ProcessGuid = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid OtherGuid = Guid.Parse("66666666-2222-4333-8444-555555555555");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static ObservedEvent Fixture(string channel, string provider, int eventCode, int second,
        params (string Name, string Value)[] fields)
    {
        var xml = new XElement(EventNamespace + "Event",
            new XElement(EventNamespace + "System", new XElement(EventNamespace + "EventID", eventCode),
                new XElement(EventNamespace + "EventRecordID", second)),
            new XElement(EventNamespace + "EventData", fields.Select(f =>
                new XElement(EventNamespace + "Data", new XAttribute("Name", f.Name), f.Value))))
            .ToString(SaveOptions.DisableFormatting);
        return new ObservedEvent(EventIdentity.FromXml(channel, xml), Epoch.AddSeconds(second),
            Epoch.AddSeconds(second + 1), "windows.event", channel, provider, eventCode, second, "synthetic-host", xml);
    }

    public static async Task SeedAsync(string path)
    {
        var store = new SqliteEventStore(path);
        await store.InitializeAsync();
        await store.InsertAsync([
            Fixture(SysmonChannel, SysmonProvider, 3, 1, ("ProcessGuid", ProcessGuid.ToString("B")),
                ("ProcessId", "78"), ("Image", @"C:\Fixture\network.exe"), ("Protocol", "tcp"),
                ("Initiated", "true"), ("SourceIp", "192.0.2.10"), ("SourcePort", "52525"),
                ("DestinationIp", "2001:db8::7"), ("DestinationPort", "443")),
            Fixture(SysmonChannel, SysmonProvider, 3, 2, ("ProcessGuid", OtherGuid.ToString("B")),
                ("ProcessId", "78"), ("Protocol", "udp"), ("Initiated", "false"),
                ("SourceIp", "198.51.100.8"), ("SourcePort", "53"),
                ("DestinationIp", "192.0.2.10"), ("DestinationPort", "52526")),
            Fixture(SecurityChannel, SecurityProvider, 5156, 3, ("ProcessID", "78"),
                ("Application", @"\device\harddiskvolume2\fixture.exe"), ("Direction", "%%14592"),
                ("Protocol", "6"), ("SourceAddress", "192.0.2.10"), ("SourcePort", "443"),
                ("DestAddress", "203.0.113.8"), ("DestPort", "52526")),
            Fixture(SecurityChannel, SecurityProvider, 5157, 4, ("ProcessID", "0x4e"),
                ("Direction", "%%14593"), ("Protocol", "17"), ("SourceAddress", "192.0.2.10"),
                ("SourcePort", "52527"), ("DestAddress", "2001:db8::8"), ("DestPort", "53")),
            Fixture(SysmonChannel, SysmonProvider, 22, 5, ("ProcessGuid", ProcessGuid.ToString("D")),
                ("ProcessId", "78"), ("QueryName", "fixture.example"), ("QueryStatus", "0"),
                ("QueryResults", "type: 5 alias.example;2001:db8::7;")),
            Fixture(SysmonChannel, SysmonProvider, 22, 6, ("ProcessGuid", ProcessGuid.ToString("B")),
                ("ProcessId", "78"), ("QueryName", "missing.example"), ("QueryStatus", "9003"),
                ("QueryResults", "-")),
            Fixture(SysmonChannel, SysmonProvider, 22, 7, ("ProcessGuid", OtherGuid.ToString("B")),
                ("ProcessId", "78"), ("QueryName", "unrelated.example"), ("QueryResults", ProcessGuid.ToString("D"))),
            Fixture(SysmonChannel, "OtherProvider", 3, 8, ("ProcessGuid", ProcessGuid.ToString("B"))),
            Fixture("Application", SysmonProvider, 22, 9, ("QueryName", "other-channel.example"))
        ]);
    }

    public static async Task VerifyAsync(string path)
    {
        await SeedAsync(path);
        var store = new SqliteEventStore(path);
        var first = await store.GetNetworkConnectionsAsync(new EventQuery(Limit: 1));
        Check(first.Returned == 1 && first.NextCursor is not null && first.Items[0] is
            { Decision: "blocked", Protocol: "udp", ProtocolNumber: 17, Direction: "outbound",
              Process: { ProcessId: 78, ProcessGuid: null, Attribution: "pidOnly" },
              Source: { Channel: SecurityChannel, SourceEventId: 5157 } },
            "WFP block uses provider decision and retains PID-only attribution");
        var following = await store.GetNetworkConnectionsAsync(new EventQuery(Limit: 3, Cursor: first.NextCursor));
        Check(following.Returned == 3 && following.NextCursor is null && following.Items[0] is
            { Decision: "allowed", Direction: "inbound", SourceEndpoint: { Role: "local", Port: 443 },
              DestinationEndpoint.Role: "remote" },
            "Paging combines sources in order and WFP preserves local endpoint roles for inbound events");
        Check(following.Items[1] is { Protocol: "udp", Direction: "inbound",
            SourceEndpoint.Role: "remote", DestinationEndpoint.Role: "local" },
            "Inbound Sysmon data assigns roles from the reported initiation");
        var scoped = await store.GetNetworkConnectionsAsync(new EventQuery(), ProcessGuid);
        Check(scoped.Items.Single() is { Decision: null, Protocol: "tcp",
            Process: { ProcessGuid: { }, Attribution: "processGuid", ProcessId: 78 },
            DestinationEndpoint: { Address: "2001:db8::7", Port: 443, Role: "remote" } },
            "GUID lookup excludes reused PIDs and unrelated providers before paging");

        var dns = await store.GetDnsQueriesAsync(new EventQuery(), ProcessGuid);
        Check(dns.Returned == 2 && dns.Items[0] is { QueryStatus: 9003, QueryResults: null } &&
            dns.Items[1] is { QueryName: "fixture.example", QueryStatus: 0,
                QueryResults: "type: 5 alias.example;2001:db8::7;" },
            "DNS preserves a failure, cached-capable source results and exact GUID matching");
        var dnsRange = await store.GetDnsQueriesAsync(new EventQuery(Channel: SysmonChannel,
            FromUtc: Epoch.AddSeconds(5), ToUtc: Epoch.AddSeconds(6)));
        Check(dnsRange.Items.Single().EventId == dns.Items[1].EventId &&
            (await store.GetByIdAsync(dns.Items[1].EventId))?.Category == "windows.event",
            "Network views retain evidence identity and standard event-summary behavior");

        var damaged = Fixture(SysmonChannel, SysmonProvider, 3, 10,
            ("DestinationPort", "70000"), ("DestinationIp", "invalid-ip"),
            ("ProcessGuid", ProcessGuid.ToString("B")), ("ProcessGuid", OtherGuid.ToString("B")));
        Check(WindowsNetworkEventDecoder.DecodeConnection(damaged) is { PayloadStatus: "unavailable",
            Process.Attribution: "unavailable", DestinationEndpoint: { Address: null, Port: null } },
            "Invalid and ambiguous input does not invent network or identity fields");
        var olderProtocol = Fixture(SecurityChannel, SecurityProvider, 5156, 11,
            ("Protocol", "1"), ("SourcePort", "8"), ("DestPort", "0"));
        Check(WindowsNetworkEventDecoder.DecodeConnection(olderProtocol) is { Protocol: "icmp",
            SourceEndpoint.Port: null, DestinationEndpoint.Port: null },
            "ICMP types are not described as TCP/UDP ports");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }
}
