namespace SysWitness.Contracts;

/// <summary>A PID-attributed endpoint at capture time. No process-instance identity or traffic direction is inferred.</summary>
public sealed record NetworkInventoryRow(
    string Protocol, string AddressFamily, uint ProcessId,
    string LocalAddress, ushort LocalPort, uint LocalScopeId,
    string? RemoteAddress, ushort? RemotePort, uint? RemoteScopeId,
    uint? TcpState, string? TcpStateName);

public sealed record NetworkSnapshotPartition(
    string Protocol, string AddressFamily, bool Succeeded, uint? NativeError,
    string? FailureReason, IReadOnlyList<NetworkInventoryRow> Rows);

/// <summary>Four independently read tables, not a simultaneous system-wide snapshot.</summary>
public sealed record NetworkSnapshot(DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    IReadOnlyList<NetworkSnapshotPartition> Partitions);

/// <summary>First presence, changed fields, or absence from a later successful table; never an actual connect/disconnect event.</summary>
public sealed record NetworkInventoryObservation(
    string EventId, DateTimeOffset OccurredAtUtc, DateTimeOffset ObservedAtUtc,
    NetworkEventSource Source, string PayloadStatus, string Change, string Quality,
    string ProcessAttribution, NetworkInventoryRow? Endpoint,
    DateTimeOffset? CaptureStartedAtUtc, DateTimeOffset? CaptureCompletedAtUtc);
