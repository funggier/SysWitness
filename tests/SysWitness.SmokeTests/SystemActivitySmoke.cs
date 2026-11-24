using System.Text.Json;
using System.Xml.Linq;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;
using static SysWitness.Core.WindowsProcessEventDecoder;

internal static class SystemActivitySmoke
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const string Host = "system-activity-fixture";
    private const string Secret = "UNLISTED_SECRET_MUST_NOT_LEAK";
    private static readonly Guid ProcessGuid = Guid.Parse("cccccccc-2222-4333-8444-555555555555");

    private static ObservedEvent Fixture(string channel, string provider, int id, int record,
        params (string Name, string Value)[] fields)
    {
        var xml = new XElement(Ns + "Event",
            new XElement(Ns + "System",
                new XElement(Ns + "Provider", new XAttribute("Name", provider)),
                new XElement(Ns + "EventID", id), new XElement(Ns + "Version", 1),
                new XElement(Ns + "EventRecordID", record),
                new XElement(Ns + "Execution", new XAttribute("ProcessID", 99999)),
                new XElement(Ns + "Computer", Host)),
            new XElement(Ns + "EventData", fields.Select(f =>
                new XElement(Ns + "Data", new XAttribute("Name", f.Name), f.Value))))
            .ToString(SaveOptions.DisableFormatting);
        return new ObservedEvent(EventIdentity.FromXml(channel, xml), Epoch.AddSeconds(record % 4),
            Epoch.AddSeconds(100 + record), "windows.event", channel, provider, id, record, Host, xml);
    }

    public static async Task SeedAsync(string path)
    {
        var store = new SqliteEventStore(path);
        await store.InitializeAsync();
        ObservedEvent S(int id, int record, params (string, string)[] fields) =>
            Fixture(SysmonChannel, SysmonProvider, id, record, fields);
        ObservedEvent A(int id, int record, params (string, string)[] fields) =>
            Fixture(SecurityChannel, SecurityProvider, id, record, fields);
        await store.InsertAsync([
            S(11, 301, ("ProcessId", "44"), ("ProcessGuid", ProcessGuid.ToString("B")),
                ("Image", @"C:\fixture.exe"), ("TargetFilename", @"C:\fixture.txt"),
                ("UnexpectedSecret", Secret)),
            S(2, 302, ("TargetFilename", @"C:\fixture.txt"),
                ("CreationUtcTime", "2026-01-01 00:00:00.000"),
                ("PreviousCreationUtcTime", "2025-01-01 00:00:00.000")),
            A(4663, 303, ("ObjectType", "File"), ("ObjectName", @"C:\fixture.txt"),
                ("ProcessId", "0x2c"), ("ProcessName", @"C:\fixture.exe"),
                ("AccessMask", "0x1"), ("AccessList", "%%4416"), ("HandleId", "0x44")),
            A(4656, 304, ("ObjectType", "File"), ("ObjectName", @"C:\fixture.txt"),
                ("AccessMask", "0x10000")),
            S(13, 305, ("TargetObject", @"HKLM\Software\Fixture\Value"),
                ("Details", "DWORD (0x00000001)"), ("EventType", "SetValue")),
            A(4657, 306, ("ObjectName", @"\REGISTRY\MACHINE\Software\Fixture"),
                ("ObjectValueName", "Value"), ("OldValue", "old"), ("NewValue", "new"),
                ("OperationType", "%%1905"), ("ProcessId", "0x2d")),
            A(4663, 307, ("ObjectType", "Key"), ("ObjectName", @"\REGISTRY\MACHINE\Software"),
                ("AccessMask", "0x1")),
            A(4624, 308, ("SubjectUserName", "REPORTER"), ("TargetUserName", "SIGNED_IN"),
                ("TargetLogonId", "0x12"), ("ProcessId", "0x2e"), ("LogonType", "3"),
                ("IpAddress", "192.0.2.1"), ("AuthenticationPackageName", "NTLM")),
            A(4625, 309, ("TargetUserName", "FAILED_USER"), ("Status", "0xc000006d"),
                ("SubStatus", "0xc000006a")),
            A(4647, 310, ("TargetUserName", "SIGNED_IN"), ("TargetLogonId", "0x12")),
            A(4776, 311, ("TargetUserName", "LOCAL_USER"), ("PackageName", "NTLM"),
                ("Workstation", "SOURCE"), ("Status", "0x0")),
            A(4697, 312, ("SubjectUserName", "INSTALLER"), ("ServiceName", "fixture"),
                ("ServiceFileName", @"C:\fixture.exe -service"), ("ServiceType", "0x10"),
                ("ServiceStartType", "2"), ("ServiceAccount", "LocalSystem"), ("ProcessId", "8888")),
            Fixture("System", "Service Control Manager", 7045, 313,
                ("ServiceName", "fixture2"), ("ImagePath", @"C:\fixture2.exe"),
                ("ServiceType", "user mode service"), ("StartType", "demand start"),
                ("AccountName", "LocalSystem")),
            S(6, 314, ("ImageLoaded", @"C:\fixture.sys"), ("Signed", "true"),
                ("Signature", "Fixture publisher"), ("Hashes", "SHA256=example"), ("ProcessId", "8888")),
            A(4697, 315, ("ServiceName", "fixtureDriver"), ("ServiceType", "0x1"),
                ("ServiceFileName", @"C:\fixture.sys")),
            A(4698, 316, ("TaskName", @"\Fixture"),
                ("TaskContent", "<Task>" + Secret + "</Task>"), ("ClientProcessId", "47"),
                ("ParentProcessId", "45"), ("RpcCallClientLocality", "0")),
            A(4702, 317, ("TaskName", @"\Fixture"),
                ("TaskContentNew", "<Task>" + Secret + "</Task>")),
            A(6416, 318, ("DeviceId", @"USB\VID_FIXTURE\1"), ("DeviceDescription", "Fixture device"),
                ("ClassId", "{fixture}"), ("ClassName", "USB"), ("VendorIds", "VID_FIXTURE")),
            S(7, 319, ("ProcessId", "48"), ("Image", @"C:\fixture.exe"),
                ("ImageLoaded", @"C:\fixture.dll"), ("Signed", "false")),
            S(10, 320, ("SourceProcessId", "49"), ("SourceProcessGUID", ProcessGuid.ToString("B")),
                ("SourceImage", @"C:\source.exe"), ("TargetProcessId", "50"),
                ("TargetImage", @"C:\target.exe"), ("GrantedAccess", "0x1010"),
                ("SourceThreadId", "8"), ("CallTrace", "fixture.dll+0x1")),
            S(8, 321, ("SourceProcessId", "49"), ("TargetProcessId", "50"),
                ("NewThreadId", "9"), ("StartAddress", "0x1234")),
            S(11, 322, ("ProcessId", "51"), ("ProcessId", "52"), ("TargetFilename", @"C:\duplicate.txt")),
            S(11, 323, ("TargetFilename", @"C:\malformed.txt")) with { RawXml = "<broken>" },
            Fixture("System", "Unrelated-Provider", 11, 324, ("TargetFilename", @"C:\unrelated.txt")),
            Fixture(SecurityChannel, "Unrelated-Provider", 4663, 325, ("ObjectType", "File")),
            Fixture("Application", SysmonProvider, 11, 326, ("TargetFilename", @"C:\wrong-channel.txt")),
            A(4663, 327, ("ObjectType", "Process"), ("ObjectName", "not-a-file")),
            A(4660, 328, ("HandleId", "0x44"), ("ProcessId", "0x2c")),
            A(4663, 329, ("ObjectType", "File"), ("ObjectType", "Key"), ("ObjectName", "ambiguous"))
        ]);
    }

    public static async Task VerifyAsync(string path)
    {
        var ambiguous = WindowsSystemActivityDecoder.Decode(Fixture(SysmonChannel, SysmonProvider, 10, 330,
            ("SourceProcessGuid", ProcessGuid.ToString("B")), ("SourceProcessGUID", ProcessGuid.ToString("B")),
            ("TargetProcessId", "0xffffffff"), ("GrantedAccess", "-1")), "process-access")!;
        Check(ambiguous.Actor is null && ambiguous.ProcessAccess!.Target!.ProcessId == uint.MaxValue &&
            ambiguous.ProcessAccess.GrantedAccess is null,
            "Conflicting GUID spellings and invalid masks remain unknown; unsigned PID bounds are respected");
        var oversized = WindowsSystemActivityDecoder.Decode(Fixture(SysmonChannel, SysmonProvider, 11, 331,
            ("TargetFilename", new string('x', 8193))), "files")!;
        Check(oversized.File!.Path is null, "Oversized values are unavailable rather than silently truncated");
        await SeedAsync(path);
        var store = new SqliteEventStore(path);
        var files = await store.GetSystemActivityAsync(new EventQuery(), "files");
        Check(files.Returned == 6, "File sources and object-type boundaries apply before paging");
        var created = files.Items.Single(i => i.Source.SourceRecordId == 301);
        Check(created.File!.Operation == "file.createdOrOverwritten" && created.Actor!.ProcessId == 44 &&
            created.Actor.ProcessGuid == ProcessGuid && created.Actor.IdentityQuality == "processGuid",
            "Sysmon file actor identity is payload-derived");
        var used = files.Items.Single(i => i.Source.SourceRecordId == 303);
        Check(used.File!.Operation == "object.access.used" && used.File.AccessMask == 1 &&
            used.Actor!.ProcessId == 44, "Audit mask is retained without inventing a file-read claim");
        Check(files.Items.Single(i => i.Source.SourceRecordId == 304).File!.Operation == "object.access.requested",
            "Requested access is distinct from performed access and deletion");
        var duplicate = files.Items.Single(i => i.Source.SourceRecordId == 322);
        Check(duplicate.Actor is null || duplicate.Actor.ProcessId is null,
            "Duplicate payload process fields cannot become a trusted PID");
        var malformed = files.Items.Single(i => i.Source.SourceRecordId == 323);
        Check(malformed.PayloadStatus == "unavailable" && malformed.Actor is null &&
            malformed.File!.Path is null, "Malformed payload preserves provenance without invented details");

        var registry = await store.GetSystemActivityAsync(new EventQuery(), "registry");
        Check(registry.Returned == 3 &&
            registry.Items.Single(i => i.Source.SourceRecordId == 306).Registry!.ValueName == "Value" &&
            registry.Items.Single(i => i.Source.SourceRecordId == 307).Registry!.Operation == "object.access.used",
            "Registry value changes and key accesses remain distinct from file evidence");
        var auth = await store.GetSystemActivityAsync(new EventQuery(), "authentication");
        var login = auth.Items.Single(i => i.Source.SourceRecordId == 308);
        Check(auth.Returned == 4 && login.Subject!.Name == "REPORTER" &&
            login.Authentication!.Target!.Name == "SIGNED_IN" && login.Actor!.ProcessId == 46,
            "Authentication reporting identity and authenticated identity stay separate");
        Check(auth.Items.Single(i => i.Source.SourceRecordId == 310).Authentication!.Operation == "logoff.initiated" &&
            auth.Items.Single(i => i.Source.SourceRecordId == 311).Actor is null,
            "Logoff initiation and NTLM validation do not imply a session end or actor PID");
        var services = await store.GetSystemActivityAsync(new EventQuery(), "services");
        Check(services.Returned == 3 && services.Items.All(i => i.Actor is null) &&
            services.Items.Single(i => i.Source.SourceRecordId == 313).Service!.Name == "fixture2",
            "Service installation does not attribute provider execution as the installer process");
        var drivers = await store.GetSystemActivityAsync(new EventQuery(), "drivers");
        Check(drivers.Returned == 2 && drivers.Items.All(i => i.Actor is null) &&
            drivers.Items.Single(i => i.Source.SourceRecordId == 314).Driver!.Signed == true,
            "Driver load and driver-service install are source-specific with no invented PID");
        var tasks = await store.GetSystemActivityAsync(new EventQuery(), "tasks");
        Check(tasks.Returned == 2 && tasks.Items.Single(i => i.Source.SourceRecordId == 316).Actor!.ProcessId == 47 &&
            tasks.Items.All(i => i.Task!.DefinitionAvailable),
            "Task client PID is used only when supplied and task XML is not the public payload");
        Check((await store.GetSystemActivityAsync(new EventQuery(), "devices")).Items.Single()
            .Device!.InstanceId == @"USB\VID_FIXTURE\1", "Device recognition retains the instance identifier");
        Check((await store.GetSystemActivityAsync(new EventQuery(), "modules")).Items.Single()
            .Module!.ImagePath == @"C:\fixture.dll", "Module image is distinct from the loading process");
        var access = await store.GetSystemActivityAsync(new EventQuery(), "process-access");
        Check(access.Returned == 2 && access.Items.Single(i => i.Source.SourceRecordId == 320)
            .ProcessAccess!.GrantedAccess == 0x1010, "Process-open access mask does not claim memory transfer");

        foreach (var kind in WindowsSystemActivityDecoder.Kinds)
        {
            var page = await store.GetSystemActivityAsync(new EventQuery(), kind);
            var ids = new List<string>();
            string? cursor = null;
            do
            {
                var slice = await store.GetSystemActivityAsync(new EventQuery(Limit: 1, Cursor: cursor), kind);
                Check(slice.Returned == 1, "Source and payload family predicates precede paging");
                ids.Add(slice.Items.Single().EventId);
                cursor = slice.NextCursor;
                Check(ids.Count <= page.Returned, "Activity cursor terminates");
            } while (cursor is not null);
            Check(ids.SequenceEqual(page.Items.Select(i => i.EventId)) && ids.Distinct().Count() == page.Returned,
                "Tied occurrence timestamps page without duplication or omission");
            foreach (var item in page.Items)
            {
                Check(item.Actor?.ProcessId != 99999 && await store.GetByIdAsync(item.EventId) is not null &&
                    item.ObservedAtUtc > item.OccurredAtUtc, "Raw evidence identity and both times are retained");
                var count = new object?[] { item.File, item.Registry, item.Authentication, item.Service,
                    item.Driver, item.Task, item.Device, item.Module, item.ProcessAccess }.Count(x => x is not null);
                Check(count == 1 && item.Kind == kind, "Exactly one explicitly typed payload per activity");
            }
            var json = JsonSerializer.Serialize(page);
            Check(!json.Contains(Secret, StringComparison.Ordinal) && !json.Contains("RawXml", StringComparison.Ordinal) &&
                !json.Contains("TaskContent", StringComparison.Ordinal), "Unlisted fields and source XML cannot leak through projections");
        }
        Check((await store.GetSystemActivityAsync(new EventQuery(Channel: "Application"), "files")).Returned == 0 &&
            (await store.GetSystemActivityAsync(new EventQuery(Provider: "' OR 1=1 --"), "files")).Returned == 0,
            "Source filters intersect the permitted family and SQL binds values");
        Check((await store.GetSystemActivityAsync(new EventQuery(SourceEventId: 4663), "files")).Returned == 1,
            "Source-ID filtering preserves object-type classification");
        await RejectAsync(() => store.GetSystemActivityAsync(new EventQuery(), "unknown"));
        await RejectAsync(() => store.GetSystemActivityAsync(new EventQuery(Category: "windows.event"), "files"));
        await RejectAsync(() => store.GetSystemActivityAsync(new EventQuery(Cursor: "invalid"), "files"));
    }

    private static async Task RejectAsync(Func<Task<SystemActivityPage>> action)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("FAIL: Invalid system activity query was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
}
