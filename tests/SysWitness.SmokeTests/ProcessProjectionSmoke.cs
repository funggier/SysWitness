using Microsoft.Data.Sqlite;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

internal static class ProcessProjectionSmoke
{
    private static readonly Guid Subject = Guid.Parse("12121212-2222-4333-8444-555555555555");
    private static readonly Guid Parent = Guid.Parse("34343434-2222-4333-8444-555555555555");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private static ObservedEvent Event(int number, string fields)
    {
        var xml = $"""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><EventRecordID>{number}</EventRecordID><Computer>projection-fixture-host</Computer></System>
              <EventData>{fields}</EventData>
            </Event>
            """;
        return new ObservedEvent(EventIdentity.FromXml(WindowsProcessEventDecoder.SysmonChannel, xml),
            Epoch.AddSeconds(number), Epoch.AddSeconds(number), "windows.event",
            WindowsProcessEventDecoder.SysmonChannel, WindowsProcessEventDecoder.SysmonProvider,
            1, number, "projection-fixture-host", xml);
    }

    public static async Task VerifyAsync(string path)
    {
        var store = new SqliteEventStore(path);
        await store.InitializeAsync();
        var first = Event(1, $"<Data Name='ProcessGuid'>{Subject:B}</Data><Data Name='ParentProcessGuid'>{Parent:B}</Data>");
        var malformed = Event(2, "<broken>");
        await store.InsertAsync([first, malformed]);
        var initial = await store.GetProcessProjectionStatusAsync();
        Check(initial is { Version: 1, EligibleEvents: 2, IndexedEvents: 2, PendingEvents: 0 },
            "Every eligible record is marked processed, including unavailable identities");
        Check((await store.GetProcessTreeAsync(Subject, "projection-fixture-host")).Relationships.Count == 1,
            "Indexed read retains exact source-backed parent relation");

        var committed = Event(3, $"<Data Name='ProcessGuid'>{Subject:D}</Data>");
        await store.CommitBatchAsync(committed.Channel, [committed], "<bookmark/>", 3);
        var rejected = Event(4, $"<Data Name='ProcessGuid'>{Parent:D}</Data>");
        var failed = false;
        try { await store.CommitBatchAsync(rejected.Channel, [rejected], "<bad-bookmark/>", 999); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && (await store.GetProcessProjectionStatusAsync()).IndexedEvents == 3 &&
            await store.GetByIdAsync(rejected.EventId) is null &&
            (await store.GetCheckpointAsync(committed.Channel))?.RecordId == 3,
            "Evidence, derived identity and bookmark roll back together");

        // Simulate an interrupted derived backfill without touching raw evidence or checkpoints.
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM process_identity_projection WHERE event_id=$id";
            cmd.Parameters.AddWithValue("$id", first.EventId);
            await cmd.ExecuteNonQueryAsync();
        }
        Check((await store.GetProcessProjectionStatusAsync()).PendingEvents == 1,
            "Partial projection is visible as pending work");
        var reopened = new SqliteEventStore(path);
        await reopened.InitializeAsync();
        Check((await reopened.GetProcessProjectionStatusAsync()) is { IndexedEvents: 3, PendingEvents: 0 } &&
            (await reopened.GetCheckpointAsync(committed.Channel))?.RecordId == 3,
            "Restart resumes backfill without replaying a source or changing its bookmark");
        Check((await reopened.GetProcessActivityAsync(new EventQuery(), Subject, "projection-fixture-host")).Returned == 2,
            "Backfilled activity preserves GUID and malformed-evidence semantics");

        var pruning = new SqliteEventStore(path, StoragePolicy.Default with { RetentionDays = 1 });
        await pruning.InitializeAsync();
        Check(await pruning.PruneExpiredAsync(Epoch.AddDays(3)) == 3 &&
            (await pruning.GetProcessProjectionStatusAsync()) is { EligibleEvents: 0, IndexedEvents: 0, PendingEvents: 0 } &&
            (await pruning.GetCheckpointAsync(committed.Channel))?.RecordId == 3,
            "Retention removes derived rows atomically and preserves source checkpoints");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
}
