using SysWitness.Storage;

/// <summary>Deterministic free-space simulation; never manipulates a real volume.</summary>
internal sealed class StubFreeSpaceProbe(long? available) : IStorageSpaceProbe
{
    public long? GetAvailableFreeBytes(string databasePath) => available;
}
