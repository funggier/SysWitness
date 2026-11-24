using SysWitness.Collectors.Windows;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

namespace SysWitness.Api;

public enum NetworkCollectionMode { Auto, Off }

internal sealed record NetworkPartitionCoverage(string Protocol, string AddressFamily,
    string Status, int EndpointCount, uint? NativeError, string? LastError);

internal sealed record NetworkCollectorCoverage(
    string Mode, string ActiveSource, string Status, DateTimeOffset? LastPollAtUtc,
    long StoredObservations, long StoragePauseCount, string? LastError,
    IReadOnlyList<NetworkPartitionCoverage> Partitions, string Completeness);

/// <summary>Read-only diagnostics; configuration is accepted only from local startup settings.</summary>
internal sealed class NetworkCollectorState(NetworkCollectionMode mode)
{
    private NetworkCollectorCoverage _coverage = new(mode.ToString().ToLowerInvariant(), "iphelper-snapshot",
        mode == NetworkCollectionMode.Off ? "disabled" : "starting", null, 0, 0, null, [],
        "NOT GUARANTEED: 3-second independent TCP/UDP IPv4/IPv6 snapshots; short-lived endpoints can be missed. " +
        "Diffs are observations, not connect/disconnect events. PIDs are not process-instance identities.");
    public NetworkCollectorCoverage Snapshot() => Volatile.Read(ref _coverage);
    internal void Set(NetworkCollectorCoverage value) => Volatile.Write(ref _coverage, value);

    public static NetworkCollectionMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => NetworkCollectionMode.Auto,
        "off" => NetworkCollectionMode.Off,
        _ => throw new InvalidOperationException("SYSWITNESS_NETWORK_MODE must be auto or off.")
    };
}

internal sealed class WindowsNetworkCollectionService(
    NetworkCollectionMode mode, SqliteEventStore store, NetworkCollectorState state,
    ILogger<WindowsNetworkCollectionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (mode == NetworkCollectionMode.Off) return;
        if (!OperatingSystem.IsWindows())
        {
            state.Set(state.Snapshot() with { Status = "degraded", LastError = "windows_required" });
            return;
        }
        var reader = new NativeNetworkSnapshotReader();
        var differ = new NetworkSnapshotDiffer();
        while (!stoppingToken.IsCancellationRequested)
        {
            var capture = reader.Capture(stoppingToken);
            var diagnostics = capture.Partitions.Select(p => new NetworkPartitionCoverage(
                p.Protocol, p.AddressFamily, p.Succeeded ? "collecting" : "degraded", p.Rows.Count,
                p.NativeError, p.FailureReason)).ToArray();
            state.Set(state.Snapshot() with {
                Status = capture.Partitions.All(p => p.Succeeded) ? "collecting" : "degraded",
                LastPollAtUtc = capture.CompletedAtUtc, Partitions = diagnostics,
                LastError = capture.Partitions.Any(p => !p.Succeeded) ? "native_partition_unavailable" : null });
            // No next capture or diff advance while storage is paused: bounded pending evidence is retried.
            var observations = differ.Compare(capture, Environment.MachineName);
            for (var offset = 0; offset < observations.Count; offset += 256)
                await PersistAsync(observations.Skip(offset).Take(256).ToArray(), stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task PersistAsync(IReadOnlyList<ObservedEvent> observations, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var inserted = await store.InsertAsync(observations, ct);
                state.Set(state.Snapshot() with { StoredObservations = state.Snapshot().StoredObservations + inserted });
                return;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                var reason = error switch {
                    StoragePressureException => "storage_soft_limit_reached",
                    StorageDiskSpaceException disk => disk.Reason,
                    _ => "network_storage_unavailable"
                };
                var current = state.Snapshot();
                state.Set(current with { Status = "paused", LastError = reason,
                    StoragePauseCount = current.StoragePauseCount + 1 });
                // Do not log endpoint values, payloads, SQL text, paths, or exception messages.
                logger.LogWarning("Network observation storage paused: {Reason}", reason);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        ct.ThrowIfCancellationRequested();
    }
}
