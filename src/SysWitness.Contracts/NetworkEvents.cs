namespace SysWitness.Contracts;

public sealed record NetworkEventSource(string Channel, string Provider, int SourceEventId,
    long? SourceRecordId, string? Computer);

public sealed record NetworkProcess(uint? ProcessId, Guid? ProcessGuid, string? ImagePath,
    string Attribution);

/// <summary>The provider's reported endpoint with its role where supported by source evidence.</summary>
public sealed record NetworkEndpoint(string? Address, ushort? Port, string? HostName, string? Role);

public sealed record NetworkConnectionEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ObservedAtUtc,
    NetworkEventSource Source,
    string PayloadStatus,
    NetworkProcess Process,
    string? Protocol,
    byte? ProtocolNumber,
    string? Direction,
    string? Decision,
    NetworkEndpoint SourceEndpoint,
    NetworkEndpoint DestinationEndpoint);

public sealed record DnsQueryEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ObservedAtUtc,
    NetworkEventSource Source,
    string PayloadStatus,
    NetworkProcess Process,
    string? QueryName,
    long? QueryStatus,
    string? QueryResults);

public sealed record NetworkEventPage<T>(IReadOnlyList<T> Items, int Limit, int Returned, string? NextCursor);
