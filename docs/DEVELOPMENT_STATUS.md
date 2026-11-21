# SysWitness — Development Status (2026-11-21)

## Current build (M1 observation prototype)

- **Repository:** funggier/SysWitness, public; copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.
- **Local workspace:** `T:\Space\Projects\ProjectsProgram\SysWitness`.
- **Runtime:** Windows 10, .NET 10 SDK, local SQLite.
- **Live collectors:** Windows Event Log **System** and **Application** channels by default, with independent workers. Each initially backfills up to 256 newest events and records `initial_history_outside_backfill_window` if earlier records exist. **Security** and **Sysmon** are optional local configuration and may not be accessible.
- **Subsequent collection:** per-channel native EventBookmarks; events and corresponding checkpoint are committed in **one database transaction**, with read failure/invalid bookmark and log rotation/clear indicators. An unavailable optional channel is marked `degraded` and retried later, without stopping other collectors.
- **API:** `127.0.0.1:8769` by default; either legacy `SYSWITNESS_API_TOKEN` or local `SYSWITNESS_CLIENT_KEYS_FILE` authenticates clients. Per-client `events.read`/`coverage.read`/`storage.read` scopes are enforced with HTTP 401/403 on unauthorized access. No writable/action endpoints; rejects unexpected Host header; event summary excludes raw XML.
- **Read methods:** health, aggregate/per-channel monitoring coverage with separate configured/collecting/degraded lists, per-channel gap history, storage status and retention summaries, event search with page cursor and channel/category/provider/sourceEventId/fromUtc/toUtc filters, event-by-ID.
- **Data folder:** `data/` beside the executable unless `SYSWITNESS_DATA_DIRECTORY` is specified; choose a protected writable path. **Retention defaults OFF**; optional `SYSWITNESS_RETENTION_DAYS` enables bounded, audited age-based deletion of local Event copies. A 4096 MiB default **soft** storage budget and a separate 1024 MiB **minimum free-disk reserve** (`SYSWITNESS_MIN_FREE_DISK_MB`) pause new writes if the respective threshold would be exceeded, or if available disk space cannot be inspected, keeping native bookmarks unchanged. See [Storage policy](STORAGE_POLICY_TH.md).
- **Optional client architecture:** Dashboard, read-only MCP, AI and third-party apps remain future adapters to this same API. Protection stays entirely external.

## Verification

On the Windows development machine:
- `dotnet build SysWitness.sln -c Release`: 0 errors, 0 warnings.
- `dotnet run --project tests/SysWitness.SmokeTests -c Release --no-build`: pass for stable IDs, deduplication, atomic rollback, restart persistence, persisted gaps, bounded filters, disk-space guards and read-only client-key scope/config validation.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1`: pass against real Windows System/Application Event Logs, independent restarts/checkpoints, optional unavailable Sysmon, volume free-space API, isolated client scopes with HTTP 200/401/403/404 and absent action endpoints. Testing uses an isolated temporary database and random token.
- `dotnet list SysWitness.sln package --vulnerable --include-transitive`: no listed vulnerable dependency in latest local query. Always re-audit on release.
- GitHub Windows workflows successfully completed for commits `62c3b0f` and `f9a69c3`. Current commit result must be checked separately.

## Portable Windows x64 packaging (new)

- `scripts/Build-Portable.ps1` produces `artifacts/SysWitness-0.1.0-preview.1-win-x64-portable.zip` using .NET 10 **self-contained**, untrimmed win-x64 publish. No .NET install is required on the target machine.
- Open `Start-SysWitness.cmd` from the extracted folder; first run generates `config/settings.json`, a local client key registry and `config/owner-token.txt` (sensitive; private folder only). SQLite remains at relative `data/events.sqlite` by default. No registry edits, Windows service or driver installation.
- `tests/Test-PortableRelocation.ps1` performs actual ZIP extraction, first run, API query, process stop, **move/rename folder**, restart, retained owner key, old event lookup, and checkpoint checks. It passed on the development Windows machine using an isolated test folder.
- Source/build tools and the .NET SDK are needed to *produce* the ZIP, not to *run* it. Self-contained .NET runtime redistribution notices are included in the package.
- Moving across machines may change provider access/identity and filesystem ACL behavior; only same-machine directory relocation has been verified so far.

## Important limitations

- No promise of lossless collection: initial coverage is bounded, a provider may drop events, and severe log churn/clear can defeat replay.
- Collection is polling with saved bookmarks, **not** a live ETW subscription; poll period is five seconds, faster when draining pending batches.
- Only System and Application Windows Event Log channels are collected by default. Security and Sysmon event channels are opt-in **raw Event Log reads**, not yet semantic decoders; WFP, registry, file, drivers, devices and full network tracing are not implemented.
- Source-specific permissions, detailed telemetry loss diagnostics, **strict** quota/reclamation, robust disk I/O failure recovery and separation of elevated collector from unprivileged API remain. Basic free-disk guard is implemented but is a pre-write check, not a filesystem reservation. Basic opt-in retention and approximate soft-budget suspension are implemented.
- The old shared Token remains supported for compatibility; client key hashes with **category-level** read scopes are now supported. Fine-grained per-channel access, rotation without restart, read audit, rate limiting and optional MCP connector remain future features.
- Data files are sensitive; filesystem permissions must protect the executable folder or configured data directory.

## Next engineering priorities

1. Atomic source checkpoint regression tests with generated multi-batch fixtures and independent read/replay.
2. **Completed:** multi-channel Windows Event Log framework with independent bookmarks, coverage status and unavailable-source isolation, without changing Windows Audit Policy remotely.
3. **Partially completed:** opt-in retention with deletion history, read-only storage status, soft-budget write pause and free-disk preflight guard; still needs safe SQLite file compaction, more complete I/O error handling and loss diagnostics.
4. **Partially completed:** independent client keys and category-level read scopes. Continue with per-channel restrictions, read audit, token rotation and evidence export policy.
5. MCP adapter and dashboard **only after** the data service is stable.

The permanent restriction remains: **NO blocking, process termination, quarantine, remote shell, protection control, or Windows-configuration API**.
