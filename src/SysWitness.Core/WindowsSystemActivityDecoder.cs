using System.Globalization;
using SysWitness.Contracts;
using static SysWitness.Core.WindowsProcessEventDecoder;

namespace SysWitness.Core;

/// <summary>A fixed source rule, shared by SQL selection and projection. Payload predicates are closed, internal names.</summary>
public sealed record SystemActivitySourceRule(
    string Channel, string Provider, IReadOnlyList<int> EventIds, string? PayloadPredicate = null);

/// <summary>
/// Source-aware projections of existing records. Never attributes the System/Execution PID,
/// infers actor identity from paths, or interprets an access mask as proof of data transfer.
/// </summary>
public static class WindowsSystemActivityDecoder
{
    public const string ScmProvider = "Service Control Manager";
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[]
    {
        "files", "registry", "authentication", "services", "drivers",
        "tasks", "devices", "modules", "process-access"
    });

    private static SystemActivitySourceRule S(params int[] ids) => new(SysmonChannel, SysmonProvider, Array.AsReadOnly(ids));
    private static SystemActivitySourceRule A(int[] ids, string? predicate = null) =>
        new(SecurityChannel, SecurityProvider, Array.AsReadOnly(ids), predicate);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SystemActivitySourceRule>> Rules =
        new Dictionary<string, IReadOnlyList<SystemActivitySourceRule>>(StringComparer.Ordinal)
        {
            ["files"] = Array.AsReadOnly(new[] { S(2, 9, 11, 15, 23, 26, 27, 28, 29),
                A([4656, 4663, 4670], "fileObject") }),
            ["registry"] = Array.AsReadOnly(new[] { S(12, 13, 14), A([4657]),
                A([4656, 4663, 4670], "registryObject") }),
            ["authentication"] = Array.AsReadOnly(new[] { A([4624, 4625, 4634, 4647, 4648, 4672, 4776]) }),
            ["services"] = Array.AsReadOnly(new[] { A([4697]),
                new SystemActivitySourceRule("System", ScmProvider, Array.AsReadOnly(new[] { 7045 })) }),
            ["drivers"] = Array.AsReadOnly(new[] { S(6), A([4697], "driverService") }),
            ["tasks"] = Array.AsReadOnly(new[] { A([4698, 4699, 4700, 4701, 4702]) }),
            ["devices"] = Array.AsReadOnly(new[] { A([6416, 6419, 6420, 6421, 6422, 6423, 6424]) }),
            ["modules"] = Array.AsReadOnly(new[] { S(7) }),
            ["process-access"] = Array.AsReadOnly(new[] { S(8, 10) })
        };

    public static IReadOnlyList<SystemActivitySourceRule> GetSourceRules(string kind) =>
        kind is not null && Rules.TryGetValue(kind, out var rules) ? rules :
            throw new ArgumentException("Unknown system activity kind.", nameof(kind));

    /// <summary>Used as a deterministic SQL predicate before pagination, never as user-supplied code.</summary>
    public static bool MatchesPayload(string xml, string predicate)
    {
        var fields = WindowsEventXml.ReadFields(xml);
        return predicate switch
        {
            "fileObject" => Field(fields, "ObjectType") == "File",
            "registryObject" => Field(fields, "ObjectType") == "Key",
            "driverService" => Number(Field(fields, "ServiceType")) is 1 or 2 or 8,
            _ => false
        };
    }

    public static IReadOnlyList<string> GetLimitations(string kind)
    {
        _ = GetSourceRules(kind);
        return Array.AsReadOnly(new[]
        {
            "Only retained records from the listed channel, provider and source IDs are projected; no event is evidence of complete coverage.",
            "Collection permissions, existing audit/Sysmon configuration, retention and source gaps limit visibility. This endpoint changes no policy.",
            "Missing, duplicate, invalid, nested or oversized payload values remain null. The source event ID still supplies the operation family.",
            "PIDs and logon IDs are local source identifiers; they are not globally stable process or session correlation keys.",
            kind switch
            {
                "files" => "File creates/overwrites, selected deletion and audit events are not every file read/write. ReadData can mean ListDirectory; DELETE access can occur during rename. Untyped 4660 deletion evidence is not classified without correlation.",
                "registry" => "Registry notification/read coverage is not implied. Sysmon TargetObject can include a value name; it is not split into a guessed key and value. Audits require existing policy and SACLs.",
                "authentication" => "Reporting subject, target account and caller process have separate roles. 4648 is an attempt; 4647 is initiation. NTLM 4776 is emitted on the authoritative host and lacks destination/actor process.",
                "services" => "Installation events include driver services and do not show every later configuration/state change. No installer PID is inferred from provider execution.",
                "drivers" => "A driver loaded event and a driver-service installation are different observations. Neither identifies the process that caused a driver load.",
                "tasks" => "Task definition XML remains raw retained evidence and is excluded from this DTO. Optional client PID/locality fields depend on source event version and are not inferred.",
                "devices" => "Device recognition and enable/disable requests are source observations, not a complete device attachment history or proof of who physically attached a device.",
                "modules" => "Sysmon image-load coverage depends on existing configuration and is disabled by default. Signature fields reflect source-provided results.",
                _ => "Opening a process is not proof of memory read/write. Remote-thread StartModule/StartFunction are inferred by Sysmon and may be absent."
            }
        });
    }

    public static SystemActivityEvent? Decode(ObservedEvent evidence, string kind)
    {
        var matches = GetSourceRules(kind).Any(rule =>
            evidence.Channel == rule.Channel && evidence.Provider == rule.Provider &&
            rule.EventIds.Contains(evidence.SourceEventId) &&
            (rule.PayloadPredicate is null || MatchesPayload(evidence.RawXml, rule.PayloadPredicate)));
        if (!matches) return null;

        var fields = WindowsEventXml.ReadFields(evidence.RawXml);
        string? F(string key) => Field(fields, key);
        var id = evidence.SourceEventId;
        var sysmon = evidence.Channel == SysmonChannel && evidence.Provider == SysmonProvider;
        var source = new SystemActivitySource(evidence.Channel, evidence.Provider, id,
            evidence.SourceRecordId, evidence.Computer);
        var actor = sysmon && id != 6 ? ReadProcess(fields, id is 8 or 10 ? "Source" : "") : null;
        SystemActivityAccount? subject = sysmon
            ? ReadAccount(null, F(id is 8 or 10 ? "SourceUser" : "User"), null, null)
            : ReadAccount(F("SubjectUserSid"), F("SubjectUserName"), F("SubjectDomainName"), F("SubjectLogonId"));
        if (!sysmon && kind is "files" or "registry")
            actor = ReadProcess(fields, security: true);
        if (kind == "authentication" && id is 4624 or 4625 or 4648)
            actor = ReadProcess(fields, security: true);
        if (kind == "tasks")
            actor = Process(Pid(F("ClientProcessId")), null, null);
        var result = new SystemActivityEvent(evidence.EventId, evidence.OccurredAtUtc,
            evidence.ObservedAtUtc, kind, source, fields is null ? "unavailable" : "decoded", actor, subject);

        switch (kind)
        {
            case "files":
                return result with { File = new FileActivity(
                    sysmon ? id switch
                    {
                        2 => "file.creationTime.changed", 9 => "device.rawRead",
                        11 => "file.createdOrOverwritten", 15 => "file.stream.created",
                        23 or 26 => "file.deleted", 27 => "file.executable.creation.blocked",
                        28 => "file.shredding.blocked", _ => "file.executable.created"
                    } : AuditOperation(id),
                    F(sysmon ? "TargetFilename" : "ObjectName"), F("Device"),
                    Utc(F("CreationUtcTime")), Utc(F("PreviousCreationUtcTime")),
                    Number(F("AccessMask")), F("AccessList"), F("ObjectType"), F("HandleId"),
                    F(id == 15 ? "Hash" : "Hashes")) };
            case "registry":
                return result with { Registry = new RegistryActivity(
                    sysmon ? id switch { 12 => "registry.object.createdOrDeleted",
                        13 => "registry.value.set", _ => "registry.object.renamed" }
                        : id == 4657 ? "registry.value.modified" : AuditOperation(id),
                    F(sysmon ? "TargetObject" : "ObjectName"), F("ObjectValueName"), F("NewName"),
                    F("Details"), F("OldValue"), F("NewValue"), F("OldValueType"), F("NewValueType"),
                    Number(F("AccessMask")), F("AccessList"), F("HandleId"),
                    F(sysmon ? "EventType" : "OperationType")) };
            case "authentication":
                return result with { Authentication = new AuthenticationActivity(
                    id switch { 4624 => "logon.succeeded", 4625 => "logon.failed",
                        4634 => "logon.session.ended", 4647 => "logoff.initiated",
                        4648 => "credentials.explicit.attempted", 4672 => "logon.privileges.assigned",
                        _ => "credentials.ntlm.validation" },
                    ReadAccount(F("TargetUserSid"), F("TargetUserName"), F("TargetDomainName"), F("TargetLogonId")),
                    Pid(F("LogonType")), F("Status"), F("SubStatus"), F("FailureReason"),
                    F("IpAddress"), Port(F("IpPort")), F(id == 4776 ? "Workstation" : "WorkstationName"),
                    F(id == 4776 ? "PackageName" : "AuthenticationPackageName"),
                    F("TargetServerName"), F("PrivilegeList")) };
            case "services":
                var scm = evidence.Provider == ScmProvider;
                return result with { Service = new ServiceActivity("service.installed", F("ServiceName"),
                    F(scm ? "ImagePath" : "ServiceFileName"), F("ServiceType"),
                    F(scm ? "StartType" : "ServiceStartType"), F(scm ? "AccountName" : "ServiceAccount")) };
            case "drivers":
                return result with { Actor = null, Driver = new DriverActivity(
                    sysmon ? "driver.loaded" : "driver.service.installed",
                    F(sysmon ? "ImageLoaded" : "ServiceFileName"), F("Hashes"), Boolean(F("Signed")),
                    F("Signature"), F("SignatureStatus"), F("ServiceName"), F("ServiceType"), F("ServiceStartType")) };
            case "tasks":
                // Presence is checked independently of public string bounds; XML itself never enters the DTO.
                var definitionKey = id == 4702 ? "TaskContentNew" : "TaskContent";
                var definitionAvailable = fields?.GetValueOrDefault(definitionKey) is { Length: > 0 };
                return result with { Task = new TaskActivity(
                    id switch { 4698 => "task.created", 4699 => "task.deleted", 4700 => "task.enabled",
                        4701 => "task.disabled", _ => "task.updated" }, F("TaskName"),
                    definitionAvailable, F("ClientProcessStartKey"), Pid(F("ParentProcessId")),
                    Pid(F("RpcCallClientLocality")), F("FQDN")) };
            case "devices":
                return result with { Actor = null, Device = new DeviceActivity(
                    id switch { 6416 => "device.recognized", 6419 => "device.disable.requested",
                        6420 => "device.disabled", 6421 => "device.enable.requested",
                        6422 => "device.enabled", 6423 => "device.installation.forbidden",
                        _ => "device.installation.allowedAfterProhibition" },
                    F("DeviceId"), F("DeviceDescription"), F("ClassId"), F("ClassName"),
                    F("VendorIds"), F("CompatibleIds"), F("LocationInformation")) };
            case "modules":
                return result with { Module = new ModuleActivity("module.loaded", F("ImageLoaded"),
                    F("Hashes"), Boolean(F("Signed")), F("Signature"), F("SignatureStatus")) };
            case "process-access":
                return result with { ProcessAccess = new ProcessAccessActivity(
                    id == 10 ? "process.opened" : "process.remoteThread.created",
                    ReadProcess(fields, "Target"), Number(F("GrantedAccess")), Pid(F("SourceThreadId")),
                    F("CallTrace"), Pid(F("NewThreadId")), F("StartAddress"), F("StartModule"), F("StartFunction")) };
            default:
                return null;
        }
    }

    private static string AuditOperation(int id) => id switch
    {
        4656 => "object.access.requested", 4663 => "object.access.used", _ => "object.permissions.changed"
    };
    private static string? Field(Dictionary<string, string?>? fields, string key) =>
        fields?.GetValueOrDefault(key) is { Length: <= 8192 } text ? text : null;
    private static string? Alias(Dictionary<string, string?>? fields, string first, string second)
    {
        // Two spellings in one payload are ambiguous even if either could parse.
        if (fields?.ContainsKey(first) == true && fields.ContainsKey(second)) return null;
        return Field(fields, first) ?? Field(fields, second);
    }
    private static SystemActivityProcess? ReadProcess(Dictionary<string, string?>? fields,
        string prefix = "", bool security = false)
    {
        var pid = Pid(Field(fields, prefix + "ProcessId"));
        var guid = security ? null : WindowsEventXml.ParseGuid(Alias(fields, prefix + "ProcessGuid", prefix + "ProcessGUID"));
        var image = Field(fields, security ? "ProcessName" : prefix + "Image");
        return Process(pid, guid, image);
    }
    private static SystemActivityProcess? Process(uint? pid, Guid? guid, string? image) =>
        pid is null && guid is null && image is null ? null :
            new(pid, guid, image, guid is not null ? "processGuid" : pid is not null ? "pidOnly" : "unavailable");
    private static SystemActivityAccount? ReadAccount(string? sid, string? name, string? domain, string? logon) =>
        sid is null && name is null && domain is null && logon is null ? null : new(sid, name, domain, logon);
    private static ulong? Number(string? text)
    {
        if (text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
            return ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
    private static uint? Pid(string? text) => Number(text) is { } n && n <= uint.MaxValue ? (uint)n : null;
    private static ushort? Port(string? text) => Number(text) is { } n && n <= ushort.MaxValue ? (ushort)n : null;
    private static bool? Boolean(string? text) => text switch { "true" => true, "false" => false, _ => null };
    private static DateTimeOffset? Utc(string? text) =>
        text is not null && (text.Contains('T') || text.Contains(' ')) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
}
