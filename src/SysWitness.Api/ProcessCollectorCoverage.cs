namespace SysWitness.Api;

public enum ProcessCollectionMode
{
    Auto,
    Snapshot,
    Wmi,
    Off
}

internal sealed record ProcessCollectorCoverage(
    string Mode, string? ActiveSource, string Status, string? FallbackReason = null,
    string? LastError = null, DateTimeOffset? LastPollAtUtc = null, long DroppedEvents = 0)
{
    public static ProcessCollectorCoverage Initial(ProcessCollectionMode mode) =>
        new(mode.ToString().ToLowerInvariant(), null, mode == ProcessCollectionMode.Off ? "disabled" : "starting");

    public static ProcessCollectionMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => ProcessCollectionMode.Auto,
        "snapshot" => ProcessCollectionMode.Snapshot,
        "wmi" => ProcessCollectionMode.Wmi,
        "off" => ProcessCollectionMode.Off,
        _ => throw new InvalidOperationException("SYSWITNESS_PROCESS_MODE must be auto, snapshot, wmi or off.")
    };
}
