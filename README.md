# SysWitness

**Deep System Observability • Read-only Data API • Windows-first**

SysWitness เป็นโปรเจกต์สำรวจและบันทึกเหตุการณ์ของคอมพิวเตอร์อย่างละเอียด ทั้งระบบ เครือข่าย โปรเซส บัญชีผู้ใช้ ไฟล์ Registry และความปลอดภัย โดยมี API กลางให้โปรแกรมอื่นรวมถึง AI Local และ MCP Client **อ่าน ค้นหา และวิเคราะห์ข้อมูลได้เท่านั้น**

> **Status:** Initial architecture + first implementation phase. The published roadmap describes planned features and must not be mistaken for implemented capabilities.

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

## Proposed technology
C#/.NET 10, ASP.NET Core Minimal API, SQLite in MVP, Windows Event Log as initial provider. Future integrations are modular and opt-in. Elevated Windows audit configuration is a distinct local setup operation, **not** a remote API feature.

## Development and confidentiality
Do not commit real Windows Event Logs, SQLite databases, machine identifiers, credentials, API tokens, system traces or other private host data. Keep runtime data outside the source tree or in ignored folders.

## License
**Copyright (c) 2026 Kittiwat Kitikun — All Rights Reserved.**

This repository is **publicly viewable but not open-source licensed**. No permission to use, redistribute, modify, or incorporate this code into other projects is granted by publishing it. See [LICENSE](LICENSE). Third-party dependencies retain their own licenses.
