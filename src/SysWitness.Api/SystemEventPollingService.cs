using SysWitness.Core;
using SysWitness.Storage;

namespace SysWitness.Api;

/// <summary>Read-only polling prototype; no Windows audit policy modifications.</summary>
public sealed class SystemEventPollingService(
    WindowsEventLogReader source,
    SqliteEventStore store,
    CollectionState state,
    ILogger<SystemEventPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var events = source.ReadLatest(64);
                var added = await store.InsertAsync(events, stoppingToken);
                state.SetSuccess();
                logger.LogInformation("Observation poll: read {ReadCount}, added {AddedCount}", events.Count, added);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                state.SetError(error.GetType().Name);
                logger.LogWarning("Observation polling degraded ({ErrorType})", error.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
