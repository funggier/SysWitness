using Microsoft.Data.Sqlite;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}

Require(WindowsEventLogChannels.ParseEnabled(null).SequenceEqual(["System", "Application"]),
    "Default channel configuration is System and Application");
Require(WindowsEventLogChannels.ParseEnabled("System, Application, application").SequenceEqual(
    ["System", "Application"]), "Channel allowlist canonicalizes and deduplicates");
bool invalidChannel = false;
try { WindowsEventLogChannels.ParseEnabled("System,NotARealChannel"); }
catch (ArgumentException) { invalidChannel = true; }
Require(invalidChannel, "Unknown local channel configurations are rejected");

var xml = "<Event><Data>test</Data></Event>";
var idA = EventIdentity.FromXml("System", xml);
Require(idA == EventIdentity.FromXml("System", xml), "Deterministic event identity");
Require(idA != EventIdentity.FromXml("System", "<Event><Data>other</Data></Event>"),
    "Different source content gets a distinct ID");

var temp = Path.Combine(Path.GetTempPath(), "SysWitness-Smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var path = Path.Combine(temp, "events.sqlite");
try
{
    var store = new SqliteEventStore(path);
    await store.InitializeAsync();
    var timestamp = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
    ObservedEvent MakeEvent(int n, string channel = "System") =>
        new(EventIdentity.FromXml(channel, "<Event><Id>" + n + "</Id></Event>"),
            timestamp, timestamp, "windows.event", channel, "fixture", n, n, null,
            "<Event><Id>" + n + "</Id></Event>");

    var initial = MakeEvent(1);
    Require(await store.InsertAsync(new[] { initial }) == 1, "Insert fixture");
    Require(await store.InsertAsync(new[] { initial }) == 0, "Deduplication");
    Require(await store.GetCheckpointAsync("System") is null, "Fixture insert never advances collector");

    var batch = new[] { MakeEvent(2), MakeEvent(3), MakeEvent(4) };
    var nativeBookmark = "<BookmarkList><Bookmark Channel='System' RecordId='4'/></BookmarkList>";
    Require(await store.CommitBatchAsync("System", batch, nativeBookmark, 4) == 3, "Commit batch");
    var checkpoint = await store.GetCheckpointAsync("System");
    Require(checkpoint?.RecordId == 4 && checkpoint.BookmarkXml == nativeBookmark,
        "Bookmark and events persisted atomically");

    // A fresh instance must read the same saved bookmark after process restart.
    var restoredStore = new SqliteEventStore(path);
    await restoredStore.InitializeAsync();
    Require((await restoredStore.GetCheckpointAsync("System"))?.BookmarkXml == nativeBookmark,
        "Bookmark survives reopening SQLite");

    var invalid = new ObservedEvent(EventIdentity.FromXml("System", "invalid"),
        timestamp, timestamp, "windows.event", "System", "fixture", 8, 8, null, null!);
    var threw = false;
    try
    {
        await restoredStore.CommitBatchAsync("System", new[] { MakeEvent(5), invalid },
            "<BookmarkList><Bookmark RecordId='8'/></BookmarkList>", 8);
    }
    catch (Exception ex) when (ex is SqliteException or InvalidOperationException) { threw = true; }
    Require(threw, "Simulated failed disk write must fail transaction");
    Require((await restoredStore.GetCheckpointAsync("System"))?.RecordId == 4,
        "Failed transaction cannot advance bookmark");
    Require(await restoredStore.GetByIdAsync(MakeEvent(5).EventId) is null,
        "Failed transaction rolls back earlier records");

    Require(await restoredStore.CommitBatchAsync("System",
        new[] { MakeEvent(5) }, "<BookmarkList><Bookmark RecordId='5'/></BookmarkList>",
        5, "fixture_gap", 4, new EventLogWindow(5, 5)) == 1, "Commit with gap");
    Require(await restoredStore.CountGapsAsync("System") == 1, "Coverage gap is durable");
    var gaps = await restoredStore.GetGapsAsync("System");
    Require(gaps[0].Reason == "fixture_gap" && gaps[0].PreviousRecordId == 4,
        "Coverage gap metadata retained");

    // A second source writes to the same store but never advances the first channel.
    var applicationEvent = MakeEvent(99, "Application");
    var applicationBookmark = "<BookmarkList><Bookmark Channel='Application' RecordId='99'/></BookmarkList>";
    Require(await restoredStore.CommitBatchAsync("Application", [applicationEvent],
        applicationBookmark, 99) == 1, "Second source writes independent evidence");
    Require((await restoredStore.GetCheckpointAsync("Application"))?.BookmarkXml ==
        applicationBookmark, "Second source persists independent native bookmark");
    Require((await restoredStore.GetCheckpointAsync("System"))?.RecordId == 5,
        "Application checkpoint cannot alter System checkpoint");
    Require(await restoredStore.CountGapsAsync("Application") == 0,
        "Gap histories are independently scoped by channel");

    var wrongChannelRejected = false;
    try
    {
        await restoredStore.CommitBatchAsync("System", [MakeEvent(100, "Application")],
            applicationBookmark, 100);
    }
    catch (ArgumentException) { wrongChannelRejected = true; }
    Require(wrongChannelRejected, "Mixed-channel commit is rejected atomically");
    Require((await restoredStore.GetCheckpointAsync("System"))?.RecordId == 5,
        "Rejected mixed-channel commit preserves first-source bookmark");

    var remaining = Enumerable.Range(6, 8).Select(i => MakeEvent(i)).ToArray();
    await restoredStore.InsertAsync(remaining);
    Require(await restoredStore.InsertAsync([applicationEvent]) == 0,
        "Duplicate second-source event does not overwrite evidence");

    var seen = new HashSet<string>();
    string? cursor = null;
    var loops = 0;
    do
    {
        var page = await restoredStore.GetPageAsync(3, cursor, "System");
        Require(page.Items.Count <= 3, "Page limited");
        foreach (var item in page.Items)
        {
            Require(seen.Add(item.EventId), "Pagination has no duplicated events");
            Require(item.Channel == "System", "Channel filter applied");
        }
        cursor = page.NextCursor;
        Require(++loops < 20, "Pagination terminates");
    } while (cursor is not null);
    Require(seen.Count == 13, "All 13 System events reachable with stable cursor");

    // Structured filtering must never interpret user input as a SQL expression.
    Require((await restoredStore.GetPageAsync(new EventQuery(
        Limit: 50, Channel: "System", Provider: "fixture", SourceEventId: 7))).Returned == 1,
        "Provider and source-event-ID filtering");
    Require((await restoredStore.GetPageAsync(new EventQuery(
        Limit: 50, Category: "windows.event", ToUtc: timestamp))).Returned == 0,
        "UTC upper bound is exclusive");
    Require((await restoredStore.GetPageAsync(new EventQuery(
        Limit: 50, FromUtc: timestamp.AddMinutes(1)))).Returned == 0,
        "UTC lower bound applied");
    Require((await restoredStore.GetPageAsync(new EventQuery(
        Limit: 50, Provider: "' OR 1=1 --"))).Returned == 0,
        "Provider input cannot escape a parameterized query");
    Require((await restoredStore.GetPageAsync(new EventQuery(
        Limit: 50, Channel: "Application"))).Returned == 1,
        "Independent channel filter");
    bool reversedRange = false;
    try
    {
        await restoredStore.GetPageAsync(new EventQuery(
            FromUtc: timestamp.AddMinutes(1), ToUtc: timestamp));
    }
    catch (ArgumentException) { reversedRange = true; }
    Require(reversedRange, "Reversed UTC range rejected");

    Require((await restoredStore.GetByIdAsync(idA)) is null,
        "An ID never inserted into the fixture must not be invented");

    bool invalidCursor = false;
    try { await restoredStore.GetPageAsync(10, "bad-not-base64"); }
    catch (ArgumentException) { invalidCursor = true; }
    Require(invalidCursor, "Invalid cursor rejected");

    bool invalidLimit = false;
    try { await restoredStore.GetRecentAsync(501); }
    catch (ArgumentOutOfRangeException) { invalidLimit = true; }
    Require(invalidLimit, "Excessive limit rejected");

    Console.WriteLine("PASS: durable checkpoint/restart, rollback, durable gaps, cursor pages, auth-independent data filters");
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(temp, true); } catch (IOException) { }
}
