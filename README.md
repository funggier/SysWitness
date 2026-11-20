# SysWitness

**Deep System Observability • Read-only Data API • Windows-first**

SysWitness เป็นโปรเจกต์สำรวจและบันทึกเหตุการณ์ของคอมพิวเตอร์อย่างละเอียด ทั้งระบบ เครือข่าย โปรเซส บัญชีผู้ใช้ ไฟล์ Registry และความปลอดภัย โดยมี API กลางให้โปรแกรมอื่นรวมถึง AI Local และ MCP Client **อ่าน ค้นหา และวิเคราะห์ข้อมูลได้เท่านั้น**

> **Status:** Early M1 prototype. Reads the latest Windows **System** channel records by polling and stores them in local SQLite. Authenticated read-only localhost API is operational; polling can miss events. The roadmap describes many unimplemented features.

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
- [Detailed project plan (Thai)](docs/PROJECT_PLAN_TH.md)
- [Architecture and system boundaries](docs/ARCHITECTURE.md)
- [API contract and permissions](docs/API_CONTRACT.md)
- [Evidence/data model](docs/DATA_MODEL.md)
- [Privacy and threat model](docs/SECURITY.md)
- [Milestones and acceptance criteria](docs/ROADMAP.md)
- [Architecture decision: observation-only](docs/decisions/0001-observation-only.md)
- [Third-party dependency notices](docs/THIRD_PARTY_NOTICES.md)

## Proposed technology
C#/.NET 10, ASP.NET Core Minimal API, SQLite in MVP, Windows Event Log as initial provider. Future integrations are modular and opt-in. Elevated Windows audit configuration is a distinct local setup operation, **not** a remote API feature.

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
dotnet run --project src/SysWitness.Api
```

In a separate terminal, use the same secret as `X-SysWitness-Token` (never share it or commit it):

```powershell
Invoke-RestMethod http://127.0.0.1:8769/api/v1/health
Invoke-RestMethod http://127.0.0.1:8769/api/v1/events?limit=10 -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
Invoke-RestMethod http://127.0.0.1:8769/api/v1/monitoring/coverage -Headers @{ 'X-SysWitness-Token' = '<your-token>' }
```

Environment: `SYSWITNESS_PORT` changes the local port; `SYSWITNESS_DATA_DIRECTORY` overrides default database folder (`%LOCALAPPDATA%\\SysWitness\\data`). This is a prototype, not a full audit or security product. **Missing events are possible** because the source currently samples the most recent 64 entries every 15 seconds rather than replaying a persistent native bookmark. There is no automatic retention, scope-based multi-client authorization, or MCP server yet.

## Development and confidentiality
Do not commit real Windows Event Logs, SQLite databases, machine identifiers, credentials, API tokens, system traces or other private host data. Keep runtime data outside the source tree or in ignored folders.

## License
**Copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.**

This repository is **publicly viewable but not open-source licensed**. No permission to use, redistribute, modify, or incorporate this code into other projects is granted by publishing it. See [LICENSE](LICENSE). Third-party dependencies retain their own licenses.
