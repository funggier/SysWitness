namespace SysWitness.Api;

/// <summary>In-memory advisory status; not a guarantee of complete coverage.</summary>
public sealed class CollectionState
{
    private string _status = "starting";
    private string _lastError = "";
    private DateTimeOffset? _lastPollAtUtc;

    public void SetSuccess() { _lastPollAtUtc = DateTimeOffset.UtcNow; _lastError = ""; _status = "sampling"; }
    public void SetError(string message) { _lastError = message; _status = "degraded"; }

    public object Snapshot() => new
    {
        channel = "System",
        status = _status,
        lastPollAtUtc = _lastPollAtUtc,
        lastError = _lastError,
        completeness = "UNVERIFIED: latest-64-records polling every 15s; events may be missed",
        enabledCollectors = new[] { "windows-eventlog:System" },
        plannedCollectors = new[] { "Sysmon", "ETW", "WFP", "NTFS", "Registry", "Devices" }
    };
}
