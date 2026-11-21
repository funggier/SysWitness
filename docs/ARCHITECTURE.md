# SysWitness Architecture

## Modules
- **SysWitness.Contracts**: versioned DTOs, event categories, compatibility, provenance
- **SysWitness.Collectors.Windows**: event source adapters; privileges only when needed. The current WindowsEventLogReader is in Core; it is registered as an independent background service per configured channel.
- **SysWitness.Core**: ingestion, normalization, source health, correlation boundaries
- **SysWitness.Storage**: append-only evidence, normalized views, indexes, retention, soft-limit checks and on-volume free-disk guard
- **SysWitness.Security**: standalone .NET library for locally configured client-key hash registry and explicit read scopes
- **SysWitness.Api**: bounded read/query endpoints and HTTP authorization boundary; never execute host actions
- **SysWitness.Dashboard** *(optional)*: API client, no database bypass
- **SysWitness.Mcp** *(optional)*: maps read-only MCP tools to API; no OS actions
- **External protection applications** *(out of repository)*: independent authorization/control planes

## Trust boundaries
1. Windows telemetry (untrusted content) to collector.
2. Collector (trusted to read source) to store; persist raw/native details separately from interpreted data.
3. API (least privilege service) authenticates using the isolated SysWitness.Security library. Scope checks are explicit for each known GET endpoint; unrecognized routes fail closed.
4. MCP/AI: treat all log content as untrusted task data, not model instructions.
5. Future protection product: separately installed/runtime permissions; no callback command channel into SysWitness.

## Runtime processes
Initial MVP hosts independently configured System/Application Event Log collectors (with opt-in Security/Sysmon), SQLite database and HTTP API in a single *unprivileged* local process; the database defaults to `data/` beside the executable (writable, protected folder required), or to a path set by `SYSWITNESS_DATA_DIRECTORY`. For elevated sources, split collector into an isolated, least-privilege process and use authenticated local IPC. **Do not run a network-facing web API as administrator by default.**

## Deep monitoring caveats
- Event source availability, audit policy, and provider versions determine actual observability.
- ETW/WFP/USN deliver different semantics; they must not be conflated.
- Correlation needs process GUID or PID + start time and source-aware constraints.
- Timestamps are UTC and origin times can be out-of-order; preserve received time separately.
- Failed-to-collect, invalid event, data loss, retention deletion, and unsupported states are reportable coverage gaps.

## Multiple Windows Event Log channels

- `SYSWITNESS_EVENT_CHANNELS` is a local-only comma-separated, case-insensitive allowlist. Defaults to System and Application; Security and Microsoft-Windows-Sysmon/Operational are opt-in.
- Each channel owns an independent reader loop, native bookmark and SQLite checkpoint, durable gap history and synchronized coverage status.
- All collectors share the same serialized SQLite write path; an unavailable channel is retried after 30 seconds and does not block others.
- A degraded optional source does not make successful collection from System/Application invalid, but the aggregate health response correctly reports `degraded`.
- The API has no channel-enabling or OS audit setup endpoints.

## Local storage maintenance boundary

The optional `StorageMaintenanceService` runs only when `SYSWITNESS_RETENTION_DAYS` is configured locally. It deletes old normalized/raw evidence copies in bounded SQLite batches and records aggregate deletion history in the same transaction. The default is no automatic deletion. Storage is governed by a conservative approximate soft limit **and** minimum available free bytes on the database volume. If a batch would exceed the estimated budget, leave less than the configured disk reserve, or if free space cannot be checked, collectors pause and **do not** advance native bookmarks. An HTTP client, MCP or AI cannot change these rules or invoke deletion. The API offers only read-only `/storage/status` and `/storage/retention` endpoints. Full disk-quota enforcement and compaction remain out of scope for this step.

## Local read-client security boundary

`SysWitness.Security` independently validates a locally provided JSON list of client IDs, SHA-256 token hashes, and `events.read`/`coverage.read`/`storage.read` scopes. Neither raw Client tokens nor permission updates are accepted through the Data API. The legacy global Token remains as an optional all-read-scopes credential for backward compatibility. The registry is loaded once at startup; changes require restart. See [API_CLIENTS_TH.md](API_CLIENTS_TH.md).

## Portable and relocatable application layout

Windows x64 release builds use self-contained .NET publish (not trimmed or single-file) so that runtime DLLs and native SQLite dependencies reside alongside the app with no extraction cache outside the folder. The `Start-SysWitness.cmd` / `.ps1` bootstrap resolves its application root from the script path, not the process working directory. Default settings, client API key hashes, owner token and database are stored in local `config/` and `data/` directories. No hardcoded drive, registry key, Windows Service or installer is required. The first-run secret exists only after unpacking and is **excluded from public ZIPs**. Local settings require relative data paths inside the portable root; moving the entire folder preserves the database and bookmarks. See [portable guide](../portable/README-PORTABLE-TH.md).

## Configuration boundary
Only a local, separately authorized setup/admin interface may install providers, configure Windows audits, or modify the collector. No such actions exist in public data API, MCP toolset, or AI interface.
