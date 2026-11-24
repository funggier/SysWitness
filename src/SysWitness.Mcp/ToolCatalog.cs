namespace SysWitness.Mcp;

internal sealed record ToolField(string Type, int? Minimum = null, int? Maximum = null,
    int? MaxLength = null, bool Required = false, string? Kind = null);

internal sealed record ToolDefinition(string Name, string Description, IReadOnlyDictionary<string, ToolField> Fields)
{
    public object Describe() => new
    {
        name = Name,
        description = Description,
        inputSchema = new
        {
            type = "object",
            properties = Fields.ToDictionary(x => x.Key, x =>
            {
                var value = new Dictionary<string, object> { ["type"] = x.Value.Type };
                if (x.Value.Minimum is { } minimum) value["minimum"] = minimum;
                if (x.Value.Maximum is { } maximum) value["maximum"] = maximum;
                if (x.Value.MaxLength is { } length) { value["minLength"] = 1; value["maxLength"] = length; }
                if (x.Value.Kind == "guid") value["pattern"] = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
                if (x.Value.Kind == "eventId") value["pattern"] = "^[0-9a-fA-F]{64}$";
                if (x.Value.Kind == "time") value["format"] = "date-time";
                return value;
            }),
            required = Fields.Where(x => x.Value.Required).Select(x => x.Key).ToArray(),
            additionalProperties = false
        },
        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
    };
}

internal static class ToolCatalog
{
    public static readonly IReadOnlyDictionary<string, ToolDefinition> Tools = Build();

    private static IReadOnlyDictionary<string, ToolDefinition> Build()
    {
        var result = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
        void Add(string suffix, string description, Dictionary<string, ToolField>? fields = null)
        {
            var name = "syswitness_get_" + suffix;
            result.Add(name, new(name, description, fields ?? []));
        }
        static Dictionary<string, ToolField> Page(bool category = false, bool network = false)
        {
            var fields = new Dictionary<string, ToolField>
            {
                ["limit"] = new("integer", 1, 500),
                ["cursor"] = new("string", MaxLength: 4096),
                ["channel"] = new("string", MaxLength: 256),
                ["provider"] = new("string", MaxLength: 256),
                ["sourceEventId"] = new("integer", 0, 65535),
                ["fromUtc"] = new("string", MaxLength: 64, Kind: "time"),
                ["toUtc"] = new("string", MaxLength: 64, Kind: "time")
            };
            if (category) fields.Add("category", new("string", MaxLength: 128));
            if (network) fields.Add("processGuid", new("string", MaxLength: 36, Kind: "guid"));
            return fields;
        }
        static Dictionary<string, ToolField> Subject(bool activity)
        {
            var fields = Page();
            fields.Add("processGuid", new("string", MaxLength: 36, Required: true, Kind: "guid"));
            if (activity) fields.Add("computer", new("string", MaxLength: 256, Required: true));
            return fields;
        }
        Add("health", "Read API availability. Does not inspect or change Windows.");
        Add("events", "Read retained event summaries. Preserve cursor, filters and coverage context; an empty page does not prove no activity.", Page(category: true));
        Add("event", "Read one retained event summary by evidence ID; raw XML is not exposed.",
            new() { ["eventId"] = new("string", MaxLength: 64, Required: true, Kind: "eventId") });
        Add("coverage", "Read source coverage and gaps in collection; this does not enable collection.",
            new() { ["channel"] = new("string", MaxLength: 256) });
        Add("gaps", "Read bounded recorded collection gaps.",
            new() { ["channel"] = new("string", MaxLength: 256), ["limit"] = new("integer", 1, 100) });
        Add("storage_status", "Read point-in-time storage capacity, retention and ingestion status; cannot change policy.");
        Add("retention", "Read prior local retention deletion summaries; cannot delete evidence.",
            new() { ["limit"] = new("integer", 1, 100) });
        Add("process_events", "Read retained process observations. Requires processes.read; command lines can contain sensitive data.", Page());
        Add("process_timeline", "Read Sysmon subject Process GUID timeline; PID-only observations cannot be correlated by this route.", Subject(activity: false));
        Add("process_activity", "Read derived Sysmon activity for an exact recorded computer and GUID. Requires processes.read AND network.read. Preserve limitations.", Subject(activity: true));
        Add("process_tree", "Read bounded derived Sysmon parent/child view for an exact recorded computer. Reference-only nodes and truncation do not establish completeness.",
            new() { ["processGuid"] = new("string", MaxLength: 36, Required: true, Kind: "guid"),
                ["computer"] = new("string", MaxLength: 256, Required: true),
                ["depth"] = new("integer", 0, 8), ["limit"] = new("integer", 1, 256) });
        Add("connections", "Read retained network connection observations and audit decisions; does not inspect live sockets or block traffic.", Page(network: true));
        Add("dns", "Read retained Sysmon DNS observations; does not perform DNS lookups.", Page(network: true));
        Add("network_inventory", "Read retained local interface, address, route, neighbor and socket inventory observations. Snapshot presence is not connection establishment evidence.", Page());
        Add("storage_projections", "Read global projection readiness and counts. Does not rebuild or modify projections.");
        Add("file_activity", "Read retained file activity evidence; no file content or arbitrary file reads. Preserve source limitations.", Page());
        Add("registry_activity", "Read retained registry activity evidence; does not read live registry or modify keys.", Page());
        Add("authentication_activity", "Read retained authentication evidence; source fields are observations, not a verdict.", Page());
        Add("service_activity", "Read retained service activity evidence; cannot start, stop or configure services.", Page());
        Add("driver_activity", "Read retained driver activity evidence; cannot load or unload drivers.", Page());
        Add("task_activity", "Read retained scheduled-task observations; cannot list live tasks, execute or change them.", Page());
        Add("device_activity", "Read retained device activity evidence; cannot control devices.", Page());
        Add("module_activity", "Read retained module activity evidence; cannot load, inject or unload modules.", Page());
        Add("process_access_activity", "Read retained process-access evidence. A reported process-open is not proof of memory access.", Page());
        return result;
    }
}
