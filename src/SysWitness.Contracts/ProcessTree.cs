namespace SysWitness.Contracts;

public sealed record ProcessTree(
    Guid RootProcessGuid, string Computer, string Method, string Quality,
    int MaxDepth, int NodeLimit, int SourceEventLimit, int SourceEventsUsed,
    bool DepthLimitReached, bool NodeLimitReached, bool EvidenceLimitReached, bool HasCycle,
    IReadOnlyList<ProcessTreeNode> Nodes, IReadOnlyList<ProcessTreeRelationship> Relationships,
    IReadOnlyList<UnresolvedProcessRelationship> Unresolved, IReadOnlyList<string> Limitations);

public sealed record ProcessTreeNode(Guid ProcessGuid, int? Depth, string EvidenceState,
    bool HasConflictingParents, IReadOnlyList<ProcessEvent> Creations);

/// <summary>The child creation record is the evidence for the source-reported parent relationship.</summary>
public sealed record ProcessTreeRelationship(Guid ParentProcessGuid, Guid ChildProcessGuid,
    string Basis, ProcessEvent Evidence);

public sealed record UnresolvedProcessRelationship(string Reason, ProcessEvent Evidence);
