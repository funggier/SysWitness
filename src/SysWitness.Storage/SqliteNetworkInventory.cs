using SysWitness.Contracts;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    /// <summary>Pages first/changed/missing inventory evidence, not a claim of currently live sockets.</summary>
    public async Task<NetworkEventPage<NetworkInventoryObservation>> GetNetworkInventoryAsync(
        EventQuery query, CancellationToken ct = default)
    {
        ValidateEventQuery(query);
        if (query.Category is not null)
            throw new ArgumentException("Category is not an inventory filter.");
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$inventory_channel", LocalNetworkEvidence.SnapshotChannel);
        cmd.Parameters.AddWithValue("$inventory_provider", LocalNetworkEvidence.SnapshotProvider);
        const string source = "channel=$inventory_channel AND provider=$inventory_provider AND source_event_id IN (1,2,3)";
        var (items, cursor) = await ReadEvidencePageAsync(cmd, query, source,
            e => LocalNetworkEvidence.Decode(e)!, ct);
        return new(items, query.Limit, items.Count, cursor);
    }
}
