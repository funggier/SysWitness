namespace SysWitness.Contracts;

/// <summary>A derived grouping of source observations, not a causal DNS-to-connection claim.</summary>
public sealed record ProcessActivityPage(
    Guid ProcessGuid,
    string Computer,
    string CorrelationMethod,
    string Quality,
    IReadOnlyList<ProcessActivityEvent> Items,
    int Limit,
    int Returned,
    string? NextCursor,
    IReadOnlyList<string> Limitations);

/// <summary>Exactly one typed payload is populated; its source and EventId reference retained evidence.</summary>
public sealed record ProcessActivityEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ObservedAtUtc,
    string Kind,
    ProcessEvent? Lifecycle = null,
    NetworkConnectionEvent? Connection = null,
    DnsQueryEvent? DnsQuery = null);
