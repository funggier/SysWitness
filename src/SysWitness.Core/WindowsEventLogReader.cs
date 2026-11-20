using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using SysWitness.Contracts;

namespace SysWitness.Core;

/// <summary>
/// Uses Windows native EventBookmarks to resume after the last durably stored record.
/// On first use, intentionally backfills only the last N records.
/// </summary>
public sealed class WindowsEventLogReader
{
    private readonly string _channel;

    public WindowsEventLogReader(string channel = "System")
    {
        if (!WindowsEventLogChannels.IsSupported(channel))
            throw new ArgumentException("Unsupported Windows Event Log channel.", nameof(channel));
        _channel = channel;
    }

    public string Channel => _channel;

    /// <summary>Oldest/newest record identifiers still exposed by the source channel.</summary>
    public EventLogWindow ReadAvailableWindow()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Event Log requires Windows.");
        return new EventLogWindow(ReadEdge(reverse: false), ReadEdge(reverse: true));
    }

    /// <summary>
    /// Reads forward from bookmark; no bookmark backfills the most recent maxEvents.
    /// HasMore is conservative (true if maxEvents was reached); no claim of zero loss.
    /// </summary>
    public WindowsEventBatch ReadBatch(string? bookmarkXml, int maxEvents)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Event Log requires Windows.");
        if (maxEvents is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));

        var bootstrap = string.IsNullOrWhiteSpace(bookmarkXml);
        var query = new EventLogQuery(_channel, PathType.LogName) { ReverseDirection = bootstrap };
        using var reader = bootstrap
            ? new EventLogReader(query)
            : new EventLogReader(query, new EventBookmark(bookmarkXml!));

        var events = new List<ObservedEvent>(maxEvents);
        string? finalBookmark = null;
        long? finalRecordId = null;

        for (var i = 0; i < maxEvents; i++)
        {
            using var record = reader.ReadEvent();
            if (record is null)
                break;

            var xml = record.ToXml();
            var bookmark = record.Bookmark?.BookmarkXml;
            if (string.IsNullOrWhiteSpace(bookmark))
                throw new InvalidOperationException("Windows Event Log record did not supply a bookmark.");

            // Reverse initial read: first record is newest and owns the final bookmark.
            if (!bootstrap || i == 0)
            {
                finalBookmark = bookmark;
                finalRecordId = record.RecordId;
            }

            events.Add(new ObservedEvent(
                EventIdentity.FromXml(_channel, xml),
                new DateTimeOffset((record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime(), TimeSpan.Zero),
                DateTimeOffset.UtcNow,
                "windows.event",
                _channel,
                record.ProviderName ?? "unknown",
                record.Id,
                record.RecordId,
                record.MachineName,
                xml));
        }

        if (bootstrap)
            events.Reverse();

        return new WindowsEventBatch(events, finalBookmark, finalRecordId,
            Bootstrap: bootstrap, MayHaveMore: events.Count == maxEvents);
    }

    [SupportedOSPlatform("windows")]
    private long? ReadEdge(bool reverse)
    {
        var query = new EventLogQuery(_channel, PathType.LogName) { ReverseDirection = reverse };
        using var reader = new EventLogReader(query);
        using var record = reader.ReadEvent();
        return record?.RecordId;
    }

}

public sealed record WindowsEventBatch(
    IReadOnlyList<ObservedEvent> Events,
    string? LastBookmarkXml,
    long? LastRecordId,
    bool Bootstrap,
    bool MayHaveMore);
