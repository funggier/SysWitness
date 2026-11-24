using SysWitness.Contracts;

namespace SysWitness.Core;

/// <summary>Builds a bounded descendant graph with parent reference nodes and explicit ambiguity.</summary>
public sealed class ProcessTreeBuilder
{
    public const int SourceEventLimit = 1000;
    public const string Method = "sysmon_reported_parent_guid_and_computer_v1";
    private readonly Guid _root;
    private readonly string _computer;
    private readonly int _maxDepth;
    private readonly int _nodeLimit;
    private readonly Dictionary<Guid, List<ProcessEvent>> _nodes = [];
    private readonly Dictionary<Guid, int> _depths = [];
    private readonly HashSet<Guid> _expanded = [];
    private readonly HashSet<string> _evidenceIds = [];
    private readonly List<ProcessTreeRelationship> _relationships = [];
    private readonly List<UnresolvedProcessRelationship> _unresolved = [];
    private bool _depthLimitReached;
    private bool _nodeLimitReached;

    public ProcessTreeBuilder(Guid root, string computer, int maxDepth = 3, int nodeLimit = 100)
    {
        if (root == Guid.Empty || string.IsNullOrWhiteSpace(computer) ||
            computer.Length > 256 || computer.Any(char.IsControl) ||
            maxDepth is < 0 or > 8 || nodeLimit is < 1 or > 256)
            throw new ArgumentException("Invalid process tree identity or limits.");
        _root = root;
        _computer = computer;
        _maxDepth = maxDepth;
        _nodeLimit = nodeLimit;
        _nodes.Add(root, []);
        _depths.Add(root, 0);
    }

    public IReadOnlyCollection<string> EvidenceIds => _evidenceIds;

    public IReadOnlyList<Guid> GetFrontier()
    {
        var pending = _depths.Where(pair => !_expanded.Contains(pair.Key)).ToArray();
        if (pending.Length == 0) return [];
        var depth = pending.Min(pair => pair.Value);
        return pending.Where(pair => pair.Value == depth).Select(pair => pair.Key).Order().ToArray();
    }

    public void MarkExpanded(IEnumerable<Guid> frontier) => _expanded.UnionWith(frontier);

    public void Add(ProcessEvent creation)
    {
        if (creation.Kind != "start" || creation.Source.Channel != WindowsProcessEventDecoder.SysmonChannel ||
            creation.Source.Provider != WindowsProcessEventDecoder.SysmonProvider ||
            creation.Source.SourceEventId != 1 || creation.Source.Computer != _computer)
            throw new ArgumentException("Tree evidence must be a Sysmon creation from the selected computer.");
        if (!_evidenceIds.Add(creation.EventId)) return;

        var parent = creation.Parent?.ProcessGuid;
        if (creation.ProcessGuid is not { } child)
        {
            _unresolved.Add(new("subject_guid_unavailable", creation));
            return;
        }

        if (!_depths.ContainsKey(child))
        {
            if (parent is not { } ancestor || !_depths.TryGetValue(ancestor, out var parentDepth))
                return;
            if (parentDepth >= _maxDepth)
            {
                _depthLimitReached = true;
                return;
            }
            if (!EnsureNode(child)) return;
            _depths[child] = parentDepth + 1;
        }
        _nodes[child].Add(creation);

        if (parent is null)
        {
            _unresolved.Add(new("parent_guid_unavailable", creation));
            return;
        }
        if (parent == child)
        {
            _unresolved.Add(new("self_reference", creation));
            return;
        }
        if (!EnsureNode(parent.Value)) return;
        _relationships.Add(new(parent.Value, child, "sourceReported", creation));
    }

    private bool EnsureNode(Guid guid)
    {
        if (_nodes.ContainsKey(guid)) return true;
        if (_nodes.Count >= _nodeLimit)
        {
            _nodeLimitReached = true;
            return false;
        }
        _nodes.Add(guid, []);
        return true;
    }

    public ProcessTree Snapshot(bool evidenceLimitReached)
    {
        var nodes = _nodes.OrderBy(pair => _depths.GetValueOrDefault(pair.Key, int.MaxValue))
            .ThenBy(pair => pair.Key).Select(pair => new ProcessTreeNode(pair.Key,
                _depths.TryGetValue(pair.Key, out var depth) ? depth : null,
                pair.Value.Count > 0 ? "creationInView" : "referenceOnly",
                pair.Value.Select(item => item.Parent?.ProcessGuid)
                    .Where(guid => guid is not null).Distinct().Skip(1).Any(),
                pair.Value.OrderByDescending(item => item.OccurredAtUtc)
                    .ThenByDescending(item => item.EventId, StringComparer.Ordinal).ToArray())).ToArray();
        return new ProcessTree(_root, _computer, Method, "derived", _maxDepth, _nodeLimit,
            SourceEventLimit, _evidenceIds.Count, _depthLimitReached, _nodeLimitReached,
            evidenceLimitReached, HasCycle(), nodes,
            _relationships.OrderByDescending(edge => edge.Evidence.OccurredAtUtc)
                .ThenByDescending(edge => edge.Evidence.EventId, StringComparer.Ordinal).ToArray(),
            _unresolved.ToArray(),
            [
                "Parent links are reported by Sysmon, not independently verified execution causality.",
                "Only this computer's retained Sysmon creation records are included; PID-only sources are not joined.",
                "The root and descendants are expanded; other parent nodes are references, not a full ancestor search.",
                "Depth, node or source-event limits can omit relationships; database scans have no duration guarantee.",
                "Reference-only nodes and missing records do not prove that a process never existed.",
                "Conflicting parents and cycles are retained or flagged rather than silently resolved.",
                "Source gaps, retention and source timestamp errors can leave an incomplete history."
            ]);
    }

    private bool HasCycle()
    {
        if (_unresolved.Any(item => item.Reason == "self_reference")) return true;
        var pairs = _relationships.Select(edge => (Parent: edge.ParentProcessGuid, Child: edge.ChildProcessGuid))
            .Distinct().ToArray();
        var incoming = _nodes.Keys.ToDictionary(guid => guid, _ => 0);
        var children = _nodes.Keys.ToDictionary(guid => guid, _ => new List<Guid>());
        foreach (var pair in pairs)
        {
            incoming[pair.Child]++;
            children[pair.Parent].Add(pair.Child);
        }
        var ready = new Queue<Guid>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var removed = 0;
        while (ready.TryDequeue(out var guid))
        {
            removed++;
            foreach (var child in children[guid])
                if (--incoming[child] == 0) ready.Enqueue(child);
        }
        return removed != _nodes.Count;
    }
}
