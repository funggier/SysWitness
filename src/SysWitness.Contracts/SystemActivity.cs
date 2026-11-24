namespace SysWitness.Contracts;

/// <summary>Bounded projection of retained evidence. This is not a claim of complete host visibility.</summary>
public sealed record SystemActivityPage(
    string Kind, IReadOnlyList<SystemActivityEvent> Items, int Limit, int Returned,
    string? NextCursor, IReadOnlyList<string> Limitations);

/// <summary>Exactly one family payload is populated. EventId references retained source evidence; no raw XML is serialized.</summary>
public sealed record SystemActivityEvent(
    string EventId, DateTimeOffset OccurredAtUtc, DateTimeOffset ObservedAtUtc,
    string Kind, SystemActivitySource Source, string PayloadStatus,
    SystemActivityProcess? Actor = null, SystemActivityAccount? Subject = null,
    FileActivity? File = null, RegistryActivity? Registry = null,
    AuthenticationActivity? Authentication = null, ServiceActivity? Service = null,
    DriverActivity? Driver = null, TaskActivity? Task = null, DeviceActivity? Device = null,
    ModuleActivity? Module = null, ProcessAccessActivity? ProcessAccess = null,
    string ObservationMethod = "eventLog", string Quality = "observed");

public sealed record SystemActivitySource(
    string Channel, string Provider, int SourceEventId, long? SourceRecordId, string? Computer);

/// <summary>pidOnly is a local source identifier, not a stable process instance correlation.</summary>
public sealed record SystemActivityProcess(
    uint? ProcessId, Guid? ProcessGuid, string? ImagePath, string IdentityQuality);

/// <summary>Source identity fields are preserved without guessing a SID or splitting account names.</summary>
public sealed record SystemActivityAccount(string? Sid, string? Name, string? Domain, string? LogonId);

public sealed record FileActivity(
    string Operation, string? Path, string? DevicePath = null,
    DateTimeOffset? CreationUtcTime = null, DateTimeOffset? PreviousCreationUtcTime = null,
    ulong? AccessMask = null, string? AccessList = null, string? ObjectType = null,
    string? HandleId = null, string? Hashes = null);

public sealed record RegistryActivity(
    string Operation, string? ObjectPath, string? ValueName = null, string? NewName = null,
    string? Details = null, string? OldValue = null, string? NewValue = null,
    string? OldValueType = null, string? NewValueType = null,
    ulong? AccessMask = null, string? AccessList = null, string? HandleId = null,
    string? SourceOperation = null);

public sealed record AuthenticationActivity(
    string Operation, SystemActivityAccount? Target = null, uint? LogonType = null,
    string? Status = null, string? SubStatus = null, string? FailureReason = null,
    string? SourceAddress = null, ushort? SourcePort = null, string? WorkstationName = null,
    string? AuthenticationPackage = null, string? TargetServer = null, string? PrivilegeList = null);

/// <summary>The service family includes driver services. Type/start strings retain source representation.</summary>
public sealed record ServiceActivity(
    string Operation, string? Name, string? ImagePath = null, string? ServiceType = null,
    string? StartType = null, string? Account = null);

public sealed record DriverActivity(
    string Operation, string? ImagePath, string? Hashes = null, bool? Signed = null,
    string? Signature = null, string? SignatureStatus = null, string? Name = null,
    string? ServiceType = null, string? StartType = null);

/// <summary>DefinitionAvailable reports presence only. Task definition XML remains retained evidence, not API data.</summary>
public sealed record TaskActivity(
    string Operation, string? Name, bool DefinitionAvailable, string? ClientProcessStartKey = null,
    uint? ParentProcessId = null, uint? RpcCallClientLocality = null, string? Fqdn = null);

public sealed record DeviceActivity(
    string Operation, string? InstanceId, string? Description = null, string? ClassId = null,
    string? ClassName = null, string? HardwareIds = null, string? CompatibleIds = null,
    string? Location = null);

public sealed record ModuleActivity(
    string Operation, string? ImagePath, string? Hashes = null, bool? Signed = null,
    string? Signature = null, string? SignatureStatus = null);

/// <summary>A process-open observation is not proof that memory was read/written; thread start names may be inferred by Sysmon.</summary>
public sealed record ProcessAccessActivity(
    string Operation, SystemActivityProcess? Target = null, ulong? GrantedAccess = null,
    uint? SourceThreadId = null, string? CallTrace = null, uint? NewThreadId = null,
    string? StartAddress = null, string? StartModule = null, string? StartFunction = null);
