using System.Diagnostics.Eventing.Reader;
using SysWitness.Contracts;

namespace SysWitness.Core;

/// <summary>
/// Windows Event Log read-only polling proof of concept.
/// Reads the latest N records, not a lossless live subscription or verified checkpointed stream.
/// </summary>
public sealed class WindowsEventLogReader
{
    private readonly string _channel;

    public WindowsEventLogReader(string channel = "System")
    {
        if (channel is not ("System" or "Application"))
            throw new ArgumentException("Only the safe initial channels System and Application are supported.", nameof(channel));
        _channel = channel;
    }

    public string Channel => _channel;

    public IReadOnlyList<ObservedEvent> ReadLatest(int limit)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Event Log collection requires Windows.");
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be from 1 to 1000.");

        var result = new List<ObservedEvent>(limit);
        var query = new EventLogQuery(_channel, PathType.LogName) { ReverseDirection = true };
        using var reader = new EventLogReader(query);

        for (var index = 0; index < limit; index++)
        {
            using var record = reader.ReadEvent();
            if (record is null)
                break;

            var raw = record.ToXml(); // source record, stored only locally
            result.Add(new ObservedEvent(
                EventIdentity.FromXml(_channel, raw),
                new DateTimeOffset((record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime(), TimeSpan.Zero),
                DateTimeOffset.UtcNow,
                "windows.event",
                _channel,
                record.ProviderName ?? "unknown",
                record.Id,
                record.RecordId,
                record.MachineName,
                raw));
        }

        return result;
    }
}
