using System.Xml.Linq;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Security;
using SysWitness.Storage;
using static SysWitness.Core.WindowsProcessEventDecoder;

internal static class ProcessTreeSmoke
{
    private static readonly Guid Root = Guid.Parse("cccccccc-2222-4333-8444-555555555555");
    private static readonly Guid Child = Guid.Parse("dddddddd-2222-4333-8444-555555555555");
    private static readonly Guid Grandchild = Guid.Parse("eeeeeeee-2222-4333-8444-555555555555");
    private static readonly Guid Parent = Guid.Parse("ffffffff-2222-4333-8444-555555555555");
    private static readonly Guid Sibling = Guid.Parse("12345678-2222-4333-8444-555555555555");
    private const string Host = "tree-fixture-host";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static ObservedEvent Fixture(int record, Guid? child, Guid? parent, string? host = Host,
        bool ambiguous = false)
    {
        var fields = new List<XElement>
        {
            new(Ns + "Data", new XAttribute("Name", "ProcessId"), "78"),
            new(Ns + "Data", new XAttribute("Name", "ParentProcessId"), "78"),
            new(Ns + "Data", new XAttribute("Name", "CommandLine"), "fixture.exe " + Root.ToString("D"))
        };
        if (child is not null) fields.Add(new(Ns + "Data", new XAttribute("Name", "ProcessGuid"), child.Value.ToString("B")));
        if (parent is not null) fields.Add(new(Ns + "Data", new XAttribute("Name", "ParentProcessGuid"), parent.Value.ToString("D")));
        if (ambiguous) fields.Add(new(Ns + "Data", new XAttribute("Name", "ProcessGuid"), Parent.ToString("D")));
        var xml = new XElement(Ns + "Event",
            new XElement(Ns + "System", new XElement(Ns + "Provider", new XAttribute("Name", SysmonProvider)),
                new XElement(Ns + "EventID", 1), new XElement(Ns + "EventRecordID", record),
                new XElement(Ns + "Computer", host)),
            new XElement(Ns + "EventData", fields)).ToString(SaveOptions.DisableFormatting);
        return new ObservedEvent(EventIdentity.FromXml(SysmonChannel, xml), Epoch.AddSeconds(record),
            Epoch.AddSeconds(record + 1), "windows.event", SysmonChannel, SysmonProvider, 1,
            record, host, xml);
    }

    public static async Task SeedAsync(string path)
    {
        var store = new SqliteEventStore(path);
        await store.InitializeAsync();
        await store.InsertAsync([
            Fixture(1, Root, Parent), Fixture(2, Child, Root), Fixture(3, Grandchild, Child),
            Fixture(4, Sibling, Root),
            Fixture(5, Root, Grandchild, "other-tree-host"),
            Fixture(6, Guid.Parse("87654321-2222-4333-8444-555555555555"), null),
            Fixture(7, Grandchild, Root) with { Provider = "OtherProvider" },
            Fixture(8, Grandchild, Root) with { Channel = "Application" },
            LocalProcessEvidence.Create(LocalProcessEvidence.SnapshotChannel, LocalProcessEvidence.Start,
                Host, Epoch, Epoch, 78, "fixture.exe", parentPid: 78, startedAt: Epoch)
        ]);
    }

    public static async Task VerifyAsync(string path)
    {
        await SeedAsync(path);
        var store = new SqliteEventStore(path);
        var tree = await store.GetProcessTreeAsync(Root, Host);
        Check(tree.Nodes.Count == 5 && tree.Relationships.Count == 4 && tree.SourceEventsUsed == 4 &&
            !tree.HasCycle && !tree.EvidenceLimitReached && !tree.DepthLimitReached &&
            !tree.NodeLimitReached && tree.Unresolved.Count == 0 && tree.Quality == "derived",
            "Tree combines exact source GUID relationships across descendant levels");
        Check(tree.Nodes.Single(node => node.ProcessGuid == Parent) is
            { Depth: null, EvidenceState: "referenceOnly", Creations.Count: 0 } &&
            tree.Nodes.Single(node => node.ProcessGuid == Grandchild).Depth == 2,
            "Parent references do not invent a creation observation and descendants retain depth");
        foreach (var edge in tree.Relationships)
        {
            Check(edge.Evidence.ProcessGuid == edge.ChildProcessGuid &&
                edge.Evidence.Parent!.ProcessGuid == edge.ParentProcessGuid &&
                edge.Evidence.Source.Computer == Host && edge.Basis == "sourceReported" &&
                (await store.GetByIdAsync(edge.Evidence.EventId)) is not null,
                "Every edge carries the child creation evidence and original source");
        }
        var shallow = await store.GetProcessTreeAsync(Root, Host, maxDepth: 0);
        Check(shallow.DepthLimitReached && shallow.Nodes.Count == 2 && shallow.Relationships.Count == 1,
            "Depth zero includes root and reported parent but reports omitted descendants");
        var tiny = await store.GetProcessTreeAsync(Root, Host, nodeLimit: 1);
        Check(tiny.NodeLimitReached && tiny.Nodes.Count == 1 && tiny.Relationships.Count == 0 &&
            tiny.Nodes[0].Creations.Count == 1, "Node limits bound output without dropping root evidence");
        var unknown = await store.GetProcessTreeAsync(Guid.NewGuid(), Host);
        Check(unknown.Nodes.Single().EvidenceState == "referenceOnly" && unknown.SourceEventsUsed == 0,
            "Unknown identity is a requested reference, not proof of a process");
        Check((await store.GetProcessTreeAsync(Root, Host.ToUpperInvariant())).SourceEventsUsed == 0 &&
            (await store.GetProcessTreeAsync(Root, "' OR 1=1 --")).SourceEventsUsed == 0,
            "Host identity is exact and binds as data");

        // Conflicts and cycles must remain visible; PID equality never supplies a GUID.
        var conflict = new ProcessTreeBuilder(Root, Host);
        conflict.Add(WindowsProcessEventDecoder.Decode(Fixture(10, Child, Root))!);
        conflict.Add(WindowsProcessEventDecoder.Decode(Fixture(11, Child, Parent))!);
        conflict.Add(WindowsProcessEventDecoder.Decode(Fixture(12, Root, null))!);
        conflict.Add(WindowsProcessEventDecoder.Decode(Fixture(13, null, Root))!);
        conflict.Add(WindowsProcessEventDecoder.Decode(Fixture(14, Child, Root, ambiguous: true))!);
        var ambiguous = conflict.Snapshot(false);
        Check(ambiguous.Nodes.Single(node => node.ProcessGuid == Child).HasConflictingParents &&
            ambiguous.Relationships.Count == 2 && ambiguous.Unresolved.Count == 3,
            "Conflicting parent GUIDs, missing identity and duplicate fields are not silently resolved");
        var cycleStore = new SqliteEventStore(path + "-cycle");
        await cycleStore.InitializeAsync();
        await cycleStore.InsertAsync([Fixture(20, Root, Child), Fixture(21, Child, Root)]);
        var cyclic = await cycleStore.GetProcessTreeAsync(Root, Host);
        Check(cyclic.HasCycle && cyclic.Nodes.Count == 2 && cyclic.Relationships.Count == 2,
            "Source cycles are flagged and bounded traversal terminates");
        var self = new ProcessTreeBuilder(Root, Host);
        self.Add(WindowsProcessEventDecoder.Decode(Fixture(22, Root, Root))!);
        Check(self.Snapshot(false) is { HasCycle: true, Relationships.Count: 0, Unresolved.Count: 1 },
            "Self-parent evidence is unresolved and explicitly flagged");

        // More than the fixed evidence budget is reported even when the output node limit is small.
        var budgetStore = new SqliteEventStore(path + "-budget");
        await budgetStore.InitializeAsync();
        var many = Enumerable.Range(1, ProcessTreeBuilder.SourceEventLimit + 2)
            .Select(i => Fixture(100 + i, Guid.Parse(i.ToString("x8") + "-4444-4333-8444-555555555555"), Root)).ToArray();
        await budgetStore.InsertAsync(many);
        var bounded = await budgetStore.GetProcessTreeAsync(Root, Host, nodeLimit: 5);
        Check(bounded.EvidenceLimitReached && bounded.SourceEventsUsed == 1000 &&
            bounded.Nodes.Count <= 5 && bounded.NodeLimitReached,
            "Fixed source-event budget has explicit truncation and bounded node output");

        await RejectAsync(() => store.GetProcessTreeAsync(Guid.Empty, Host));
        await RejectAsync(() => store.GetProcessTreeAsync(Root, ""));
        await RejectAsync(() => store.GetProcessTreeAsync(Root, "bad\nhost"));
        await RejectAsync(() => store.GetProcessTreeAsync(Root, new string('x', 257)));
        await RejectAsync(() => store.GetProcessTreeAsync(Root, Host, maxDepth: 9));
        await RejectAsync(() => store.GetProcessTreeAsync(Root, Host, nodeLimit: 0));
        await RejectAsync(() => store.GetProcessTreeAsync(Root, Host, nodeLimit: 257));
        var route = "/api/v1/processes/" + Root.ToString("D") + "/tree";
        Check(ReadOnlyClientRegistry.RequiredScopes(route)!.SequenceEqual([ReadOnlyClientRegistry.ProcessesRead]) &&
            ReadOnlyClientRegistry.RequiredScopes(route + "/kill") is null,
            "Tree requires process read permission without adding control paths");
    }

    private static async Task RejectAsync(Func<Task<ProcessTree>> action)
    {
        try { await action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("FAIL: Invalid tree query accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
}
