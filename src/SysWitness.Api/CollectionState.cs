namespace SysWitness.Api;

/// <summary>Thread-safe advisory state. Never claims complete coverage or proves absence of events.</summary>
public sealed class CollectionState
{
    private readonly object _sync = new();
    private string _status = "starting";
    private string _lastError = "";
    private DateTimeOffset? _lastPollAtUtc;
    private int _lastReadCount;
    private long _gapCount;
    private long? _lastRecordId;

    public void SetSuccess(int count, long gaps, long? recordId)
    {
        lock (_sync)
        {
            _lastPollAtUtc = DateTimeOffset.UtcNow;
            _lastReadCount = count;
            _lastError = "";
            _gapCount = gaps;
            _lastRecordId = recordId;
            _status = "collecting";
        }
    }

    public void SetError(string reason)
    {
        lock (_sync)
        {
            _lastError = reason;
            _status = "degraded";
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            return new
            {
                channel = "System",
                status = _status,
                lastPollAtUtc = _lastPollAtUtc,
                lastReadCount = _lastReadCount,
                lastRecordId = _lastRecordId,
                recordedGaps = _gapCount,
                lastError = _lastError,
                completeness = "NOT GUARANTEED: initial latest-256 backfill; native bookmark thereafter; source may clear, rotate or lose records",
                enabledCollectors = new[] { "windows-eventlog:System" },
                plannedCollectors = new[] { "Sysmon", "ETW", "WFP", "NTFS", "Registry", "Devices" }
            };
        }
    }
}
