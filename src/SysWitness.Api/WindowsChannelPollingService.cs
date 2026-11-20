using System.Diagnostics.Eventing.Reader;
using SysWitness.Core;
using SysWitness.Storage;

namespace SysWitness.Api;

/// <summary>
/// Read-only Windows collector. EventBookmark and records are committed in one SQLite transaction.
/// A cleared/overwritten log is recorded as a coverage gap; this is not lossless ETW streaming.
/// </summary>
public sealed class WindowsChannelPollingService(
    WindowsEventLogReader source,
    SqliteEventStore store,
    CollectionState state,
    ILogger<WindowsChannelPollingService> logger) : BackgroundService
{
    private const int MaxBatch = 256;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var mayHaveMore = false;
            var sourceFailed = false;
            try
            {
                var checkpoint = await store.GetCheckpointAsync(source.Channel, stoppingToken);
                var gapReason = (string?)null;
                var available = source.ReadAvailableWindow();

                if (checkpoint is not null)
                {
                    if (available.NewestRecordId is long newest && checkpoint.RecordId is long previous &&
                        newest < previous)
                    {
                        gapReason = "source_log_reset_or_cleared";
                    }
                    else if (available.OldestRecordId is long oldest && checkpoint.RecordId is long last &&
                        oldest > last + 1)
                    {
                        gapReason = "source_log_retention_gap";
                    }
                }

                WindowsEventBatch batch;
                try
                {
                    batch = source.ReadBatch(gapReason is null ? checkpoint?.BookmarkXml : null, MaxBatch);
                }
                catch (EventLogException) when (checkpoint is not null && gapReason is null)
                {
                    // Windows can invalidate a bookmark after channel overwrite or clear.
                    gapReason = "invalid_native_bookmark";
                    batch = source.ReadBatch(null, MaxBatch);
                }

                // The initial bounded backfill must not masquerade as full historical coverage.
                if (checkpoint is null && batch.Bootstrap && gapReason is null &&
                    available.OldestRecordId is long oldestAvailable &&
                    batch.Events.Count > 0 &&
                    batch.Events[0].SourceRecordId is long firstCollected &&
                    firstCollected > oldestAvailable)
                {
                    gapReason = "initial_history_outside_backfill_window";
                }

                var added = await store.CommitBatchAsync(
                    source.Channel, batch.Events, batch.LastBookmarkXml, batch.LastRecordId,
                    gapReason, checkpoint?.RecordId, available, stoppingToken);
                mayHaveMore = !batch.Bootstrap && batch.MayHaveMore;
                var recordedGaps = await store.CountGapsAsync(source.Channel, stoppingToken);
                // An empty polling cycle does not erase the durable previously committed position.
                var visibleRecordId = batch.LastRecordId ??
                    (gapReason is null ? checkpoint?.RecordId : null);
                state.SetSuccess(source.Channel, batch.Events.Count, recordedGaps, visibleRecordId);
                if (gapReason is not null)
                    logger.LogWarning("Source coverage gap detected: {Reason} on {Channel}", gapReason, source.Channel);
                logger.LogDebug("Channel {Channel}: read {Count}, stored {Inserted}",
                    source.Channel, batch.Events.Count, added);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (StoragePressureException)
            {
                sourceFailed = true;
                state.SetError(source.Channel, "storage_soft_limit_reached");
                logger.LogWarning("Observation paused on {Channel}: storage soft limit reached. Native bookmark unchanged.",
                    source.Channel);
            }
            catch (Exception error)
            {
                sourceFailed = true;
                state.SetError(source.Channel, error.GetType().Name);
                logger.LogWarning("Event collection degraded on {Channel}: {ErrorType}",
                    source.Channel, error.GetType().Name);
            }

            // Unavailable optional channels must not flood logs or slow other collectors.
            var delay = sourceFailed ? TimeSpan.FromSeconds(30) :
                mayHaveMore ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(5);
            await Task.Delay(delay, stoppingToken);
        }
    }
}
