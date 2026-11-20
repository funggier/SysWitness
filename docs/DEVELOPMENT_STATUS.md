# SysWitness — Development Status (2026-11-20)

## Current build (M1 observation prototype)

- **Repository:** funggier/SysWitness, public; copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.
- **Local workspace:** `T:\Space\Projects\ProjectsProgram\SysWitness`.
- **Runtime:** Windows 10, .NET 10 SDK, local SQLite.
- **Live collectors:** Windows Event Log **System** and **Application** channels by default, with independent workers. Each initially backfills up to 256 newest events and records `initial_history_outside_backfill_window` if earlier records exist. **Security** and **Sysmon** are optional local configuration and may not be accessible.
- **Subsequent collection:** per-channel native EventBookmarks; events and corresponding checkpoint are committed in **one database transaction**, with read failure/invalid bookmark and log rotation/clear indicators. An unavailable optional channel is marked `degraded` and retried later, without stopping other collectors.
- **API:** `127.0.0.1:8769` by default, single long API token required for non-health requests, no writable/action endpoints; reject unexpected Host header; event summary excludes raw XML.
- **Read methods:** health, aggregate/per-channel monitoring coverage with separate configured/collecting/degraded lists, per-channel gap history, storage status and retention summaries, event search with page cursor and channel/category/provider/sourceEventId/fromUtc/toUtc filters, event-by-ID.
- **Data folder:** `data/` beside the executable unless `SYSWITNESS_DATA_DIRECTORY` is specified; choose a protected writable path. **Retention defaults OFF**; optional `SYSWITNESS_RETENTION_DAYS` enables bounded, audited age-based deletion of local Event copies. A 4096 MiB default **soft** storage budget pauses new writes if the estimated size would exceed it, keeping bookmarks unchanged. See [Storage policy](STORAGE_POLICY_TH.md).
- **Optional client architecture:** Dashboard, read-only MCP, AI and third-party apps remain future adapters to this same API. Protection stays entirely external.

## Verification

On the Windows development machine:
- `dotnet build SysWitness.sln -c Release`: 0 errors, 0 warnings.
- `dotnet run --project tests/SysWitness.SmokeTests -c Release --no-build`: pass for stable IDs, deduplication, atomic rollback, restart persistence, persisted coverage gaps, pagination and bounded filters.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1`: pass against real Windows System and Application Event Logs, independent restarts/checkpoints, optional unavailable Sysmon isolation, HTTP 401/400/404 and absent action endpoints. Testing uses an isolated temporary database and random token.
- `dotnet list SysWitness.sln package --vulnerable --include-transitive`: no listed vulnerable dependency in latest local query. Always re-audit on release.
- GitHub Windows workflows successfully completed for commits `62c3b0f` and `f9a69c3`. Current commit result must be checked separately.

## Important limitations

- No promise of lossless collection: initial coverage is bounded, a provider may drop events, and severe log churn/clear can defeat replay.
- Collection is polling with saved bookmarks, **not** a live ETW subscription; poll period is five seconds, faster when draining pending batches.
- Only System and Application Windows Event Log channels are collected by default. Security and Sysmon event channels are opt-in **raw Event Log reads**, not yet semantic decoders; WFP, registry, file, drivers, devices and full network tracing are not implemented.
- Source-specific permissions, detailed telemetry loss diagnostics, **strict** quota/reclamation, free-disk guard and separation of elevated collector from unprivileged API remain. Basic opt-in retention and approximate soft-budget suspension are implemented.
- Single shared token; fine-grained per-client read scopes, rotation, read audit and optional MCP connector remain future features.
- Data files are sensitive; filesystem permissions must protect the executable folder or configured data directory.

## Next engineering priorities

1. Atomic source checkpoint regression tests with generated multi-batch fixtures and independent read/replay.
2. **Completed:** multi-channel Windows Event Log framework with independent bookmarks, coverage status and unavailable-source isolation, without changing Windows Audit Policy remotely.
3. **Partially completed:** opt-in retention with deletion history, read-only storage status and soft-budget write pause; still needs rigorous disk-free guard, file compaction policy and loss diagnostics.
4. Per-client read-only permission scope and evidence export policy.
5. MCP adapter and dashboard **only after** the data service is stable.

The permanent restriction remains: **NO blocking, process termination, quarantine, remote shell, protection control, or Windows-configuration API**.
