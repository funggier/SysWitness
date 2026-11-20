namespace SysWitness.Api;

/// <summary>
/// Source-specific advisory status. Not evidence of complete or lossless collection.
/// </summary>
public sealed record ChannelCoverage(
    string Channel,
    string Status,
    DateTimeOffset? LastPollAtUtc,
    int LastReadCount,
    long? LastRecordId,
    long RecordedGaps,
    string LastError,
    string Completeness);

/// <summary>
/// One synchronized status registry for all locally configured Event Log channels.
/// API clients can read statuses, but cannot change the channel list or collection settings.
/// </summary>
public sealed class CollectionState
{
    private const string CoverageDisclaimer =
        "NOT GUARANTEED: initial latest-256 backfill; native bookmark thereafter; " +
        "channel may clear, rotate, deny access or lose records.";

    private sealed class ChannelState(string channel)
    {
        public string Channel { get; } = channel;
        public string Status { get; set; } = "starting";
        public string LastError { get; set; } = "";
        public DateTimeOffset? LastPollAtUtc { get; set; }
        public int LastReadCount { get; set; }
        public long RecordedGaps { get; set; }
        public long? LastRecordId { get; set; }

        public ChannelCoverage Snapshot() => new(
            Channel, Status, LastPollAtUtc, LastReadCount, LastRecordId,
            RecordedGaps, LastError, CoverageDisclaimer);
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, ChannelState> _sources;
    private readonly string[] _orderedChannels;

    public CollectionState(IReadOnlyList<string> channels)
    {
        if (channels.Count == 0)
            throw new ArgumentException("At least one collector channel must be configured.", nameof(channels));
        _orderedChannels = channels.ToArray();
        _sources = new Dictionary<string, ChannelState>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            if (!_sources.TryAdd(channel, new ChannelState(channel)))
                throw new ArgumentException("Duplicate collector channel.", nameof(channels));
        }
    }

    public IReadOnlyList<string> Channels => _orderedChannels;

    public bool IsConfigured(string channel) => _sources.ContainsKey(channel);

    public void SetSuccess(string channel, int count, long gaps, long? recordId)
    {
        lock (_sync)
        {
            var state = _sources[channel];
            state.LastPollAtUtc = DateTimeOffset.UtcNow;
            state.LastReadCount = count;
            state.LastError = "";
            state.RecordedGaps = gaps;
            state.LastRecordId = recordId;
            state.Status = "collecting";
        }
    }

    public void SetError(string channel, string reason)
    {
        lock (_sync)
        {
            var state = _sources[channel];
            state.LastError = reason;
            state.Status = "degraded";
        }
    }

    public ChannelCoverage? ForChannel(string channel)
    {
        lock (_sync)
        {
            return _sources.TryGetValue(channel, out var state) ? state.Snapshot() : null;
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            var channels = _orderedChannels.Select(channel => _sources[channel].Snapshot()).ToArray();
            var primary = _sources.TryGetValue("System", out var system)
                ? system.Snapshot()
                : channels[0];
            var status = channels.Any(x => x.Status == "degraded") ? "degraded" :
                channels.All(x => x.Status == "collecting") ? "collecting" : "starting";

            // Top-level System fields preserve compatibility with the initial single-channel API.
            return new
            {
                channel = primary.Channel,
                status,
                primaryStatus = primary.Status,
                lastPollAtUtc = primary.LastPollAtUtc,
                lastReadCount = primary.LastReadCount,
                lastRecordId = primary.LastRecordId,
                recordedGaps = primary.RecordedGaps,
                lastError = primary.LastError,
                completeness = CoverageDisclaimer,
                configuredCollectors = channels.Select(c => "windows-eventlog:" + c.Channel).ToArray(),
                enabledCollectors = channels.Where(c => c.Status == "collecting")
                    .Select(c => "windows-eventlog:" + c.Channel).ToArray(),
                degradedCollectors = channels.Where(c => c.Status == "degraded")
                    .Select(c => "windows-eventlog:" + c.Channel).ToArray(),
                channels,
                plannedCollectors = new[] { "ETW", "WFP", "NTFS", "Registry", "Devices" }
            };
        }
    }
}
