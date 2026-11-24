using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SysWitness.Sdk;

namespace SysWitness.Mcp;

internal static class ToolArguments
{
    public static bool Validate(ToolDefinition tool, JsonElement arguments, out string message)
    {
        message = "Invalid tool arguments.";
        foreach (var field in arguments.EnumerateObject())
        {
            if (!tool.Fields.TryGetValue(field.Name, out var rule)) return false;
            if (rule.Type == "integer")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number) ||
                    number < rule.Minimum || number > rule.Maximum) return false;
            }
            else
            {
                if (field.Value.ValueKind != JsonValueKind.String) return false;
                var value = field.Value.GetString()!;
                if (string.IsNullOrWhiteSpace(value) || value.Length > rule.MaxLength || value.Any(char.IsControl))
                    return false;
                if (rule.Kind == "guid" &&
                    (!Guid.TryParseExact(value, "D", out var guid) || guid == Guid.Empty)) return false;
                if (rule.Kind == "eventId" && (value.Length != 64 || !value.All(Uri.IsHexDigit))) return false;
                if (rule.Kind == "time" && !TryTimestamp(value, out _)) return false;
            }
        }
        foreach (var field in tool.Fields.Where(x => x.Value.Required))
            if (!arguments.TryGetProperty(field.Key, out _)) return false;
        if (String(arguments, "fromUtc") is { } from && String(arguments, "toUtc") is { } to &&
            Timestamp(from) >= Timestamp(to)) return false;
        message = "";
        return true;
    }

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp) &&
        value.Contains('T', StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static string? String(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static int Integer(JsonElement arguments, string name, int fallback) =>
        arguments.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    private static Guid Subject(JsonElement arguments) => Guid.ParseExact(String(arguments, "processGuid")!, "D");

    private static EventReadOptions Page(JsonElement arguments) => new(
        Limit: Integer(arguments, "limit", 100), Cursor: String(arguments, "cursor"),
        Channel: String(arguments, "channel"), Category: String(arguments, "category"),
        Provider: String(arguments, "provider"),
        SourceEventId: arguments.TryGetProperty("sourceEventId", out var id) ? id.GetInt32() : null,
        FromUtc: String(arguments, "fromUtc") is { } from ? Timestamp(from) : null,
        ToUtc: String(arguments, "toUtc") is { } to ? Timestamp(to) : null);

    public static Task<JsonElement> Invoke(SysWitnessClient client, string name, JsonElement arguments, CancellationToken ct) =>
        name switch
        {
            "syswitness_get_health" => client.GetHealthAsync(ct),
            "syswitness_get_events" => client.GetEventsAsync(Page(arguments), ct),
            "syswitness_get_event" => client.GetEventAsync(String(arguments, "eventId")!, ct),
            "syswitness_get_coverage" => client.GetCoverageAsync(String(arguments, "channel"), ct),
            "syswitness_get_gaps" => client.GetGapsAsync(String(arguments, "channel"), Integer(arguments, "limit", 50), ct),
            "syswitness_get_storage_status" => client.GetStorageStatusAsync(ct),
            "syswitness_get_retention" => client.GetRetentionAsync(Integer(arguments, "limit", 50), ct),
            "syswitness_get_process_events" => client.GetProcessEventsAsync(Page(arguments), ct),
            "syswitness_get_process_timeline" => client.GetProcessTimelineAsync(Subject(arguments), Page(arguments), ct),
            "syswitness_get_process_activity" => client.GetProcessActivityAsync(Subject(arguments), String(arguments, "computer")!, Page(arguments), ct),
            "syswitness_get_process_tree" => client.GetProcessTreeAsync(Subject(arguments), String(arguments, "computer")!,
                Integer(arguments, "depth", 3), Integer(arguments, "limit", 100), ct),
            "syswitness_get_connections" => client.GetConnectionsAsync(Page(arguments),
                String(arguments, "processGuid") is null ? null : Subject(arguments), ct),
            "syswitness_get_dns" => client.GetDnsAsync(Page(arguments),
                String(arguments, "processGuid") is null ? null : Subject(arguments), ct),
            "syswitness_get_network_inventory" => client.GetNetworkInventoryAsync(Page(arguments), ct),
            "syswitness_get_storage_projections" => client.GetStorageProjectionsAsync(ct),
            "syswitness_get_file_activity" => client.GetFileActivityAsync(Page(arguments), ct),
            "syswitness_get_registry_activity" => client.GetRegistryActivityAsync(Page(arguments), ct),
            "syswitness_get_authentication_activity" => client.GetAuthenticationActivityAsync(Page(arguments), ct),
            "syswitness_get_service_activity" => client.GetServiceActivityAsync(Page(arguments), ct),
            "syswitness_get_driver_activity" => client.GetDriverActivityAsync(Page(arguments), ct),
            "syswitness_get_task_activity" => client.GetTaskActivityAsync(Page(arguments), ct),
            "syswitness_get_device_activity" => client.GetDeviceActivityAsync(Page(arguments), ct),
            "syswitness_get_module_activity" => client.GetModuleActivityAsync(Page(arguments), ct),
            "syswitness_get_process_access_activity" => client.GetProcessAccessActivityAsync(Page(arguments), ct),
            _ => throw new InvalidOperationException("Tool is not in the fixed catalog.")
        };
}
