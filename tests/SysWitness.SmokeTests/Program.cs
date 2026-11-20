using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

var idA = EventIdentity.FromXml("System", "<Event><Data>test</Data></Event>");
var idB = EventIdentity.FromXml("System", "<Event><Data>test</Data></Event>");
var idC = EventIdentity.FromXml("System", "<Event><Data>different</Data></Event>");

Require(idA == idB, "Stable ID must be deterministic");
Require(idA != idC, "Different evidence needs different ID");

var path = Path.Combine(Path.GetTempPath(), "SysWitness-Smoke-" + Guid.NewGuid().ToString("N") + ".sqlite");
try
{
    var store = new SqliteEventStore(path);
    await store.InitializeAsync();
    var eventData = new ObservedEvent(idA, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "windows.event", "System", "test-provider", 1, 100, null, "<Event><Data>test</Data></Event>");
    Require(await store.InsertAsync(new[] { eventData }) == 1, "First insert");
    Require(await store.InsertAsync(new[] { eventData }) == 0, "Duplicate must not overwrite evidence");
    var page = await store.GetRecentAsync(10);
    Require(page.Returned == 1 && page.Items[0].EventId == idA, "Query returns stored event");
    Require((await store.GetByIdAsync(idA))?.Provider == "test-provider", "Single event lookup");
    Require(await store.GetByIdAsync("invalid") is null, "Reject invalid identifier");
    var rejected = false;
    try { await store.GetRecentAsync(1000); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Require(rejected, "Bounded queries must reject excessive limits");
    Console.WriteLine("PASS: event identity, SQLite persistence, deduplication, read-only queries, bounds");
}
finally
{
    SqliteConnectionPoolsClear();
    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        try { File.Delete(path + suffix); } catch (IOException) { /* Test file may still be locked */ }
    }
}

void SqliteConnectionPoolsClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}
