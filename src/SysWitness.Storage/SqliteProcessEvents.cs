using System.Globalization;
using SysWitness.Contracts;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    public async Task<ProcessEventPage> GetProcessEventsAsync(
        EventQuery query, Guid? processGuid = null, CancellationToken ct = default)
    {
        ValidateEventQuery(query);
        if (query.Category is not null || processGuid == Guid.Empty)
            throw new ArgumentException("Invalid process event filter.");

        var position = DecodeCursor(query.Cursor);
        await using var db = await OpenAsync(ct);
        // Register on this connection only for timelines. A GUID in a command line or
        // ParentProcessGuid does not identify the process whose lifecycle is recorded.
        if (processGuid is not null)
            db.CreateFunction("syswitness_process_guid",
                (string xml) => WindowsProcessEventDecoder.ReadSysmonProcessGuid(xml)?.ToString("D"),
                isDeterministic: true);

        await using var cmd = db.CreateCommand();
        var source = processGuid is null
            ? """
                ((channel=$sysmon AND provider=$sysmon_provider AND source_event_id IN (1,5))
                  OR (channel=$security AND provider=$security_provider AND source_event_id IN (4688,4689))
                  OR (channel=$snapshot AND provider=$snapshot_provider AND source_event_id IN (1,2))
                  OR (channel=$wmi AND provider=$wmi_provider AND source_event_id IN (1,2)))
                """
            : """
                (channel=$sysmon AND provider=$sysmon_provider AND source_event_id IN (1,5)
                  AND syswitness_process_guid(raw_xml)=$process_guid)
                """;
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
        cmd.Parameters.AddWithValue("$sysmon", WindowsProcessEventDecoder.SysmonChannel);
        cmd.Parameters.AddWithValue("$sysmon_provider", WindowsProcessEventDecoder.SysmonProvider);
        if (processGuid is Guid guid)
            cmd.Parameters.AddWithValue("$process_guid", guid.ToString("D"));
        else
        {
            cmd.Parameters.AddWithValue("$security", WindowsProcessEventDecoder.SecurityChannel);
            cmd.Parameters.AddWithValue("$security_provider", WindowsProcessEventDecoder.SecurityProvider);
            cmd.Parameters.AddWithValue("$snapshot", LocalProcessEvidence.SnapshotChannel);
            cmd.Parameters.AddWithValue("$snapshot_provider", LocalProcessEvidence.SnapshotProvider);
            cmd.Parameters.AddWithValue("$wmi", LocalProcessEvidence.WmiChannel);
            cmd.Parameters.AddWithValue("$wmi_provider", LocalProcessEvidence.WmiProvider);
        }
        BindEventFilters(cmd, query, position);

        var items = new List<ProcessEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var evidence = new ObservedEvent(reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9));
            // The SQL source predicate selects exactly the records supported by the decoder.
            items.Add((WindowsProcessEventDecoder.Decode(evidence) ?? LocalProcessEvidence.Decode(evidence))!);
        }

        var hasNext = items.Count > query.Limit;
        if (hasNext)
            items.RemoveAt(items.Count - 1);
        var next = hasNext ? EncodeCursor(items[^1].OccurredAtUtc, items[^1].EventId) : null;
        return new ProcessEventPage(items, query.Limit, items.Count, next);
    }
}
