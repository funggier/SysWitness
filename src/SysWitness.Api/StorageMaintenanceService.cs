using SysWitness.Storage;

namespace SysWitness.Api;

/// <summary>
/// User-opted-in local retention only. Never changes Windows logs or native bookmarks.
/// Runs in small SQLite batches to keep read-only API and collection responsive.
/// </summary>
public sealed class StorageMaintenanceService(
    SqliteEventStore store,
    ILogger<StorageMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (store.Policy.RetentionDays is null)
            return; // Default: preserve evidence indefinitely unless explicitly configured.

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = 0;
                for (var batch = 0; batch < 20 && !stoppingToken.IsCancellationRequested; batch++)
                {
                    var count = await store.PruneExpiredAsync(
                        DateTimeOffset.UtcNow, 500, stoppingToken);
                    removed += count;
                    if (count < 500) break;
                    await Task.Delay(100, stoppingToken);
                }
                if (removed > 0)
                    logger.LogInformation("Local retention removed {Count} evidence copies; summary records retained.", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Local retention maintenance degraded: {Type}", error.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
