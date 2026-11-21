# SysWitness

**Deep System Observability • Read-only Data API • Windows-first**

SysWitness เป็นโปรเจกต์สำรวจและบันทึกเหตุการณ์ของคอมพิวเตอร์อย่างละเอียด ทั้งระบบ เครือข่าย โปรเซส บัญชีผู้ใช้ ไฟล์ Registry และความปลอดภัย โดยมี API กลางให้โปรแกรมอื่นรวมถึง AI Local และ MCP Client **อ่าน ค้นหา และวิเคราะห์ข้อมูลได้เท่านั้น**

> **Status:** M1/M2 observation prototype. Collects Windows **System** and **Application** Event Log channels independently by default (initial 256-event backfill per channel), then uses per-channel native EventBookmarks committed atomically with SQLite evidence. A read-only localhost API supports bounded searches, storage health, and per-client scoped read keys. Local opt-in retention keeps deletion summaries; default retention is **OFF**. **Security** and **Sysmon** are explicit opt-ins and may be inaccessible. This is not lossless auditing; other deep telemetry collectors, detailed access audit and MCP adapter are planned.

## Non-negotiable boundaries
- **Observation only:** no firewall blocking, process termination, file quarantine, registry protection, remote shell, or system enforcement.
- **Data API only:** no external API endpoints to configure collection, modify Windows, change evidence, or operate a protection program.
- **Independent clients:** Dashboard, MCP, AI and future applications are optional consumers, never required for monitoring.
- **Independent protection:** a future protection application may read events but has its own controls and operates independently.
- **Evidence first:** preserve source, UTC time, missing fields and correlation confidence; do not turn inferred events into facts.
- **Local first:** bind to loopback by default, authenticate clients, authorize read scopes, avoid exporting private telemetry accidentally.

## Architecture
```text
Windows Event Log | Sysmon | ETW | WFP | NTFS USN | device sources
                           |
                      Collectors
                           |
                Normalize + Evidence Store
                           |
                Search / Correlation / Index
                           |
                  Read-only HTTP API
                  /        |        \
             Dashboard   MCP       SDK / other programs
```

## Project documents
- [Portable Windows ZIP / relocate instructions (Thai)](portable/README-PORTABLE-TH.md)
- [Current implementation status and next steps](docs/DEVELOPMENT_STATUS.md)
- [Storage and retention policy (Thai)](docs/STORAGE_POLICY_TH.md)
- [Client API keys and read scopes (Thai)](docs/API_CLIENTS_TH.md)
- [Detailed project plan (Thai)](docs/PROJECT_PLAN_TH.md)
- [Architecture and system boundaries](docs/ARCHITECTURE.md)
- [API contract and permissions](docs/API_CONTRACT.md)
- [Evidence/data model](docs/DATA_MODEL.md)
- [Privacy and threat model](docs/SECURITY.md)
- [Milestones and acceptance criteria](docs/ROADMAP.md)
- [Architecture decision: observation-only](docs/decisions/0001-observation-only.md)
- [Third-party dependency notices](docs/THIRD_PARTY_NOTICES.md)
- [Windows CI pipeline](.github/workflows/windows-ci.yml)

## Proposed technology
C#/.NET 10, ASP.NET Core Minimal API, SQLite in MVP, Windows Event Log as initial provider. Future integrations are modular and opt-in. Elevated Windows audit configuration is a distinct local setup operation, **not** a remote API feature.

## Portable Windows x64 build

The **self-contained portable ZIP** is built with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Portable.ps1`. The output is in ignored `artifacts/`. Extract the ZIP, run `Start-SysWitness.cmd`, and stop the app before moving the **entire** folder to any other writable Windows location. The launcher resolves its paths relative to itself, creates local `config/` and `data/` folders, generates a random first-run owner API key locally, and never stores absolute drive paths in its default settings. **The ZIP deliberately excludes logs, databases and secrets.** See [portable guide](portable/README-PORTABLE-TH.md). The user machine does not need a separate .NET runtime.

To verify real relocation on Windows, run `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-PortableRelocation.ps1` after building the ZIP. Portable mode does not install a service, driver, audit policy or global environment variables.

## Quick start (development only)

Requires Windows with .NET 10 SDK. Run from PowerShell:

```powershell
dotnet build SysWitness.sln
dotnet run --project tests/SysWitness.SmokeTests
powershell -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$env:SYSWITNESS_API_TOKEN = -join ($bytes | ForEach-Object { $_.ToString('x2') })
$env:SYSWITNESS_EVENT_CHANNELS = 'System,Application' # default; optional Security/Sysmon below
dotnet run --project src/SysWitness.Api
```

In a separate terminal, use the same secret as `X-SysWitness-Token` (never share it or commit it):

```powershell
Invoke-RestMethod http://127.0.0.1:8769/api/v1/health
Invoke-RestMethod http://127.0.0.1:8769/api/v1/events?limit=10 -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod http://127.0.0.1:8769/api/v1/monitoring/coverage -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod 'http://127.0.0.1:8769/api/v1/monitoring/gaps?channel=Application' -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod 'http://127.0.0.1:8769/api/v1/monitoring/coverage?channel=Application' -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod 'http://127.0.0.1:8769/api/v1/storage/status' -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod 'http://127.0.0.1:8769/api/v1/storage/retention' -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod 'http://127.0.0.1:8769/api/v1/events?limit=20&channel=System&sourceEventId=6005' -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
```

**Collection source selection:** `SYSWITNESS_EVENT_CHANNELS` accepts a comma-separated local allowlist: `System`, `Application`, `Security`, `Microsoft-Windows-Sysmon/Operational`. The default is `System,Application`. Optional channels must already exist and be readable by this Windows account. An unavailable/permission-denied channel reports `degraded` without stopping the others; this program never changes Windows Audit Policy or installs Sysmon through its data API. The coverage response includes per-channel status and the gaps API accepts `?channel=...`.

**Storage policy:** By default SysWitness never automatically removes event records. The soft storage budget defaults to 4096 MiB, and a separate free-disk reserve defaults to 1024 MiB on the database volume. Ingest pauses with the native bookmark unchanged if an event batch would exceed the estimated database budget, consume the configured free-space reserve, or if the volume's free bytes cannot be verified. Local-only `SYSWITNESS_STORAGE_SOFT_LIMIT_MB` adjusts the limit (16..1048576); `SYSWITNESS_MIN_FREE_DISK_MB` adjusts the disk reserve (128..1048576); setting `SYSWITNESS_RETENTION_DAYS` (1..3650) explicitly enables periodic cleanup of aged *local evidence copies*, with permanent summary records. This is a **soft** budget, not a guaranteed physical file quota. See [Storage policy](docs/STORAGE_POLICY_TH.md).

**Client authorization:** The legacy `SYSWITNESS_API_TOKEN` continues to work with all read scopes, while an optional `SYSWITNESS_CLIENT_KEYS_FILE` (local JSON containing SHA-256 hashes, IDs and `events.read`/`coverage.read`/`storage.read` scopes) enables distinct read-only credentials per application. You can use the client file alone without a legacy token. Authorization is still category-level, not per-Channel/per-Event. See [Client keys](docs/API_CLIENTS_TH.md).

Environment: `SYSWITNESS_PORT` changes the local port; `SYSWITNESS_DATA_DIRECTORY` overrides the default database folder (`data/` beside the executable), making the development prototype directory-portable when that directory is writable. Avoid putting sensitive database files into source control or read-only installed locations. This is **not** a full audit or security product. The initial 256-event historical backfill **per configured channel** is bounded; if older System records exist, the API records a visible `initial_history_outside_backfill_window` coverage gap. Native bookmarks reduce repeat/missed-event risk on restart, but cleared/rotated logs, provider losses, interruptions, and other circumstances can still cause missing records. Collection uses a 5-second polling interval; granular per-event permissions, detailed access auditing, token rotation without restart, and MCP remain unimplemented. The API only accepts requests to localhost/127.0.0.1 and validates the HTTP Host header.

## Development and confidentiality
Do not commit real Windows Event Logs, SQLite databases, machine identifiers, credentials, API tokens, system traces or other private host data. Keep runtime data outside the source tree or in ignored folders.

## License
**Copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.**

This repository is **publicly viewable but not open-source licensed**. No permission to use, redistribute, modify, or incorporate this code into other projects is granted by publishing it. See [LICENSE](LICENSE). Third-party dependencies retain their own licenses.
