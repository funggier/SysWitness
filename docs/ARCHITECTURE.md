# SysWitness Architecture

## Modules
- **SysWitness.Contracts**: versioned DTOs, event categories, compatibility, provenance
- **SysWitness.Collectors.Windows**: event source adapters; privileges only when needed. The current WindowsEventLogReader is in Core; it is registered as an independent background service per configured channel.
- **SysWitness.Core**: ingestion, normalization, source health, correlation boundaries
- **SysWitness.Storage**: append-only evidence, normalized views, indexes, retention
- **SysWitness.Api**: bounded read/query/stream endpoints; never execute host actions
- **SysWitness.Dashboard** *(optional)*: API client, no database bypass
- **SysWitness.Mcp** *(optional)*: maps read-only MCP tools to API; no OS actions
- **External protection applications** *(out of repository)*: independent authorization/control planes

## Trust boundaries
1. Windows telemetry (untrusted content) to collector.
2. Collector (trusted to read source) to store; persist raw/native details separately from interpreted data.
3. API (least privilege service) to authenticated clients.
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

## Configuration boundary
Only a local, separately authorized setup/admin interface may install providers, configure Windows audits, or modify the collector. No such actions exist in public data API, MCP toolset, or AI interface.
