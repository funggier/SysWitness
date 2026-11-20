using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SysWitness.Contracts;

namespace SysWitness.Storage;

/// <summary>
/// Local-only disk safety and opt-in retention. Maintenance never changes native source bookmarks.
/// Physical SQLite file sizes are advisory; this is a soft stop, not a hard filesystem quota.
/// </summary>
public sealed partial class SqliteEventStore
{
    public StoragePolicy Policy => _policy;

    private (long Database, long Wal, long Shm) PhysicalBytes() =>
        (FileBytes(_databasePath), FileBytes(_databasePath + "-wal"), FileBytes(_databasePath + "-shm"));

    private static long FileBytes(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// Conservative pre-ingest check. Held under _writeLock before every event batch.
    /// Does not advance native bookmarks when it cannot accept a batch.
    /// SQLite indexes, journal and filesystem overhead are approximate; exceeding the
    /// configured limit by a small amount remains possible.
    /// </summary>
    private void EnsureWithinSoftBudget(IReadOnlyCollection<ObservedEvent> events)
    {
        if (events.Count == 0) return;

        var size = PhysicalBytes();
        var physical = checked(size.Database + size.Wal + size.Shm);
        long estimatedGrowth = 0;
        foreach (var item in events)
        {
            // Reserve roughly 2x serialized raw event size for table/index and WAL overhead.
            estimatedGrowth = checked(estimatedGrowth + 1024L +
                2L * Encoding.UTF8.GetByteCount(item.RawXml ?? ""));
        }
        if (physical + estimatedGrowth > _policy.SoftLimitBytes)
            throw new StoragePressureException();

        // A soft database-size budget does NOT protect a shared volume.
        // Refuse an event batch if available free bytes cannot be verified or
        // cannot cover both configured reserve and estimated write growth.
        var available = _spaceProbe.GetAvailableFreeBytes(_databasePath);
        if (available is null)
            throw new StorageDiskSpaceException("disk_free_space_unknown");
        if (available.Value < _policy.MinFreeDiskBytes + estimatedGrowth)
            throw new StorageDiskSpaceException("disk_free_reserve_reached");
    }

    public async Task<StorageStatus> GetStorageStatusAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM events),
                   (SELECT COALESCE(SUM(event_count), 0) FROM retention_deletions);
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var stored = reader.GetInt64(0);
        var deleted = reader.GetInt64(1);
        var sizes = PhysicalBytes();
        var physical = checked(sizes.Database + sizes.Wal + sizes.Shm);
        var freeBytes = _spaceProbe.GetAvailableFreeBytes(_databasePath);
        var reserveReached = freeBytes is not null && freeBytes <= _policy.MinFreeDiskBytes;
        var sizeReached = physical >= _policy.SoftLimitBytes;
        var pauseReason = sizeReached ? "storage_soft_limit_reached" :
            freeBytes is null ? "disk_free_space_unknown" :
            reserveReached ? "disk_free_reserve_reached" : null;
        return new StorageStatus(sizes.Database, sizes.Wal, sizes.Shm,
            physical, _policy.SoftLimitBytes, sizeReached,
            _policy.RetentionDays, stored, deleted,
            "Physical disk bytes and disk availability are snapshots; neither is a hard quota. " +
            "New batches can be paused before reaching the visible thresholds.",
            _policy.MinFreeDiskBytes, freeBytes, reserveReached,
            pauseReason is null, pauseReason);
    }

    /// <summary>
    /// Deletes ONLY this application's local event copies older than an explicit opt-in
    /// observation-age policy. Writes a durable per-channel deletion summary in the SAME
    /// transaction. Never alters Windows Event Logs or collection checkpoints.
    /// </summary>
    public async Task<int> PruneExpiredAsync(
        DateTimeOffset nowUtc, int maxEvents = 500, CancellationToken ct = default)
    {
        if (_policy.RetentionDays is not int retentionDays) return 0;
        if (maxEvents is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));

        var cutoff = nowUtc.ToUniversalTime().AddDays(-retentionDays).ToString("O");
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            var expired = new List<(string Id, string Channel, string Observed)>();
            await using (var select = db.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = """
                    SELECT event_id,channel,observed_at_utc
                    FROM events
                    WHERE observed_at_utc < $cutoff
                    ORDER BY observed_at_utc ASC, event_id ASC
                    LIMIT $limit;
                    """;
                select.Parameters.AddWithValue("$cutoff", cutoff);
                select.Parameters.AddWithValue("$limit", maxEvents);
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    expired.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            if (expired.Count == 0) return 0;

            var deletedAt = nowUtc.ToUniversalTime().ToString("O");
            foreach (var group in expired.GroupBy(x => x.Channel))
            {
                await using var record = db.CreateCommand();
                record.Transaction = tx;
                record.CommandText = """
                    INSERT INTO retention_deletions
                    (deleted_at_utc,channel,event_count,oldest_observed_at_utc,
                     newest_observed_at_utc,cutoff_utc)
                    VALUES ($deleted,$channel,$count,$oldest,$newest,$cutoff);
                    """;
                record.Parameters.AddWithValue("$deleted", deletedAt);
                record.Parameters.AddWithValue("$channel", group.Key);
                record.Parameters.AddWithValue("$count", group.LongCount());
                record.Parameters.AddWithValue("$oldest", group.Min(x => x.Observed));
                record.Parameters.AddWithValue("$newest", group.Max(x => x.Observed));
                record.Parameters.AddWithValue("$cutoff", cutoff);
                await record.ExecuteNonQueryAsync(ct);
            }

            await using (var delete = db.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM events WHERE event_id=$id;";
                var id = delete.Parameters.Add("$id", SqliteType.Text);
                foreach (var entry in expired)
                {
                    id.Value = entry.Id;
                    if (await delete.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException("Retention deletion count mismatch.");
                }
            }

            await tx.CommitAsync(ct);
            return expired.Count;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<RetentionDeletion>> GetRetentionHistoryAsync(
        int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT deletion_id,deleted_at_utc,channel,event_count,
                   oldest_observed_at_utc,newest_observed_at_utc,cutoff_utc
            FROM retention_deletions
            ORDER BY deletion_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<RetentionDeletion>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            static DateTimeOffset Parse(string value) =>
                DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            results.Add(new RetentionDeletion(reader.GetInt64(0),
                Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetInt64(3), Parse(reader.GetString(4)),
                Parse(reader.GetString(5)), Parse(reader.GetString(6))));
        }
        return results;
    }
}
