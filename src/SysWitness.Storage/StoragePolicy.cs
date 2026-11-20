using System.Globalization;

namespace SysWitness.Storage;

/// <summary>
/// Local-only storage controls. No remote API or AI can change this policy.
/// Retention is OFF unless explicitly set; the soft storage budget pauses ingestion.
/// </summary>
public sealed record StoragePolicy(long SoftLimitBytes, int? RetentionDays)
{
    public static StoragePolicy Default { get; } = new(4L * 1024 * 1024 * 1024, null);

    public static StoragePolicy FromEnvironment()
    {
        var limitMb = 4096;
        var limitText = Environment.GetEnvironmentVariable("SYSWITNESS_STORAGE_SOFT_LIMIT_MB");
        if (!string.IsNullOrWhiteSpace(limitText) &&
            (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limitMb)
             || limitMb is < 16 or > 1048576))
            throw new ArgumentException("SYSWITNESS_STORAGE_SOFT_LIMIT_MB must be 16..1048576.");

        int? days = null;
        var retentionText = Environment.GetEnvironmentVariable("SYSWITNESS_RETENTION_DAYS");
        if (!string.IsNullOrWhiteSpace(retentionText))
        {
            if (!int.TryParse(retentionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed is < 1 or > 3650)
                throw new ArgumentException("SYSWITNESS_RETENTION_DAYS must be 1..3650 or unset.");
            days = parsed;
        }

        return new StoragePolicy((long)limitMb * 1024 * 1024, days);
    }
}

/// <summary>
/// Safe stop: no source bookmark advances when the local storage soft budget is exceeded.
/// </summary>
public sealed class StoragePressureException : IOException
{
    public StoragePressureException() : base("SysWitness storage soft limit reached; event ingest paused.") { }
}

public sealed record RetentionDeletion(
    long DeletionId,
    DateTimeOffset DeletedAtUtc,
    string Channel,
    long EventCount,
    DateTimeOffset OldestObservedAtUtc,
    DateTimeOffset NewestObservedAtUtc,
    DateTimeOffset CutoffUtc);

public sealed record StorageStatus(
    long DatabaseBytes,
    long WalBytes,
    long ShmBytes,
    long TotalPhysicalBytes,
    long SoftLimitBytes,
    bool SoftLimitReached,
    int? RetentionDays,
    long StoredEventCount,
    long RecordedRetentionDeletedEvents,
    string Completeness);
