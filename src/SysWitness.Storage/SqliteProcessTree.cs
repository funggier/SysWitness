using System.Globalization;
using Microsoft.Data.Sqlite;
using SysWitness.Contracts;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    public async Task<ProcessTree> GetProcessTreeAsync(Guid processGuid, string computer,
        int maxDepth = 3, int nodeLimit = 100, CancellationToken ct = default)
    {
        var tree = new ProcessTreeBuilder(processGuid, computer, maxDepth, nodeLimit);
        await using var db = await OpenAsync(ct);
        db.CreateFunction("syswitness_process_guid",
            (string xml) => WindowsProcessEventDecoder.ReadSysmonProcessGuid(xml)?.ToString("D"),
            isDeterministic: true);
        db.CreateFunction("syswitness_parent_process_guid",
            (string xml) => WindowsProcessEventDecoder.ReadSysmonParentProcessGuid(xml)?.ToString("D"),
            isDeterministic: true);

        // All expansion queries share one SQLite read snapshot, including concurrent retention.
        await using var tx = db.BeginTransaction(deferred: true);
        while (tree.GetFrontier() is { Count: > 0 } frontier)
        {
            var remaining = ProcessTreeBuilder.SourceEventLimit - tree.EvidenceIds.Count;
            await using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            string Bind(string name, object value)
            {
                cmd.Parameters.AddWithValue(name, value);
                return name;
            }
            var guids = string.Join(",", frontier.Select((guid, i) => Bind("$g" + i, guid.ToString("D"))));
            var exclusions = tree.EvidenceIds.Count == 0 ? "" :
                "AND event_id NOT IN (" + string.Join(",", tree.EvidenceIds.Select(
                    (id, i) => Bind("$seen" + i, id))) + ")";
            cmd.CommandText = $"""
                SELECT event_id,occurred_at_utc,observed_at_utc,category,channel,provider,
                       source_event_id,source_record_id,computer,raw_xml
                FROM events
                WHERE channel=$sysmon AND provider=$provider AND source_event_id=1 AND computer=$computer
                  AND (syswitness_process_guid(raw_xml) IN ({guids})
                       OR syswitness_parent_process_guid(raw_xml) IN ({guids}))
                  {exclusions}
                ORDER BY occurred_at_utc DESC,event_id DESC LIMIT $limit;
                """;
            Bind("$sysmon", WindowsProcessEventDecoder.SysmonChannel);
            Bind("$provider", WindowsProcessEventDecoder.SysmonProvider);
            Bind("$computer", computer);
            Bind("$limit", remaining + 1);
            var creations = new List<ProcessEvent>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (creations.Count == remaining)
                    {
                        foreach (var creation in creations) tree.Add(creation);
                        return tree.Snapshot(evidenceLimitReached: true);
                    }
                    var evidence = new ObservedEvent(reader.GetString(0),
                        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.GetString(8), reader.GetString(9));
                    creations.Add(WindowsProcessEventDecoder.Decode(evidence)!);
                }
            }
            foreach (var creation in creations) tree.Add(creation);
            tree.MarkExpanded(frontier);
        }
        return tree.Snapshot(evidenceLimitReached: false);
    }
}
