namespace SysWitness.Storage;

/// <summary>
/// Probes free space on the volume holding the local database. Returns null when
/// the volume cannot be inspected; consumers must fail closed for event writes.
/// </summary>
public interface IStorageSpaceProbe
{
    long? GetAvailableFreeBytes(string databasePath);
}

public sealed class VolumeStorageSpaceProbe : IStorageSpaceProbe
{
    public long? GetAvailableFreeBytes(string databasePath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(databasePath));
            if (string.IsNullOrWhiteSpace(root))
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>A source checkpoint must not advance when disk reserve is unavailable.</summary>
public sealed class StorageDiskSpaceException(string reason)
    : IOException("SysWitness disk reserve check prevented event ingestion.")
{
    public string Reason { get; } = reason;
}
