using System.Globalization;
using Microsoft.Data.Sqlite;
using SysWitness.Contracts;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    private static async Task<(IReadOnlyList<T> Items, string? NextCursor)> ReadEvidencePageAsync<T>(
        SqliteCommand cmd, EventQuery query, string source, Func<ObservedEvent, T> project, CancellationToken ct)
    {
        cmd.CommandText = $"""
            SELECT event_id,occurred_at_utc,observed_at_utc,category,channel,provider,
                   source_event_id,source_record_id,computer,raw_xml
            FROM events
            WHERE {source}
              AND ($channel IS NULL OR channel=$channel)
              AND ($provider IS NULL OR provider=$provider)
              AND ($source_event_id IS NULL OR source_event_id=$source_event_id)
              AND ($from_utc IS NULL OR occurred_at_utc >= $from_utc)
              AND ($to_utc IS NULL OR occurred_at_utc < $to_utc)
              AND ($time IS NULL OR occurred_at_utc < $time OR
                  (occurred_at_utc=$time AND event_id < $id))
            ORDER BY occurred_at_utc DESC, event_id DESC
            LIMIT $limit;
            """;
        BindEventFilters(cmd, query, DecodeCursor(query.Cursor));
        var items = new List<T>();
        ObservedEvent? last = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (items.Count == query.Limit)
                return (items, EncodeCursor(last!.OccurredAtUtc, last.EventId));

            last = new ObservedEvent(reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9));
            items.Add(project(last));
        }

        return (items, null);
    }

    private static void BindSysmonProcessGuid(SqliteConnection db, SqliteCommand cmd, Guid processGuid)
    {
        // GUID selection is backed by the durable derived identity index.
        cmd.Parameters.AddWithValue("$process_guid", processGuid.ToString("D"));
    }
}
