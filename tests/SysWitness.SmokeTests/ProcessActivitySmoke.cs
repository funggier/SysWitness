using System.Xml.Linq;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Security;
using SysWitness.Storage;
using static SysWitness.Core.WindowsProcessEventDecoder;

internal static class ProcessActivitySmoke
{
    private static readonly Guid Subject = Guid.Parse("aaaaaaaa-2222-4333-8444-555555555555");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-2222-4333-8444-555555555555");
    private const string Host = "activity-fixture-host";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static ObservedEvent Fixture(int id, int record, int second, string? computer,
        params (string Name, string Value)[] fields)
    {
        var xml = new XElement(Ns + "Event",
            new XElement(Ns + "System", new XElement(Ns + "Computer", computer),
                new XElement(Ns + "EventRecordID", record)),
            new XElement(Ns + "EventData", fields.Select(field =>
                new XElement(Ns + "Data", new XAttribute("Name", field.Name), field.Value))))
            .ToString(SaveOptions.DisableFormatting);
        return new ObservedEvent(EventIdentity.FromXml(SysmonChannel, xml), Epoch.AddSeconds(second),
            Epoch.AddSeconds(second + 1), "windows.event", SysmonChannel, SysmonProvider, id,
            record, computer, xml);
    }

    public static async Task SeedAsync(string path)
    {
        var store = new SqliteEventStore(path);
        await store.InitializeAsync();
        var otherHost = Fixture(1, 5, 4, "other-fixture-host",
            ("ProcessGuid", Subject.ToString("B")), ("ProcessId", "78"));
        var otherProvider = Fixture(3, 9, 8, Host, ("ProcessGuid", Subject.ToString("B"))) with
            { Provider = "Unrelated-Provider" };
        var pidOnly = Fixture(3, 10, 9, Host, ("ProcessId", "78"));
        var security = Fixture(5156, 11, 10, Host, ("ProcessID", "78")) with
            { Channel = SecurityChannel, Provider = SecurityProvider };
        await store.InsertAsync([
            Fixture(1, 1, 1, Host, ("ProcessGuid", Subject.ToString("B")), ("ProcessId", "78"),
                ("CommandLine", "fixture.exe --example"), ("ParentProcessGuid", Other.ToString("B"))),
            Fixture(22, 2, 2, Host, ("ProcessGuid", Subject.ToString("D")), ("ProcessId", "78"),
                ("QueryName", "activity.example"), ("QueryStatus", "9003")),
            Fixture(3, 3, 2, Host, ("ProcessGuid", Subject.ToString("D").ToUpperInvariant()),
                ("ProcessId", "78"), ("Protocol", "tcp"), ("DestinationIp", "203.0.113.7"),
                ("DestinationPort", "443")),
            Fixture(5, 4, 3, Host, ("ProcessGuid", Subject.ToString("D")), ("ProcessId", "78")),
            otherHost,
            Fixture(22, 6, 5, null, ("ProcessGuid", Subject.ToString("B"))),
            Fixture(22, 7, 6, Host, ("ProcessGuid", Other.ToString("B")), ("ProcessId", "78"),
                ("QueryResults", Subject.ToString("D"))),
            Fixture(1, 8, 7, Host, ("ProcessGuid", Other.ToString("B")),
                ("ParentProcessGuid", Subject.ToString("B")), ("CommandLine", Subject.ToString("D"))),
            otherProvider, pidOnly, security,
            Fixture(22, 12, 11, Host, ("ProcessGuid", Subject.ToString("D")),
                ("ProcessGuid", Other.ToString("D"))),
            Fixture(22, 13, 12, Host, ("ProcessGuid", Subject.ToString("D"))) with { RawXml = "<broken>" },
            LocalProcessEvidence.Create(LocalProcessEvidence.SnapshotChannel, LocalProcessEvidence.Start,
                Host, Epoch.AddSeconds(13), Epoch.AddSeconds(13), 78, "fixture.exe",
                startedAt: Epoch.AddSeconds(1))
        ]);
    }

    public static async Task VerifyAsync(string path)
    {
        await SeedAsync(path);
        // Query through a fresh store, with no migration or ingest replay required.
        var store = new SqliteEventStore(path);
        var page = await store.GetProcessActivityAsync(new EventQuery(), Subject, Host);
        Check(page.Returned == 4 && page.NextCursor is null && page.Quality == "derived" &&
            page.CorrelationMethod == ProcessActivityProjection.CorrelationMethod &&
            page.ProcessGuid == Subject && page.Computer == Host && page.Limitations.Count >= 5,
            "Activity groups retained source identity with explicit derivation and limitations");
        Check(page.Items.Select(item => item.Kind).ToHashSet().SetEquals(
            ["process.start", "process.exit", "dns.query", "network.connection"]),
            "Activity combines lifecycle, DNS and network evidence");
        foreach (var item in page.Items)
        {
            Check((item.Lifecycle is not null ? 1 : 0) + (item.Connection is not null ? 1 : 0) +
                (item.DnsQuery is not null ? 1 : 0) == 1, "Each activity has exactly one typed payload");
            Check((await store.GetByIdAsync(item.EventId)) is not null &&
                item.ObservedAtUtc > item.OccurredAtUtc, "Every activity retains evidence identity and both times");
        }
        Check(page.Items.Single(item => item.DnsQuery is not null).DnsQuery!.QueryStatus == 9003 &&
            page.Items.Single(item => item.Connection is not null).Connection!.Decision is null,
            "Failed DNS and connection evidence are preserved without a fabricated success or causal claim");
        Check(page.Items[0].Kind == "process.exit" && page.Items[^1].Kind == "process.start",
            "Occurrence time determines the displayed order");

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var slice = await store.GetProcessActivityAsync(new EventQuery(Limit: 1, Cursor: cursor), Subject, Host);
            Check(slice.Returned == 1, "Host and subject filters precede pagination");
            seen.Add(slice.Items.Single().EventId);
            cursor = slice.NextCursor;
            Check(seen.Count <= 4, "Activity pagination terminates");
        } while (cursor is not null);
        Check(seen.SequenceEqual(page.Items.Select(item => item.EventId)) && seen.Distinct().Count() == 4,
            "Paging across tied timestamps has no omissions, duplicates or unrelated identities");
        Check((await store.GetProcessActivityAsync(new EventQuery(), Subject, "other-fixture-host")).Returned == 1 &&
            (await store.GetProcessActivityAsync(new EventQuery(), Subject, Host.ToUpperInvariant())).Returned == 0 &&
            (await store.GetProcessActivityAsync(new EventQuery(), Guid.NewGuid(), Host)).Returned == 0,
            "Host equality is exact, unknown identities are empty, and hosts never leak into each other");
        var bounded = await store.GetProcessActivityAsync(new EventQuery(
            FromUtc: Epoch.AddSeconds(2), ToUtc: Epoch.AddSeconds(3)), Subject, Host);
        Check(bounded.Returned == 2 && bounded.Items.All(item => item.Lifecycle is null),
            "Inclusive/exclusive time filters apply before pagination");
        Check((await store.GetProcessActivityAsync(new EventQuery(SourceEventId: 22), Subject, Host))
            .Items.Single().Kind == "dns.query", "Source filters retain their meaning in the combined feed");
        Check((await store.GetProcessEventsAsync(new EventQuery(), Subject)).Returned == 3,
            "Existing lifecycle timeline remains unchanged, including its historical host semantics");

        await RejectAsync(() => store.GetProcessActivityAsync(new EventQuery(), Guid.Empty, Host));
        await RejectAsync(() => store.GetProcessActivityAsync(new EventQuery(), Subject, ""));
        await RejectAsync(() => store.GetProcessActivityAsync(new EventQuery(), Subject, "bad\nhost"));
        await RejectAsync(() => store.GetProcessActivityAsync(new EventQuery(), Subject, new string('a', 257)));
        await RejectAsync(() => store.GetProcessActivityAsync(new EventQuery(Cursor: "invalid"), Subject, Host));
        await RejectAsync(() => store.GetProcessActivityAsync(new EventQuery(Category: "windows.event"), Subject, Host));
        Check((await store.GetProcessActivityAsync(new EventQuery(), Subject, "' OR 1=1 --")).Returned == 0,
            "Computer names bind as data, never SQL");
        var route = "/api/v1/processes/" + Subject.ToString("D") + "/activity";
        Check(ReadOnlyClientRegistry.RequiredScopes(route)!.SequenceEqual(
            [ReadOnlyClientRegistry.ProcessesRead, ReadOnlyClientRegistry.NetworkRead]) &&
            ReadOnlyClientRegistry.RequiredScopes(route + "/extra") is null &&
            ReadOnlyClientRegistry.RequiredScope(route) is null,
            "Composite activity scope is explicit and single-scope classification fails closed");
    }

    private static async Task RejectAsync(Func<Task<ProcessActivityPage>> action)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("FAIL: Invalid activity query was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
}
