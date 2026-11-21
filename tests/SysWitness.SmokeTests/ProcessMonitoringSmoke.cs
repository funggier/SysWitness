using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

internal static class ProcessMonitoringSmoke
{
    private const string Sysmon = WindowsProcessEventDecoder.SysmonChannel;
    private const string SysmonProvider = WindowsProcessEventDecoder.SysmonProvider;
    private const string Security = WindowsProcessEventDecoder.SecurityChannel;
    private const string SecurityProvider = WindowsProcessEventDecoder.SecurityProvider;
    private static readonly Guid StartGuid = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid OtherGuid = Guid.Parse("66666666-2222-4333-8444-555555555555");

    private static ObservedEvent Fixture(string channel, string provider, int eventNumber, int second, string data)
    {
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(second);
        var xml = $"""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="{provider}" /><EventID>{eventNumber}</EventID><EventRecordID>{second}</EventRecordID></System>
              <EventData>{data}</EventData>
            </Event>
            """;
        return new ObservedEvent(EventIdentity.FromXml(channel, xml), timestamp,
            timestamp.AddSeconds(1), "windows.event", channel, provider, eventNumber, second, "synthetic-host", xml);
    }

    public static async Task SeedAsync(string databasePath)
    {
        var store = new SqliteEventStore(databasePath);
        await store.InitializeAsync();
        var fixtures = new[]
        {
            Fixture(Sysmon, SysmonProvider, 1, 1, $$"""
                <Data Name="CommandLine">"test.exe" a&amp;b</Data>
                <Data Name="Image">C:\Fixture\test.exe</Data>
                <Data Name="ParentProcessId">40</Data>
                <Data Name="ParentProcessGuid">{{OtherGuid:B}}</Data>
                <Data Name="ParentImage">C:\Fixture\parent.exe</Data>
                <Data Name="ProcessId">78</Data>
                <Data Name="ProcessGuid">{{StartGuid:B}}</Data>
                """),
            Fixture(Sysmon, SysmonProvider, 5, 2, $$"""
                <Data Name="ProcessGuid">{{StartGuid:D}}</Data>
                <Data Name="ProcessId">78</Data>
                """),
            Fixture(Sysmon, SysmonProvider, 1, 3, $$"""
                <Data Name="ProcessGuid">{{OtherGuid:B}}</Data>
                <Data Name="ParentProcessGuid">{{StartGuid:B}}</Data>
                <Data Name="CommandLine">child.exe {{StartGuid:D}}</Data>
                <Data Name="ProcessId">78</Data>
                """),
            Fixture(Security, SecurityProvider, 4688, 4, """
                <Data Name="NewProcessId">0x4e</Data>
                <Data Name="ProcessId">0X28</Data>
                <Data Name="NewProcessName">C:\Fixture\audit.exe</Data>
                <Data Name="ParentProcessName">C:\Fixture\parent.exe</Data>
                <Data Name="CommandLine" />
                """),
            Fixture(Security, SecurityProvider, 4689, 5, """
                <Data Name="ProcessId">0x4e</Data>
                <Data Name="ProcessName">C:\Fixture\audit.exe</Data>
                <Data Name="Status">0xC0000005</Data>
                """),
            Fixture(Sysmon, SysmonProvider, 1, 6, "<broken>"),
            Fixture(Sysmon, SysmonProvider, 3, 7, $$"""<Data Name="ProcessGuid">{{StartGuid:B}}</Data>"""),
            Fixture(Sysmon, "Unrelated-Provider", 1, 8, $$"""<Data Name="ProcessGuid">{{StartGuid:B}}</Data>"""),
            Fixture("System", SysmonProvider, 1, 9, $$"""<Data Name="ProcessGuid">{{StartGuid:B}}</Data>""")
        };
        await store.InsertAsync(fixtures);
    }

    public static async Task VerifyAsync(string databasePath)
    {
        await SeedAsync(databasePath);

        // Restart against existing evidence. The general event API keeps its previous shape and categories.
        var store = new SqliteEventStore(databasePath);
        await store.InitializeAsync();
        var feed = await store.GetProcessEventsAsync(new EventQuery());
        Check(feed.Returned == 6 && feed.Items[0] is { Kind: "start", PayloadStatus: "unavailable",
            ProcessGuid: null, ProcessId: null }, "Malformed data is surfaced without invented process fields");
        var exit = feed.Items[1];
        Check(exit is { Kind: "exit", ProcessId: 78, ProcessGuid: null, ExitStatus: 0xC0000005,
            Parent: null } && exit.Source is { Channel: Security, SourceEventId: 4689 },
            "Audit exit retains source-backed PID and status");
        var start = feed.Items[2];
        Check(start is { ProcessId: 78, ProcessGuid: null, CommandLine: null, Parent.ProcessId: 40 } &&
            start.Parent.ImagePath == @"C:\Fixture\parent.exe", "Audit creation distinguishes creator and new process");

        var timeline = await store.GetProcessEventsAsync(new EventQuery(Limit: 1), StartGuid);
        Check(timeline.Returned == 1 && timeline.Items[0].Kind == "exit" && timeline.NextCursor is not null,
            "Page one of a GUID timeline contains the matching exit");
        var second = await store.GetProcessEventsAsync(new EventQuery(Limit: 1, Cursor: timeline.NextCursor), StartGuid);
        var process = second.Items.Single();
        Check(process is { Kind: "start", ProcessId: 78, Parent.ProcessId: 40, CommandLine: "\"test.exe\" a&b" } &&
            process.ProcessGuid == StartGuid && process.Parent.ProcessGuid == OtherGuid &&
            process.ImagePath == @"C:\Fixture\test.exe" &&
            process.Source is { Computer: "synthetic-host", SourceRecordId: 1 } &&
            process.ObservedAtUtc > process.OccurredAtUtc && second.NextCursor is null,
            "Exact GUID timeline excludes PID reuse, child events, unrelated providers and command line mentions");
        Check((await store.GetByIdAsync(process.EventId))?.Category == "windows.event",
            "Projection leaves the saved event summary readable under its original ID and category");

        var auditOnly = await store.GetProcessEventsAsync(new EventQuery(Channel: Security, SourceEventId: 4688));
        Check(auditOnly.Items.Single().EventId == start.EventId, "Channel and source event filters apply to process feed");
        var sysmonOnly = await store.GetPageAsync(new EventQuery(Channel: Sysmon));
        Check(sysmonOnly.Returned == 6 && sysmonOnly.Items.All(item => item.Channel == Sysmon),
            "Existing event API accepts the native Sysmon channel name");
        var filtered = await store.GetProcessEventsAsync(new EventQuery(FromUtc: process.OccurredAtUtc.AddSeconds(1),
            ToUtc: process.OccurredAtUtc.AddSeconds(2)), StartGuid);
        Check(filtered.Items.Single().Kind == "exit", "Timeline applies inclusive/exclusive time bounds");
        Check((await store.GetProcessEventsAsync(new EventQuery(), Guid.NewGuid())).Returned == 0,
            "Unknown GUID returns an empty page");

        var ambiguous = Fixture(Sysmon, SysmonProvider, 1, 10, $$"""
            <Data Name="ProcessGuid">{{StartGuid:B}}</Data>
            <Data Name="ProcessGuid">{{OtherGuid:B}}</Data>
            <Data Name="ProcessId">-1</Data>
            """);
        Check(WindowsProcessEventDecoder.Decode(ambiguous) is { ProcessGuid: null, ProcessId: null },
            "Duplicate or invalid identity fields are unavailable");
        var unsafeXml = ambiguous with { RawXml =
            "<!DOCTYPE Event [<!ENTITY sample 'fixture'>]>" + ambiguous.RawXml };
        Check(WindowsProcessEventDecoder.Decode(unsafeXml)?.PayloadStatus == "unavailable",
            "DTD-bearing event payloads are rejected");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }
}
