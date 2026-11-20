# SysWitness Architecture

## Modules
- **SysWitness.Contracts**: versioned DTOs, event categories, compatibility, provenance
- **SysWitness.Collectors.Windows**: event source adapters; privileges only when needed
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
Initial MVP hosts the System Event Log collector, SQLite database and HTTP API in a single *unprivileged* local process; the database defaults to `data/` beside the executable (writable, protected folder required), or to a path set by `SYSWITNESS_DATA_DIRECTORY`. For elevated sources, split collector into an isolated, least-privilege process and use authenticated local IPC. **Do not run a network-facing web API as administrator by default.**

## Deep monitoring caveats
- Event source availability, audit policy, and provider versions determine actual observability.
- ETW/WFP/USN deliver different semantics; they must not be conflated.
- Correlation needs process GUID or PID + start time and source-aware constraints.
- Timestamps are UTC and origin times can be out-of-order; preserve received time separately.
- Failed-to-collect, invalid event, data loss, retention deletion, and unsupported states are reportable coverage gaps.

## Configuration boundary
Only a local, separately authorized setup/admin interface may install providers, configure Windows audits, or modify the collector. No such actions exist in public data API, MCP toolset, or AI interface.
