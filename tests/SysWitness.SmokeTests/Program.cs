using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysWitness.Security;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}

if (args is ["--seed-process-fixtures", var databasePath])
{
    await ProcessMonitoringSmoke.SeedAsync(databasePath);
    return;
}

if (args is ["--seed-activity-fixtures", var activityDatabasePath])
{
    await ProcessActivitySmoke.SeedAsync(activityDatabasePath);
    return;
}

if (args is ["--seed-network-fixtures", var networkDatabasePath])
{
    await NetworkMonitoringSmoke.SeedAsync(networkDatabasePath);
    return;
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
    await ProcessMonitoringSmoke.VerifyAsync(Path.Combine(temp, "process.sqlite"));
    await LocalProcessCollectionSmoke.VerifyAsync(Path.Combine(temp, "local-process.sqlite"));
    await NetworkMonitoringSmoke.VerifyAsync(Path.Combine(temp, "network.sqlite"));
    await ProcessActivitySmoke.VerifyAsync(Path.Combine(temp, "activity.sqlite"));
    // Client registry contains SHA-256 hashes only, no raw credentials.
    var clientFile = Path.Combine(temp, "clients.json");
    var eventToken = new string('e', 64);
    var storageToken = new string('s', 64);
    string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    File.WriteAllText(clientFile, JsonSerializer.Serialize(new
    {
        clients = new[]
        {
            new { id = "event-reader", tokenSha256 = Hash(eventToken), scopes = new[] { "events.read" } },
            new { id = "storage-reader", tokenSha256 = Hash(storageToken), scopes = new[] { "storage.read" } }
        }
    }));
    var registry = ReadOnlyClientRegistry.Load(null, clientFile);
    Require(registry.TryAuthenticate(eventToken, out var eventClient) &&
        eventClient!.CanRead(ReadOnlyClientRegistry.EventsRead) &&
        !eventClient.CanRead(ReadOnlyClientRegistry.StorageRead) &&
        !eventClient.CanRead(ReadOnlyClientRegistry.CoverageRead) &&
        !eventClient.CanRead(ReadOnlyClientRegistry.ProcessesRead),
        "Events-only client cannot access other data categories");
    Require(registry.TryAuthenticate(storageToken, out var storageClient) &&
        storageClient!.CanRead(ReadOnlyClientRegistry.StorageRead) &&
        !storageClient.CanRead(ReadOnlyClientRegistry.EventsRead),
        "Storage-only client cannot access event data");
    Require(!registry.TryAuthenticate(new string('z', 64), out _),
        "Unknown token denied");
    var legacyRegistry = ReadOnlyClientRegistry.Load(new string('a', 64), clientFile);
    Require(legacyRegistry.TryAuthenticate(new string('a', 64), out var legacy) &&
        legacy!.CanRead(ReadOnlyClientRegistry.EventsRead) &&
        legacy.CanRead(ReadOnlyClientRegistry.CoverageRead) &&
        legacy.CanRead(ReadOnlyClientRegistry.StorageRead) &&
        !legacy.CanRead(ReadOnlyClientRegistry.ProcessesRead),
        "Legacy token remains read-only with existing data scopes");
    Require(ReadOnlyClientRegistry.RequiredScope("/api/v1/events") ==
        ReadOnlyClientRegistry.EventsRead &&
        ReadOnlyClientRegistry.RequiredScope("/api/v1/storage/status") ==
        ReadOnlyClientRegistry.StorageRead &&
        ReadOnlyClientRegistry.RequiredScope("/api/v1/events/delete") is null,
        "Fail-closed routing never grants unknown paths a read scope");

    var invalidConfig = Path.Combine(temp, "bad-clients.json");
    File.WriteAllText(invalidConfig, """
        {"clients":[{"id":"unsafe","tokenSha256":"not-a-hash","scopes":["events.read"]}]}
        """);
    var invalidRejected = false;
    try { ReadOnlyClientRegistry.Load(null, invalidConfig); }
    catch (InvalidDataException) { invalidRejected = true; }
    Require(invalidRejected, "Malformed registry cannot start API");
    File.WriteAllText(invalidConfig, JsonSerializer.Serialize(new
    {
        clients = new[] { new { id = "unsafe", tokenSha256 = Hash(eventToken),
            scopes = new[] { "events.read", "windows.execute" } } }
    }));
    invalidRejected = false;
    try { ReadOnlyClientRegistry.Load(null, invalidConfig); }
    catch (InvalidDataException) { invalidRejected = true; }
    Require(invalidRejected, "Unknown or control-oriented scopes are rejected");

    File.WriteAllText(invalidConfig, JsonSerializer.Serialize(new
    {
        clients = new[] { new { id = "unsafe", tokenSha256 = Hash(eventToken),
            token = eventToken, scopes = new[] { "events.read" } } }
    }));
    invalidRejected = false;
    try { ReadOnlyClientRegistry.Load(null, invalidConfig); }
    catch (InvalidDataException) { invalidRejected = true; }
    Require(invalidRejected, "Client files reject accidental plaintext token properties");

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

    // Default policy MUST retain existing evidence, even when the records are old.
    Require(store.Policy.RetentionDays is null, "Retention disabled by default");
    Require(await restoredStore.PruneExpiredAsync(timestamp.AddYears(5)) == 0,
        "Default storage policy never deletes user evidence");
    Require((await restoredStore.GetPageAsync(new EventQuery(Limit: 100))).Returned == 14,
        "Default policy preserved all evidence");

    var retentionPath = Path.Combine(temp, "retention.sqlite");
    var retentionStore = new SqliteEventStore(retentionPath,
        new StoragePolicy(16 * 1024 * 1024, RetentionDays: 7));
    await retentionStore.InitializeAsync();

    // Same observation timestamps can span multiple channels: deletion ledger is per channel.
    var oldObserved = DateTimeOffset.Parse("2026-12-01T10:00:00Z");
    var now = DateTimeOffset.Parse("2026-12-20T10:00:00Z");
    var recentObserved = now.AddHours(-1);
    ObservedEvent AgeEvent(int id, string channel, DateTimeOffset observed) =>
        new(EventIdentity.FromXml(channel, "<Event>age-" + id + "</Event>"),
            observed, observed, "windows.event", channel, "test", id, id, null,
            "<Event>age-" + id + "</Event>");
    var oldSystem = AgeEvent(301, "System", oldObserved);
    var oldApplication = AgeEvent(302, "Application", oldObserved.AddDays(1));
    var recentSystem = AgeEvent(303, "System", recentObserved);

    await retentionStore.CommitBatchAsync("System", [oldSystem, recentSystem],
        "<BookmarkList><Bookmark RecordId='303'/></BookmarkList>", 303);
    await retentionStore.CommitBatchAsync("Application", [oldApplication],
        "<BookmarkList><Bookmark RecordId='302'/></BookmarkList>", 302);

    Require(await retentionStore.PruneExpiredAsync(now, 1) == 1,
        "Retention must delete in bounded batches");
    Require(await retentionStore.PruneExpiredAsync(now, 10) == 1,
        "Second bounded retention batch deletes remaining expired source");
    Require(await retentionStore.PruneExpiredAsync(now, 10) == 0,
        "A fresh record must not be deleted");
    Require((await retentionStore.GetByIdAsync(recentSystem.EventId)) is not null,
        "Fresh evidence remains available");
    Require(await retentionStore.GetByIdAsync(oldSystem.EventId) is null &&
        await retentionStore.GetByIdAsync(oldApplication.EventId) is null,
        "Opt-in expired evidence is removed");
    Require((await retentionStore.GetCheckpointAsync("System"))?.RecordId == 303 &&
        (await retentionStore.GetCheckpointAsync("Application"))?.RecordId == 302,
        "Retention MUST NOT mutate native collector bookmarks");
    var audit = await retentionStore.GetRetentionHistoryAsync();
    Require(audit.Count == 2 && audit.Sum(x => x.EventCount) == 2 &&
        audit.Select(x => x.Channel).ToHashSet().SetEquals(["System", "Application"]),
        "Per-channel retention deletion summaries are durable");
    var retentionStatus = await retentionStore.GetStorageStatusAsync();
    Require(retentionStatus.StoredEventCount == 1 &&
        retentionStatus.RecordedRetentionDeletedEvents == 2 &&
        retentionStatus.RetentionDays == 7,
        "Read-only storage status reports retained and deleted counts");

    var reopenedRetentionStore = new SqliteEventStore(retentionPath,
        new StoragePolicy(16 * 1024 * 1024, 7));
    await reopenedRetentionStore.InitializeAsync();
    Require((await reopenedRetentionStore.GetRetentionHistoryAsync()).Sum(x => x.EventCount) == 2,
        "Retention deletion history persists across a new storage instance");
    Require((await reopenedRetentionStore.GetCheckpointAsync("System"))?.RecordId == 303,
        "Retention changes do not invalidate restart checkpoints");

    var pressurePath = Path.Combine(temp, "pressure.sqlite");
    var pressureStore = new SqliteEventStore(pressurePath, new StoragePolicy(1024 * 1024, null));
    await pressureStore.InitializeAsync();
    var large = AgeEvent(400, "System", now) with { RawXml = new string('x', 2_000_000) };
    bool paused = false;
    try
    {
        await pressureStore.CommitBatchAsync("System", [large],
            "<BookmarkList><Bookmark RecordId='400'/></BookmarkList>", 400);
    }
    catch (StoragePressureException) { paused = true; }
    Require(paused, "Oversized event batch must be stopped rather than silently dropped");
    Require(await pressureStore.GetCheckpointAsync("System") is null &&
        await pressureStore.GetByIdAsync(large.EventId) is null,
        "Soft budget rejection must leave both checkpoint and evidence unchanged");

    // Simulate low free bytes deterministically without filling or modifying a real drive.
    var guardPath = Path.Combine(temp, "disk-reserve.sqlite");
    var guardPolicy = new StoragePolicy(16L * 1024 * 1024, null, MinFreeDiskBytes: 1024 * 1024);
    var diskGuarded = new SqliteEventStore(guardPath, guardPolicy,
        new StubFreeSpaceProbe(available: 1024 * 1024));
    await diskGuarded.InitializeAsync();
    var guardedEvent = AgeEvent(401, "System", now);
    var reserveStopped = false;
    try
    {
        await diskGuarded.CommitBatchAsync("System", [guardedEvent], "<bookmark>401</bookmark>", 401);
    }
    catch (StorageDiskSpaceException ex) when (ex.Reason == "disk_free_reserve_reached")
    {
        reserveStopped = true;
    }
    Require(reserveStopped, "An insufficient free-space reserve must block event writes");
    Require(await diskGuarded.GetCheckpointAsync("System") is null &&
        await diskGuarded.GetByIdAsync(guardedEvent.EventId) is null,
        "Disk guard failure cannot write evidence or advance bookmark");
    var lowDiskStatus = await diskGuarded.GetStorageStatusAsync();
    Require(lowDiskStatus.DiskReserveReached && !lowDiskStatus.CanAcceptNewEvidence &&
        lowDiskStatus.IngestPauseReason == "disk_free_reserve_reached" &&
        lowDiskStatus.AvailableFreeDiskBytes == 1024 * 1024,
        "Read-only storage status must report volume pressure accurately");

    var unknownDisk = new SqliteEventStore(Path.Combine(temp, "unknown-disk.sqlite"),
        guardPolicy, new StubFreeSpaceProbe(available: null));
    await unknownDisk.InitializeAsync();
    var unknownStopped = false;
    try
    {
        await unknownDisk.InsertAsync([guardedEvent]);
    }
    catch (StorageDiskSpaceException ex) when (ex.Reason == "disk_free_space_unknown")
    {
        unknownStopped = true;
    }
    Require(unknownStopped && (await unknownDisk.GetStorageStatusAsync()).IngestPauseReason ==
        "disk_free_space_unknown", "Unknown volume free space fails closed");

    var ampleDisk = new SqliteEventStore(Path.Combine(temp, "ample-disk.sqlite"),
        guardPolicy, new StubFreeSpaceProbe(available: 8 * 1024 * 1024));
    await ampleDisk.InitializeAsync();
    Require(await ampleDisk.CommitBatchAsync("System", [guardedEvent],
        "<bookmark>401</bookmark>", 401) == 1,
        "Adequate free-space reserve allows ingestion");
    Require((await ampleDisk.GetStorageStatusAsync()).CanAcceptNewEvidence,
        "Disk guard reports ready when volume has adequate available space");

    // Invalid local settings must fail closed; never silently accept a destructive policy.
    var previousDays = Environment.GetEnvironmentVariable("SYSWITNESS_RETENTION_DAYS");
    var previousMinFree = Environment.GetEnvironmentVariable("SYSWITNESS_MIN_FREE_DISK_MB");
    try
    {
        Environment.SetEnvironmentVariable("SYSWITNESS_RETENTION_DAYS", "-1");
        var rejectedRetention = false;
        try { StoragePolicy.FromEnvironment(); }
        catch (ArgumentException) { rejectedRetention = true; }
        Require(rejectedRetention, "Invalid local retention setting rejected");
        Environment.SetEnvironmentVariable("SYSWITNESS_RETENTION_DAYS", null);
        Environment.SetEnvironmentVariable("SYSWITNESS_MIN_FREE_DISK_MB", "-1");
        var rejectedReserve = false;
        try { StoragePolicy.FromEnvironment(); }
        catch (ArgumentException) { rejectedReserve = true; }
        Require(rejectedReserve, "Invalid local minimum free disk setting rejected");
    }
    finally
    {
        Environment.SetEnvironmentVariable("SYSWITNESS_RETENTION_DAYS", previousDays);
        Environment.SetEnvironmentVariable("SYSWITNESS_MIN_FREE_DISK_MB", previousMinFree);
    }

    Console.WriteLine("PASS: durable bookmarks, independent sources, cursor search, " +
        "opt-in retention, disk-free guard, process and network projections, host-scoped activity and scoped read-only client keys");
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(temp, true); } catch (IOException) { }
}
