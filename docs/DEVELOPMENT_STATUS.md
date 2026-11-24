# SysWitness — Development Status (2026-11-24)

## Current build (observation prototype with initial activity correlation)

- **Repository:** funggier/SysWitness, public; copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.
- **Local workspace:** `T:\Space\Projects\ProjectsProgram\SysWitness`.
- **Runtime:** Windows 10, .NET 10 SDK, local SQLite.
- **Live collectors:** Windows Event Log **System** and **Application** channels by default, with independent workers. Each initially backfills up to 256 newest events and records `initial_history_outside_backfill_window` if earlier records exist. **Security** and **Sysmon** are optional local configuration and may not be accessible.
- **Subsequent collection:** per-channel native EventBookmarks; events and corresponding checkpoint are committed in **one database transaction**, with read failure/invalid bookmark and log rotation/clear indicators. An unavailable optional channel is marked `degraded` and retried later, without stopping other collectors.
- **API:** `127.0.0.1:8769` by default; either legacy `SYSWITNESS_API_TOKEN` or local `SYSWITNESS_CLIENT_KEYS_FILE` authenticates clients. Per-client `events.read`/`coverage.read`/`storage.read` scopes are enforced with HTTP 401/403 on unauthorized access. No writable/action endpoints; rejects unexpected Host header; event summary excludes raw XML.
- **Read methods:** health, aggregate/per-channel monitoring coverage with separate configured/collecting/degraded lists, per-channel gap history, storage status and retention summaries, event search with page cursor and channel/category/provider/sourceEventId/fromUtc/toUtc filters, event-by-ID.
- **Process monitoring:** Sysmon 1/5 and Windows Security 4688/4689 are decoded from retained evidence. The authenticated process feed exposes source provenance, lifecycle and parent fields, and the timeline groups Sysmon records by subject GUID. Requires explicit `processes.read`.
- **Network monitoring:** The network API decodes Sysmon TCP/UDP and DNS events plus Windows Security WFP permit/block records. Both feeds require `network.read`. Sysmon events can be filtered by the original process GUID. Windows Security events retain PID-only attribution.
- **Initial correlation:** `GET /api/v1/processes/{processGuid}/activity?computer=<recorded-computer>` combines retained Sysmon lifecycle, DNS and connection records by exact subject GUID and host. It requires both `processes.read` and `network.read`, retains evidence IDs, and labels the grouping as derived with explicit limits. No PID-only or causal joins. See [Activity guide](PROCESS_ACTIVITY_TH.md).
- **Local process collection:** Default `auto` receives WMI process events if permitted and falls back to snapshots, reporting the selected source and fallback reason in monitoring coverage. The process API exposes local observations with source and quality fields. The local collector operates under the caller's current Windows account.
- **Data folder:** `data/` beside the executable unless `SYSWITNESS_DATA_DIRECTORY` is specified; choose a protected writable path. **Retention defaults OFF**; optional `SYSWITNESS_RETENTION_DAYS` enables bounded, audited age-based deletion of local Event copies. A 4096 MiB default **soft** storage budget and a separate 1024 MiB **minimum free-disk reserve** (`SYSWITNESS_MIN_FREE_DISK_MB`) pause new writes if the respective threshold would be exceeded, or if available disk space cannot be inspected, keeping native bookmarks unchanged. See [Storage policy](STORAGE_POLICY_TH.md).
- **Optional client architecture:** Dashboard, read-only MCP, AI and third-party apps remain future adapters to this same API. Protection stays entirely external.

## Verification

On the Windows development machine:
- `dotnet build SysWitness.sln -c Release`: 0 errors, 0 warnings.
- `dotnet run --project tests/SysWitness.SmokeTests -c Release --no-build`: pass for stable IDs, deduplication, atomic rollback, restart persistence, persisted gaps, bounded filters, disk-space guards and read-only client-key scope/config validation.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1`: pass against real Windows System/Application Event Logs, independent restarts/checkpoints, optional unavailable Sysmon, volume free-space API, isolated client scopes with HTTP 200/401/403/404 and absent action endpoints. Testing uses an isolated temporary database and random token.
- `dotnet list SysWitness.sln package --vulnerable --include-transitive`: no listed vulnerable dependency in latest local query. Always re-audit on release.
- GitHub Windows workflows successfully completed for commits `62c3b0f` and `f9a69c3`. Current commit result must be checked separately.

## Current-session verification (2026-11-24)

- Started from clean local/GitHub `main` at `a89b40d133a3f4a143344f6a09ed445a05e499b2`. The earlier handoff's `e9b8fed` predates three implemented process/network commits; its published preview.1 remains historical evidence.
- Windows Release build with warnings-as-errors passed. New activity fixture tests passed for identity/host isolation, negative PID/parent/payload matches, ambiguous/malformed input, tied-time pagination, query bounds and preserved evidence references.
- HTTP API smoke passed with 200/400/401/403/404, combined process+network authorization, host isolation, activity pagination and no raw XML, alongside live System/Application restart regressions. Correlation input remains synthetic.
- The pre-implementation source check found Sysmon unavailable, Security unreadable and WMI access denied. Auto process collection passed with snapshot fallback. No provider or Windows audit settings were changed.
- Preview.2 ZIP built and passed same-machine folder relocation, retained token/event/checkpoints and combined activity API checks. Archive inspection found no runtime private data and no missing expected redistribution notices. NuGet vulnerability query listed no vulnerable packages from current sources. SHA-256: `5DAA103E17C0B155E1AA2C3C67642C0AB8F708D9090DE208B119DCAF6062C117`.
- Source GUID extraction still occurs on query, as in the prior GUID timeline. Result limits do not impose a scan-duration budget. Workload benchmarks and live Sysmon end-to-end validation remain open.

## Portable Windows x64 packaging

- `scripts/Build-Portable.ps1` produces `artifacts/SysWitness-0.1.0-preview.2-win-x64-portable.zip` using .NET 10 **self-contained**, untrimmed win-x64 publish. No .NET install is required on the target machine.
- Open `Start-SysWitness.cmd` from the extracted folder; first run generates `config/settings.json`, a local client key registry and `config/owner-token.txt` (sensitive; private folder only). SQLite remains at relative `data/events.sqlite` by default. No registry edits, Windows service or driver installation.
- `tests/Test-PortableRelocation.ps1` performs actual ZIP extraction, first run, API query, process stop, **move/rename folder**, restart, retained owner key, old event lookup, and checkpoint checks. It passed on the development Windows machine using an isolated test folder.
- Source/build tools and the .NET SDK are needed to *produce* the ZIP, not to *run* it. Self-contained .NET runtime redistribution notices are included in the package.
- Moving across machines may change provider access/identity and filesystem ACL behavior; only same-machine directory relocation has been verified so far.

## Important limitations

- No promise of lossless collection: initial coverage is bounded, a provider may drop events, and severe log churn/clear can defeat replay.
- Collection is polling with saved bookmarks, **not** a live ETW subscription; poll period is five seconds, faster when draining pending batches.
- System and Application are collected by default. Process monitoring needs the Security or Sysmon channel and suitable local permissions and provider/audit settings. On the development account the Sysmon channel was unavailable and Security could not be read. Event Log process decoding and the GUID timeline are exercised with synthetic Windows XML. The local process collector has separate Windows integration tests in `tests/Test-ProcessCollection.ps1`. Network and DNS decoding and HTTP authorization are exercised with synthetic Sysmon and Security fixtures. Live end-to-end network provider testing requires readable, configured channels. Direct WFP/ETW subscriptions, file, registry, drivers and devices remain planned.
- Source-specific permissions, detailed telemetry loss diagnostics, **strict** quota/reclamation, robust disk I/O failure recovery and separation of elevated collector from unprivileged API remain. Basic free-disk guard is implemented but is a pre-write check, not a filesystem reservation. Basic opt-in retention and approximate soft-budget suspension are implemented.
- The old shared Token remains supported for compatibility; client key hashes with **category-level** read scopes are now supported. Fine-grained per-channel access, rotation without restart, read audit, rate limiting and optional MCP connector remain future features.
- Data files are sensitive; filesystem permissions must protect the executable folder or configured data directory.

## Next engineering priorities

1. **Initial correlation completed:** host-scoped Sysmon process/DNS/network activity with explicit derivation and evidence references. Next extend parent/child relationships and cross-source identity only when supported by evidence; verify live Sysmon and Windows Security sources when available.
2. Atomic source checkpoint regression tests with generated multi-batch fixtures and independent read/replay.
3. **Completed:** multi-channel Windows Event Log framework with independent bookmarks, coverage status and unavailable-source isolation, without changing Windows Audit Policy remotely.
4. **Partially completed:** opt-in retention with deletion history, read-only storage status, soft-budget write pause and free-disk preflight guard; still needs safe SQLite file compaction, more complete I/O error handling and loss diagnostics.
5. **Partially completed:** independent client keys and category-level read scopes. Continue with per-channel restrictions, read audit, token rotation and evidence export policy.
6. MCP adapter and dashboard **only after** the data service is stable.

The permanent restriction remains: **NO blocking, process termination, quarantine, remote shell, protection control, or Windows-configuration API**.
