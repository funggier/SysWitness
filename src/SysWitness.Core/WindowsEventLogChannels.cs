namespace SysWitness.Core;

/// <summary>
/// Explicit local administrator-controlled allowlist. A data API client cannot enable channels.
/// Collector availability and read permission are checked at runtime, not assumed here.
/// </summary>
public static class WindowsEventLogChannels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "System",
        ["Application"] = "Application",
        [WindowsProcessEventDecoder.SecurityChannel] = WindowsProcessEventDecoder.SecurityChannel,
        [WindowsProcessEventDecoder.SysmonChannel] = WindowsProcessEventDecoder.SysmonChannel
    };

    public static IReadOnlyList<string> DefaultChannels { get; } = ["System", "Application"];

    public static bool IsSupported(string? channel) =>
        channel is not null && Names.ContainsKey(channel);

    public static IReadOnlyList<string> ParseEnabled(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
            return DefaultChannels.ToArray();

        var configured = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in configuration.Split(','))
        {
            var name = fragment.Trim();
            if (name.Length == 0 || !Names.TryGetValue(name, out var canonicalName))
                throw new ArgumentException(
                    "Unsupported or empty SYSWITNESS_EVENT_CHANNELS value. " +
                    "Allowed: System, Application, Security, Microsoft-Windows-Sysmon/Operational.");
            if (seen.Add(canonicalName))
                configured.Add(canonicalName);
        }
        return configured;
    }
}
