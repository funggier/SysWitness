using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

internal static class LocalProcessCollectionSmoke
{
    public static async Task VerifyAsync(string databasePath)
    {
        var beforeAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var observedAt = beforeAt.AddSeconds(2);
        var before = new Dictionary<uint, ProcessSample>
        {
            [10] = new(10, "old", beforeAt),
            [11] = new(11, "unknown", null),
            [12] = new(12, "gone", beforeAt)
        };
        var now = new Dictionary<uint, ProcessSample>
        {
            [10] = new(10, "new", beforeAt.AddSeconds(1)),
            [11] = new(11, "unknown", beforeAt),
            [13] = new(13, "arrived", null)
        };
        var changes = WindowsProcessSnapshot.Compare(before, now, observedAt, "sample-host");
        Check(changes.Count == 4, "Snapshots detect known PID reuse without asserting that missing identity fields indicate reuse");
        Check(changes.All(e => e.OccurredAtUtc == observedAt && e.ObservedAtUtc == observedAt),
            "Snapshot evidence time is the time of observation");
        var wmi = LocalProcessEvidence.Create(LocalProcessEvidence.WmiChannel, LocalProcessEvidence.Start,
            "sample-host", beforeAt, observedAt, 22, "sample.exe", parentPid: 10);
        var stop = LocalProcessEvidence.Create(LocalProcessEvidence.WmiChannel, LocalProcessEvidence.Exit,
            "sample-host", beforeAt.AddSeconds(1), observedAt, 22, "sample.exe", exitStatus: 23);

        var store = new SqliteEventStore(databasePath);
        await store.InitializeAsync();
        await store.InsertAsync(changes.Concat([wmi, stop]));
        var reopened = new SqliteEventStore(databasePath);
        var projected = await reopened.GetProcessEventsAsync(new EventQuery(Channel: LocalProcessEvidence.SnapshotChannel));
        var oldInstance = projected.Items.Single(e => e.ProcessId == 10 && e.Kind == "exit");
        var newInstance = projected.Items.Single(e => e.ProcessId == 10 && e.Kind == "start");
        Check(oldInstance is { ObservationMethod: "snapshot", Quality: "derived", ProcessGuid: null } &&
            oldInstance.LocalProcessInstanceId is not null && newInstance.LocalProcessInstanceId is not null &&
            oldInstance.LocalProcessInstanceId != newInstance.LocalProcessInstanceId,
            "Snapshot evidence distinguishes reused PIDs after database restart");
        Check(projected.Items.Single(e => e.ProcessId == 13).LocalProcessInstanceId is null,
            "An unreadable creation time leaves local identity unknown");
        var departure = WindowsProcessSnapshot.Compare(now, before, observedAt.AddSeconds(3), "sample-host")
            .Select(LocalProcessEvidence.Decode).Single(e => e is { Kind: "exit", ProcessId: 10 });
        Check(departure?.LocalProcessInstanceId == newInstance.LocalProcessInstanceId,
            "A snapshot start and exit share the same local instance ID if the creation time was available");

        var native = await reopened.GetProcessEventsAsync(new EventQuery(Channel: LocalProcessEvidence.WmiChannel));
        Check(native.Items[0] is { Kind: "exit", ExitStatus: 23, ObservationMethod: "wmi", Quality: "observed" } &&
            native.Items[1] is { Kind: "start", ProcessId: 22, ProcessName: "sample.exe", Parent.ProcessId: 10,
                LocalProcessInstanceId: null }, "Native WMI lifecycle evidence preserves the reported fields");
        Check((await reopened.GetProcessEventsAsync(new EventQuery(), Guid.NewGuid())).Returned == 0,
            "Local observations are excluded from Sysmon GUID timelines");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }
}
