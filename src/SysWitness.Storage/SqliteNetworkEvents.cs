using SysWitness.Contracts;
using SysWitness.Core;
using static SysWitness.Core.WindowsNetworkEventDecoder;
using static SysWitness.Core.WindowsProcessEventDecoder;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    public Task<NetworkEventPage<NetworkConnectionEvent>> GetNetworkConnectionsAsync(
        EventQuery query, Guid? processGuid = null, CancellationToken ct = default) =>
        ReadNetworkPageAsync(query, processGuid, SysmonNetworkConnect,
            evidence => DecodeConnection(evidence)!, ct);

    public Task<NetworkEventPage<DnsQueryEvent>> GetDnsQueriesAsync(
        EventQuery query, Guid? processGuid = null, CancellationToken ct = default) =>
        ReadNetworkPageAsync(query, processGuid, SysmonDnsQuery,
            evidence => DecodeDnsQuery(evidence)!, ct);

    private async Task<NetworkEventPage<T>> ReadNetworkPageAsync<T>(
        EventQuery query, Guid? processGuid, int sysmonEvent, Func<ObservedEvent, T> project, CancellationToken ct)
    {
        ValidateEventQuery(query);
        if (query.Category is not null || processGuid == Guid.Empty)
            throw new ArgumentException("Invalid network event filter.");

        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        var source = "(channel=$sysmon AND provider=$sysmon_provider AND source_event_id=$sysmon_event)";
        cmd.Parameters.AddWithValue("$sysmon", SysmonChannel);
        cmd.Parameters.AddWithValue("$sysmon_provider", SysmonProvider);
        cmd.Parameters.AddWithValue("$sysmon_event", sysmonEvent);
        if (processGuid is Guid guid)
        {
            BindSysmonProcessGuid(db, cmd, guid);
            source += " AND event_id IN (SELECT event_id FROM process_identity_projection WHERE version=1 AND process_guid=$process_guid)";
        }
        else if (sysmonEvent == SysmonNetworkConnect)
        {
            source = "(" + source + """
                 OR (channel=$security AND provider=$security_provider AND source_event_id IN ($allowed, $blocked)))
                """;
            cmd.Parameters.AddWithValue("$security", SecurityChannel);
            cmd.Parameters.AddWithValue("$security_provider", SecurityProvider);
            cmd.Parameters.AddWithValue("$allowed", FilteringPlatformAllowed);
            cmd.Parameters.AddWithValue("$blocked", FilteringPlatformBlocked);
        }

        var (items, next) = await ReadEvidencePageAsync(cmd, query, source, project, ct);
        return new NetworkEventPage<T>(items, query.Limit, items.Count, next);
    }
}
