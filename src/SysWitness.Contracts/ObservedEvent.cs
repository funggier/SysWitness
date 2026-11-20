namespace SysWitness.Contracts;

/// <summary>Raw source record and source-provided metadata. Never a claim of malicious activity.</summary>
public sealed record ObservedEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ObservedAtUtc,
    string Category,
    string Channel,
    string Provider,
    int SourceEventId,
    long? SourceRecordId,
    string? Computer,
    string RawXml);

/// <summary>Minimized data returned by the public API. Raw XML is intentionally excluded.</summary>
public sealed record EventSummary(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    string Category,
    string Channel,
    string Provider,
    int SourceEventId,
    long? SourceRecordId);

public sealed record EventPage(IReadOnlyList<EventSummary> Items, int Limit, int Returned, string? NextCursor = null);

public sealed record EventLogWindow(long? OldestRecordId, long? NewestRecordId);

/// <summary>Bounded, structured read-only query. No raw SQL or executable filters.</summary>
public sealed record EventQuery(
    int Limit = 100,
    string? Cursor = null,
    string? Channel = null,
    string? Category = null,
    string? Provider = null,
    int? SourceEventId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);
