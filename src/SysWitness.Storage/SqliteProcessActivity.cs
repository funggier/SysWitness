using SysWitness.Contracts;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    public async Task<ProcessActivityPage> GetProcessActivityAsync(
        EventQuery query, Guid processGuid, string computer, CancellationToken ct = default)
    {
        ValidateEventQuery(query);
        if (query.Category is not null || processGuid == Guid.Empty ||
            string.IsNullOrWhiteSpace(computer) || computer.Length > 256 || computer.Any(char.IsControl))
            throw new ArgumentException("Activity requires a nonzero GUID and a recorded computer name.");

        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        // Apply identity and host boundaries before paging. Never infer GUIDs from PIDs,
        // parent GUIDs, image paths or arbitrary mentions in the payload.
        const string source = """
            (channel=$sysmon AND provider=$sysmon_provider AND source_event_id IN (1,3,5,22)
              AND computer=$computer AND event_id IN (SELECT event_id FROM process_identity_projection WHERE version=1 AND computer=$computer AND process_guid=$process_guid))
            """;
        cmd.Parameters.AddWithValue("$sysmon", WindowsProcessEventDecoder.SysmonChannel);
        cmd.Parameters.AddWithValue("$sysmon_provider", WindowsProcessEventDecoder.SysmonProvider);
        cmd.Parameters.AddWithValue("$computer", computer);
        BindSysmonProcessGuid(db, cmd, processGuid);
        var (items, next) = await ReadEvidencePageAsync(cmd, query, source,
            ProcessActivityProjection.Decode, ct);
        return new ProcessActivityPage(processGuid, computer, ProcessActivityProjection.CorrelationMethod,
            "derived", items, query.Limit, items.Count, next, ProcessActivityProjection.Limitations);
    }
}
