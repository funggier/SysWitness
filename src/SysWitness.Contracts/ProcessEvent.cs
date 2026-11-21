namespace SysWitness.Contracts;

/// <summary>Process lifecycle fields projected from one stored source observation.</summary>
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
    uint? ExitStatus,
    string ObservationMethod = "eventLog",
    string Quality = "observed",
    string? ProcessName = null,
    DateTimeOffset? ProcessStartedAtUtc = null,
    string? LocalProcessInstanceId = null);

public sealed record ProcessEventSource(string Channel, string Provider,
    int SourceEventId, long? SourceRecordId, string? Computer);

public sealed record ProcessEventParent(uint? ProcessId, Guid? ProcessGuid, string? ImagePath);

public sealed record ProcessEventPage(IReadOnlyList<ProcessEvent> Items, int Limit, int Returned, string? NextCursor);
