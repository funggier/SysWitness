using Microsoft.Data.Sqlite;
using SysWitness.Contracts;

namespace SysWitness.Storage;

/// <summary>SQLite persistence for source evidence. No SQL query strings are accepted from clients.</summary>
public sealed class SqliteEventStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteEventStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
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
            CREATE INDEX IF NOT EXISTS ix_events_occurred ON events(occurred_at_utc DESC, event_id DESC);
            CREATE INDEX IF NOT EXISTS ix_events_category ON events(category, occurred_at_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> InsertAsync(IEnumerable<ObservedEvent> events, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var inserted = 0;
            foreach (var item in events)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO events
                    (event_id, occurred_at_utc, observed_at_utc, category, channel,
                     provider, source_event_id, source_record_id, computer, raw_xml)
                    VALUES ($id, $occurred, $observed, $category, $channel,
                            $provider, $source_id, $record_id, $computer, $xml);
                    """;
                command.Parameters.AddWithValue("$id", item.EventId);
                command.Parameters.AddWithValue("$occurred", item.OccurredAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$observed", item.ObservedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue("$channel", item.Channel);
                command.Parameters.AddWithValue("$provider", item.Provider);
                command.Parameters.AddWithValue("$source_id", item.SourceEventId);
                command.Parameters.AddWithValue("$record_id", (object?)item.SourceRecordId ?? DBNull.Value);
                command.Parameters.AddWithValue("$computer", (object?)item.Computer ?? DBNull.Value);
                command.Parameters.AddWithValue("$xml", item.RawXml);
                inserted += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<EventPage> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, occurred_at_utc, category, channel, provider,
                   source_event_id, source_record_id
            FROM events ORDER BY occurred_at_utc DESC, event_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<EventSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new EventSummary(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }
        return new EventPage(items, limit, items.Count);
    }

    public async Task<EventSummary?> GetByIdAsync(string eventId, CancellationToken cancellationToken = default)
    {
        if (eventId.Length != 64 || !eventId.All(Uri.IsHexDigit))
            return null;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, occurred_at_utc, category, channel, provider,
                   source_event_id, source_record_id
            FROM events WHERE event_id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new EventSummary(reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }
}
