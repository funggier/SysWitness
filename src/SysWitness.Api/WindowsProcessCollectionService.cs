using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

namespace SysWitness.Api;

internal sealed class WindowsProcessCollectionService(
    ProcessCollectionMode mode,
    SqliteEventStore store,
    CollectionState state,
    ILogger<WindowsProcessCollectionService> logger) : BackgroundService
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WmiDrainInterval = TimeSpan.FromSeconds(1);
    private readonly string _computer = Environment.MachineName;
    private ProcessCollectorCoverage _coverage = ProcessCollectorCoverage.Initial(mode);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Publish(_coverage with { Status = "degraded", LastError = "windows_required" });
            return;
        }

        if (mode == ProcessCollectionMode.Snapshot)
        {
            await RunSnapshotsAsync(stoppingToken);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var reason = await RunWmiAsync(stoppingToken);
            if (reason is null)
                return;

            logger.LogWarning("Windows process event subscription unavailable: {Reason}", reason);
            if (mode == ProcessCollectionMode.Auto)
            {
                Publish(_coverage with { FallbackReason = reason });
                await RunSnapshotsAsync(stoppingToken);
                return;
            }

            Publish(_coverage with { Status = "degraded", LastError = reason });
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<string?> RunWmiAsync(CancellationToken ct)
    {
        Publish(_coverage with { ActiveSource = "wmi", Status = "starting", LastError = null });
        WmiProcessSubscription subscription;
        try { subscription = WmiProcessSubscription.Open(); }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
            return error is UnauthorizedAccessException or ManagementException { ErrorCode: ManagementStatus.AccessDenied }
                || error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80041003)
                ? "wmi_access_denied"
                : "wmi_subscription_failed";
        }

        using (subscription)
        {
            var previousDrops = _coverage.DroppedEvents;
            var batch = new List<ObservedEvent>(256);
            while (!ct.IsCancellationRequested)
            {
                batch.Clear();
                while (batch.Count < 256 && subscription.Events.TryRead(out var observation))
                    batch.Add(observation);

                await PersistAsync(batch, ct);
                MarkCollecting(previousDrops + subscription.Dropped);
                if (batch.Count < 256)
                {
                    if (subscription.Failure is { } reason)
                        return reason;
                    await Task.Delay(WmiDrainInterval, ct);
                }
            }
            return null;
        }
    }

    private async Task RunSnapshotsAsync(CancellationToken ct)
    {
        Publish(_coverage with { ActiveSource = "snapshot", Status = "starting", LastError = null });
        IReadOnlyDictionary<uint, ProcessSample>? previous = null;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyDictionary<uint, ProcessSample> current;
            var observedAt = DateTimeOffset.UtcNow;
            try { current = WindowsProcessSnapshot.Read(observedAt); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Publish(_coverage with { Status = "degraded", LastError = "process_snapshot_failed" });
                previous = null;
                await Task.Delay(SnapshotInterval, ct);
                continue;
            }

            if (previous is not null)
            {
                var changes = WindowsProcessSnapshot.Compare(previous, current, observedAt, _computer);
                await PersistAsync(changes, ct);
            }

            previous = current;
            MarkCollecting(_coverage.DroppedEvents);
            await Task.Delay(SnapshotInterval, ct);
        }
    }

    private async Task PersistAsync(IReadOnlyList<ObservedEvent> observations, CancellationToken ct)
    {
        if (observations.Count == 0)
            return;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await store.InsertAsync(observations, ct);
                return;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                var reason = error switch
                {
                    StoragePressureException => "storage_soft_limit_reached",
                    StorageDiskSpaceException disk => disk.Reason,
                    _ => error.GetType().Name
                };
                Publish(_coverage with { Status = "degraded", LastError = reason });
                logger.LogWarning("Process observation storage paused: {Reason}", reason);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private void MarkCollecting(long dropped) => Publish(_coverage with
    {
        Status = "collecting",
        LastError = null,
        LastPollAtUtc = DateTimeOffset.UtcNow,
        DroppedEvents = dropped
    });

    private void Publish(ProcessCollectorCoverage coverage)
    {
        _coverage = coverage;
        state.SetProcessCollector(coverage);
    }
}
