using SysWitness.Contracts;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed partial class SqliteEventStore
{
    /// <summary>Projects only fixed source families. Source and object-type restrictions apply before keyset pagination.</summary>
    public async Task<SystemActivityPage> GetSystemActivityAsync(
        EventQuery query, string kind, CancellationToken ct = default)
    {
        ValidateEventQuery(query);
        if (query.Category is not null)
            throw new ArgumentException("System activity does not accept category filters.", nameof(query));
        var rules = WindowsSystemActivityDecoder.GetSourceRules(kind);
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        db.CreateFunction("syswitness_activity_payload",
            (string xml, string predicate) => WindowsSystemActivityDecoder.MatchesPayload(xml, predicate) ? 1 : 0,
            isDeterministic: true);

        var clauses = new List<string>();
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var channel = "$activity_channel_" + index;
            var provider = "$activity_provider_" + index;
            cmd.Parameters.AddWithValue(channel, rule.Channel);
            cmd.Parameters.AddWithValue(provider, rule.Provider);
            var ids = new List<string>();
            for (var n = 0; n < rule.EventIds.Count; n++)
            {
                var parameter = "$activity_id_" + index + "_" + n;
                ids.Add(parameter);
                cmd.Parameters.AddWithValue(parameter, rule.EventIds[n]);
            }
            var clause = "(channel=" + channel + " AND provider=" + provider +
                " AND source_event_id IN (" + string.Join(",", ids) + ")";
            if (rule.PayloadPredicate is { } predicate)
            {
                var parameter = "$activity_predicate_" + index;
                cmd.Parameters.AddWithValue(parameter, predicate);
                clause += " AND syswitness_activity_payload(raw_xml," + parameter + ")=1";
            }
            clauses.Add(clause + ")");
        }
        var source = "(" + string.Join(" OR ", clauses) + ")";
        var (items, next) = await ReadEvidencePageAsync(cmd, query, source,
            evidence => WindowsSystemActivityDecoder.Decode(evidence, kind) ??
                throw new InvalidOperationException("A selected activity source did not match its projection."), ct);
        return new SystemActivityPage(kind, items, query.Limit, items.Count, next,
            WindowsSystemActivityDecoder.GetLimitations(kind));
    }
}
