using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SysWitness.Contracts;

namespace SysWitness.Storage;

/// <summary>
/// Persistent source evidence, durable native bookmarks and bounded read queries.
/// No API caller can supply SQL; inputs only bind to predetermined parameters.
/// </summary>
public sealed partial class SqliteEventStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _databasePath;
    private readonly StoragePolicy _policy;

    public SqliteEventStore(string databasePath, StoragePolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        _databasePath = fullPath;
        _policy = policy ?? StoragePolicy.Default;
        if (_policy.SoftLimitBytes < 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(policy), "Soft limit must be at least 1 MiB.");
        if (_policy.RetentionDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(policy), "Retention days must be 1..3650.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events (
                event_id TEXT PRIMARY KEY NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                category TEXT NOT NULL,
                channel TEXT NOT NULL,
                provider TEXT NOT NULL,
                source_event_id INTEGER NOT NULL,
                source_record_id INTEGER NULL,
                computer TEXT NULL,
                raw_xml TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_occurred
                ON events(occurred_at_utc DESC, event_id DESC);
            CREATE INDEX IF NOT EXISTS ix_events_category
                ON events(category, occurred_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_events_channel
                ON events(channel, occurred_at_utc DESC, event_id DESC);
            CREATE INDEX IF NOT EXISTS ix_events_provider
                ON events(provider, occurred_at_utc DESC, event_id DESC);
            CREATE INDEX IF NOT EXISTS ix_events_source_id
                ON events(source_event_id, occurred_at_utc DESC, event_id DESC);

            CREATE TABLE IF NOT EXISTS collection_checkpoints (
                channel TEXT PRIMARY KEY NOT NULL,
                bookmark_xml TEXT NOT NULL,
                record_id INTEGER NULL,
                committed_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS collection_gaps (
                gap_id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel TEXT NOT NULL,
                detected_at_utc TEXT NOT NULL,
                reason TEXT NOT NULL,
                previous_record_id INTEGER NULL,
                available_first_record_id INTEGER NULL,
                available_last_record_id INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_observed
                ON events(observed_at_utc ASC, event_id ASC);
            CREATE TABLE IF NOT EXISTS retention_deletions (
                deletion_id INTEGER PRIMARY KEY AUTOINCREMENT,
                deleted_at_utc TEXT NOT NULL,
                channel TEXT NOT NULL,
                event_count INTEGER NOT NULL,
                oldest_observed_at_utc TEXT NOT NULL,
                newest_observed_at_utc TEXT NOT NULL,
                cutoff_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CollectorCheckpoint?> GetCheckpointAsync(string channel, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT bookmark_xml, record_id FROM collection_checkpoints
            WHERE channel=$channel;
            """;
        command.Parameters.AddWithValue("$channel", channel);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new CollectorCheckpoint(channel, reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    /// <summary>
    /// Atomic: raw records and the bookmark that follows the last stored record.
    /// If a write fails, neither the records nor checkpoint advance.
    /// </summary>
    public async Task<int> CommitBatchAsync(
        string channel,
        IEnumerable<ObservedEvent> events,
        string? bookmarkXml,
        long? lastRecordId,
        string? gapReason = null,
        long? previousRecordId = null,
        EventLogWindow? availableWindow = null,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var materialized = events.ToList();
            EnsureWithinSoftBudget(materialized);
            await using var db = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            var count = 0;
            var last = (ObservedEvent?)null;

            foreach (var item in materialized)
            {
                if (item.Channel != channel)
                    throw new ArgumentException("A collection batch cannot mix source channels.");
                last = item;
                await using var insert = db.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT OR IGNORE INTO events
                    (event_id,occurred_at_utc,observed_at_utc,category,channel,provider,
                     source_event_id,source_record_id,computer,raw_xml)
                    VALUES ($id,$occurred,$observed,$category,$channel,$provider,
                            $sourceid,$recordid,$computer,$raw);
                    """;
                insert.Parameters.AddWithValue("$id", item.EventId);
                insert.Parameters.AddWithValue("$occurred", item.OccurredAtUtc.ToUniversalTime().ToString("O"));
                insert.Parameters.AddWithValue("$observed", item.ObservedAtUtc.ToUniversalTime().ToString("O"));
                insert.Parameters.AddWithValue("$category", item.Category);
                insert.Parameters.AddWithValue("$channel", item.Channel);
                insert.Parameters.AddWithValue("$provider", item.Provider);
                insert.Parameters.AddWithValue("$sourceid", item.SourceEventId);
                insert.Parameters.AddWithValue("$recordid", (object?)item.SourceRecordId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$computer", (object?)item.Computer ?? DBNull.Value);
                insert.Parameters.AddWithValue("$raw", item.RawXml);
                count += await insert.ExecuteNonQueryAsync(ct);
            }

            if (gapReason is not null)
            {
                await using var gap = db.CreateCommand();
                gap.Transaction = tx;
                gap.CommandText = """
                    INSERT INTO collection_gaps
                    (channel,detected_at_utc,reason,previous_record_id,
                     available_first_record_id,available_last_record_id)
                    VALUES ($channel,$time,$reason,$previous,$first,$last);
                    """;
                gap.Parameters.AddWithValue("$channel", channel);
                gap.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
                gap.Parameters.AddWithValue("$reason", gapReason);
                gap.Parameters.AddWithValue("$previous", (object?)previousRecordId ?? DBNull.Value);
                gap.Parameters.AddWithValue("$first", (object?)availableWindow?.OldestRecordId ?? DBNull.Value);
                gap.Parameters.AddWithValue("$last", (object?)availableWindow?.NewestRecordId ?? DBNull.Value);
                await gap.ExecuteNonQueryAsync(ct);
            }

            if (last is not null)
            {
                if (string.IsNullOrWhiteSpace(bookmarkXml))
                    throw new InvalidOperationException("Cannot advance without a native bookmark.");
                if (last.SourceRecordId != lastRecordId)
                    throw new InvalidOperationException("Bookmark must match final committed record.");
                await using var checkpoint = db.CreateCommand();
                checkpoint.Transaction = tx;
                checkpoint.CommandText = """
                    INSERT INTO collection_checkpoints
                    (channel,bookmark_xml,record_id,committed_at_utc)
                    VALUES ($channel,$bookmark,$record,$time)
                    ON CONFLICT(channel) DO UPDATE SET
                        bookmark_xml=excluded.bookmark_xml,
                        record_id=excluded.record_id,
                        committed_at_utc=excluded.committed_at_utc;
                    """;
                checkpoint.Parameters.AddWithValue("$channel", channel);
                checkpoint.Parameters.AddWithValue("$bookmark", bookmarkXml);
                checkpoint.Parameters.AddWithValue("$record", (object?)lastRecordId ?? DBNull.Value);
                checkpoint.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
                await checkpoint.ExecuteNonQueryAsync(ct);
            }
            else if (gapReason is not null)
            {
                // Empty channel after a clear: do not keep an invalid old bookmark.
                await using var drop = db.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = "DELETE FROM collection_checkpoints WHERE channel=$channel";
                drop.Parameters.AddWithValue("$channel", channel);
                await drop.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Compatibility wrapper for simple ingestion/fixtures: does not advance a collector checkpoint.
    public async Task<int> InsertAsync(IEnumerable<ObservedEvent> events, CancellationToken ct = default)
    {
        var materialized = events.ToList();
        if (materialized.Count == 0) return 0;
        await _writeLock.WaitAsync(ct);
        try
        {
            EnsureWithinSoftBudget(materialized);
            await using var db = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            var inserted = 0;
            foreach (var e in materialized)
            {
                await using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO events
                    (event_id,occurred_at_utc,observed_at_utc,category,channel,provider,
                     source_event_id,source_record_id,computer,raw_xml)
                    VALUES ($id,$occurred,$observed,$category,$channel,$provider,
                            $sourceid,$recordid,$computer,$raw);
                    """;
                cmd.Parameters.AddWithValue("$id", e.EventId);
                cmd.Parameters.AddWithValue("$occurred", e.OccurredAtUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$observed", e.ObservedAtUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$category", e.Category);
                cmd.Parameters.AddWithValue("$channel", e.Channel);
                cmd.Parameters.AddWithValue("$provider", e.Provider);
                cmd.Parameters.AddWithValue("$sourceid", e.SourceEventId);
                cmd.Parameters.AddWithValue("$recordid", (object?)e.SourceRecordId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$computer", (object?)e.Computer ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$raw", e.RawXml);
                inserted += await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return inserted;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<long> CountGapsAsync(string channel, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM collection_gaps WHERE channel=$channel";
        cmd.Parameters.AddWithValue("$channel", channel);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<IReadOnlyList<CollectionGap>> GetGapsAsync(
        string channel, int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT gap_id,detected_at_utc,reason,previous_record_id,
                   available_first_record_id,available_last_record_id
            FROM collection_gaps WHERE channel=$channel
            ORDER BY gap_id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<CollectionGap>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new CollectionGap(reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        return list;
    }

    public Task<EventPage> GetRecentAsync(int limit, CancellationToken ct = default)
        => GetPageAsync(limit, null, null, ct);

    public Task<EventPage> GetPageAsync(
        int limit, string? cursor = null, string? channel = null, CancellationToken ct = default)
        => GetPageAsync(new EventQuery(limit, cursor, channel), ct);

    public async Task<EventPage> GetPageAsync(EventQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query.Limit));
        if (query.Channel is not null && (query.Channel.Length is < 1 or > 64 ||
            !query.Channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid channel name.", nameof(query.Channel));
        if (query.Provider is not null && (query.Provider.Length is < 1 or > 256 ||
            query.Provider.Any(char.IsControl)))
            throw new ArgumentException("Invalid provider.", nameof(query.Provider));
        if (query.Category is not null && (query.Category.Length is < 1 or > 128 ||
            query.Category.Any(char.IsControl)))
            throw new ArgumentException("Invalid category.", nameof(query.Category));
        if (query.SourceEventId is < 0 or > 65535)
            throw new ArgumentException("Invalid source event ID.", nameof(query.SourceEventId));
        if (query.FromUtc is not null && query.ToUtc is not null &&
            query.FromUtc >= query.ToUtc)
            throw new ArgumentException("fromUtc must precede toUtc.");

        var position = DecodeCursor(query.Cursor);
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT event_id,occurred_at_utc,category,channel,provider,
                   source_event_id,source_record_id
            FROM events
            WHERE ($channel IS NULL OR channel=$channel)
              AND ($category IS NULL OR category=$category)
              AND ($provider IS NULL OR provider=$provider)
              AND ($source_event_id IS NULL OR source_event_id=$source_event_id)
              AND ($from_utc IS NULL OR occurred_at_utc >= $from_utc)
              AND ($to_utc IS NULL OR occurred_at_utc < $to_utc)
              AND ($time IS NULL OR occurred_at_utc < $time OR
                  (occurred_at_utc=$time AND event_id < $id))
            ORDER BY occurred_at_utc DESC, event_id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$channel", (object?)query.Channel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$category", (object?)query.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", (object?)query.Provider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source_event_id", (object?)query.SourceEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from_utc", (object?)query.FromUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to_utc", (object?)query.ToUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$time", (object?)position?.Time ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", (object?)position?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", query.Limit + 1);
        var list = new List<EventSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new EventSummary(reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6)));

        var hasNext = list.Count > query.Limit;
        if (hasNext) list.RemoveAt(list.Count - 1);
        var next = hasNext ? EncodeCursor(list[^1]) : null;
        return new EventPage(list, query.Limit, list.Count, next);
    }

    public async Task<EventSummary?> GetByIdAsync(string eventId, CancellationToken ct = default)
    {
        if (!IsValidEventId(eventId)) return null;
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT event_id,occurred_at_utc,category,channel,provider,
                   source_event_id,source_record_id
            FROM events WHERE event_id=$id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", eventId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new EventSummary(reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connectionString);
        try { await db.OpenAsync(ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }

    private static bool IsValidEventId(string? id)
        => id is { Length: 64 } && id.All(Uri.IsHexDigit);

    private static string EncodeCursor(EventSummary summary) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            summary.OccurredAtUtc.ToUniversalTime().ToString("O") + "|" + summary.EventId));

    private static (string Time, string Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        if (cursor.Length > 256) throw new ArgumentException("Invalid cursor.");
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException) { throw new ArgumentException("Invalid cursor."); }
        var parts = decoded.Split('|');
        if (parts.Length != 2 || !IsValidEventId(parts[1]) ||
            !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date))
            throw new ArgumentException("Invalid cursor.");
        return (date.ToUniversalTime().ToString("O"), parts[1]);
    }
}

public sealed record CollectorCheckpoint(string Channel, string BookmarkXml, long? RecordId);

public sealed record CollectionGap(
    long GapId,
    DateTimeOffset DetectedAtUtc,
    string Reason,
    long? PreviousRecordId,
    long? AvailableFirstRecordId,
    long? AvailableLastRecordId);
