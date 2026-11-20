# SysWitness — Development Status (2026-11-20)

## Current build (M1 observation prototype)

- **Repository:** funggier/SysWitness, public; copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.
- **Local workspace:** `T:\Space\Projects\ProjectsProgram\SysWitness`.
- **Runtime:** Windows 10, .NET 10 SDK, local SQLite.
- **Live collector:** Windows Event Log **System** channel only. The first run backfills up to 256 newest records, reporting `initial_history_outside_backfill_window` when earlier records remain outside the backfill.
- **Subsequent collection:** native EventBookmark; events and checkpoint are committed in **one database transaction**, with read failure/invalid bookmark and log rotation/clear indicators.
- **API:** `127.0.0.1:8769` by default, single long API token required for non-health requests, no writable/action endpoints; reject unexpected Host header; event summary excludes raw XML.
- **Read methods:** health, monitoring coverage/gaps, event search with page cursor and channel/category/provider/sourceEventId/fromUtc/toUtc filters, event-by-ID.
- **Data folder:** `data/` beside the executable unless `SYSWITNESS_DATA_DIRECTORY` is specified; choose a protected writable path.
- **Optional client architecture:** Dashboard, read-only MCP, AI and third-party apps remain future adapters to this same API. Protection stays entirely external.

## Verification

On the Windows development machine:
- `dotnet build SysWitness.sln -c Release`: 0 errors, 0 warnings.
- `dotnet run --project tests/SysWitness.SmokeTests -c Release --no-build`: pass for stable IDs, deduplication, atomic rollback, restart persistence, persisted coverage gaps, pagination and bounded filters.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1`: pass against real Windows System Event Log, restart, HTTP 401/400 and absent action endpoints. Testing uses an isolated temporary database and random token.
- `dotnet list SysWitness.sln package --vulnerable --include-transitive`: no listed vulnerable dependency in latest local query. Always re-audit on release.
- GitHub Windows workflow runs build and fixture smoke tests; its first remote result is **not** asserted here.

## Important limitations

- No promise of lossless collection: initial coverage is bounded, a provider may drop events, and severe log churn/clear can defeat replay.
- Collection is polling with saved bookmarks, **not** a live ETW subscription; poll period is five seconds, faster when draining pending batches.
- Only Windows System is collected. Security, Application, Sysmon, WFP, registry, file, drivers, devices and full network tracing are not yet implemented.
- Source-specific permissions, detailed telemetry loss diagnostics, disk retention/quota, and separation of elevated collector from unprivileged API remain.
- Single shared token; fine-grained per-client read scopes, rotation, read audit and optional MCP connector remain future features.
- Data files are sensitive; filesystem permissions must protect the executable folder or configured data directory.

## Next engineering priorities

1. Atomic source checkpoint regression tests with generated multi-batch fixtures and independent read/replay.
2. Multi-source Windows Event Log collector framework (separate channel bookmarks and coverage status), without changing Windows Audit Policy remotely.
3. Retention and disk-budget/pressure handling, truthful gap and collector-health diagnostics.
4. Per-client read-only permission scope and evidence export policy.
5. MCP adapter and dashboard **only after** the data service is stable.

The permanent restriction remains: **NO blocking, process termination, quarantine, remote shell, protection control, or Windows-configuration API**.
