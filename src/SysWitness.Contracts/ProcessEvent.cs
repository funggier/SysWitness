namespace SysWitness.Contracts;

/// <summary>Process lifecycle fields reported by a single Windows Event Log record.</summary>
public sealed record ProcessEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ObservedAtUtc,
    string Kind,
    ProcessEventSource Source,
    string PayloadStatus,
    uint? ProcessId,
    Guid? ProcessGuid,
    string? ImagePath,
    string? CommandLine,
    ProcessEventParent? Parent,
    uint? ExitStatus);

public sealed record ProcessEventSource(string Channel, string Provider,
    int SourceEventId, long? SourceRecordId, string? Computer);

public sealed record ProcessEventParent(uint? ProcessId, Guid? ProcessGuid, string? ImagePath);

public sealed record ProcessEventPage(IReadOnlyList<ProcessEvent> Items, int Limit, int Returned, string? NextCursor);
