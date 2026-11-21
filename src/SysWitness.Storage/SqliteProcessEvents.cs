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

        await using var db = await OpenAsync(ct);

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
        cmd.Parameters.AddWithValue("$sysmon", WindowsProcessEventDecoder.SysmonChannel);
        cmd.Parameters.AddWithValue("$sysmon_provider", WindowsProcessEventDecoder.SysmonProvider);
        if (processGuid is Guid guid)
            BindSysmonProcessGuid(db, cmd, guid);
        else
        {
            cmd.Parameters.AddWithValue("$security", WindowsProcessEventDecoder.SecurityChannel);
            cmd.Parameters.AddWithValue("$security_provider", WindowsProcessEventDecoder.SecurityProvider);
            cmd.Parameters.AddWithValue("$snapshot", LocalProcessEvidence.SnapshotChannel);
            cmd.Parameters.AddWithValue("$snapshot_provider", LocalProcessEvidence.SnapshotProvider);
            cmd.Parameters.AddWithValue("$wmi", LocalProcessEvidence.WmiChannel);
            cmd.Parameters.AddWithValue("$wmi_provider", LocalProcessEvidence.WmiProvider);
        }
        var (items, next) = await ReadEvidencePageAsync(cmd, query, source, evidence =>
            (WindowsProcessEventDecoder.Decode(evidence) ?? LocalProcessEvidence.Decode(evidence))!, ct);
        return new ProcessEventPage(items, query.Limit, items.Count, next);
    }
}
