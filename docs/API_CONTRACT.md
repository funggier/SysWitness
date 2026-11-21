# Data API Contract (Design)

Base URL: `http://127.0.0.1:<configured-port>/api/v1` (port assigned by local configuration). **All implemented operations are GET data reads**. A POST for a structured, read-only query remains a future design option, not a current route.

## Endpoints (implemented and planned)
| Method | Path | Description | Scope |
|---|---|---|---|
| GET | /health | API availability without machine-private details | public (no token) |
| GET | /system/summary | Approved machine overview | system.read |
| GET | /monitoring/coverage | Aggregate and per-channel source health; optional `?channel=Application` | coverage.read |
| GET | /monitoring/gaps | Source-specific gap history, `?channel=System&limit=50` | coverage.read |
| GET | /storage/status | SQLite size, soft limit, event count, retention policy and counts | storage.read |
| GET | /storage/retention | Per-channel deletion summaries, optional `?limit=50` | storage.read |
| GET | /events | Filtered and paginated events | events.read |
| GET | /events/{id} | One normalized event | events.read |
| POST | /events/query | Complex *read-only* filter, not arbitrary SQL | events.read |
| GET | /evidence/{id} | Raw evidence if authorized | evidence.read |
| GET | /processes/events | Source-backed process start/exit events | processes.read |
| GET | /processes/{processGuid}/timeline | Sysmon events for a single subject Process GUID | processes.read |
| GET | /network/connections | Sysmon connections and Windows Filtering Platform audit decisions | network.read |
| GET | /network/dns | Sysmon DNS query events and reported outcomes | network.read |
| GET | /events/stream | Authenticated SSE with cursor and replay | events.stream |
| GET | /schemas/events | Supported event schemas and field coverage | schema.read |

**Implemented in the current prototype:** GET /health, GET /monitoring/coverage (aggregate or `?channel=...`), GET /monitoring/gaps (`?channel=...&limit=...`), GET /storage/status, GET /storage/retention (`?limit=1..100`), GET /events (limit, cursor, channel, category, provider, sourceEventId, fromUtc, toUtc), GET /events/{id}, GET /processes/events, GET /processes/{processGuid}/timeline, GET /network/connections and GET /network/dns. All other endpoints are **planned**, not implemented. Data endpoints accept `X-SysWitness-Token` using either a legacy token for event summaries, coverage and storage or a client-specific key from the local SHA-256 registry. Valid credentials lacking required read scopes receive HTTP 403. Unknown routes and all non-GET methods receive HTTP 404 after authentication. Raw event XML remains in local SQLite and is not exposed by the initial API.

## Local storage policy (no remote configuration)
- Retention is disabled by default. A *local* environment variable `SYSWITNESS_RETENTION_DAYS` may enable opt-in deletion of aged local event copies with durable summary records. `SYSWITNESS_STORAGE_SOFT_LIMIT_MB` defaults to 4096 MiB and conservatively pauses ingestion on projected storage pressure without advancing bookmarks.
- `GET /storage/status` exposes approximate physical SQLite/WAL/SHM bytes, configured soft limit, current evidence count, and total deleted by local retention, plus `minFreeDiskBytes`, `availableFreeDiskBytes` (null if unavailable), `diskReserveReached`, `canAcceptNewEvidence`, and `ingestPauseReason`. The data is a point-in-time snapshot, not a guaranteed write reservation.
- `GET /storage/retention` returns only durable deletion summary records. There is no POST to prune, change retention, or touch Windows Event Logs.
- The soft limit is **not** a hard disk quota; SQLite file/WAL overhead can exceed the estimate. See [Storage policy](STORAGE_POLICY_TH.md).

## Monitoring coverage rules
- `GET /monitoring/coverage` reports an aggregate status, `configuredCollectors[]` (all locally selected sources), `enabledCollectors[]` (currently collecting sources), `degradedCollectors[]`, and a `channels[]` array. The existing top-level `channel`, `lastRecordId`, `recordedGaps` fields refer to System (or the first configured channel if System is disabled) for backward compatibility.
- `GET /monitoring/coverage?channel=Application` returns only Application status. A configured source that is unavailable or unreadable reports `degraded` and a sanitized error class (not raw exception content). Querying an unconfigured channel returns 404.
- `GET /monitoring/gaps?channel=Application&limit=50` returns that channel's persisted gap records, up to 100, and defaults to System where configured.
- Sources are selected by the *local* environment `SYSWITNESS_EVENT_CHANNELS`, not remotely through API/MCP.

## Query rules
- **Current filters:** `limit` (1..500), `cursor` (opaque base64), `channel`, `category`, `provider`, `sourceEventId` (0..65535), `fromUtc` (inclusive), and `toUtc` (exclusive). UTC filters require timezone-aware timestamps such as `2026-11-20T11:00:00Z`; normalized UTC is used for comparisons. `nextCursor` is returned when there may be more records. Unknown or duplicate parameters produce HTTP 400.
- **Planned filters for the general event route:** source fields, process identity, IP/port, cross-source correlation and advanced search. See the typed routes for their available filters.
- Limit defaults to 100 and is capped at 500; results order by `(occurred_at_utc DESC, event_id DESC)`; cursors support resuming pagination, not snapshot isolation under concurrent writes. No unlimited dumps.
- Implemented: bounded result count and structured filters. Future: query-cost budgets, enforced time-range limits and query-duration caps. Invalid input uses machine-readable error codes.
- Unknown fields are null/omitted with coverage annotation; never fabricate a value.
- Version under /api/v1; additive fields backward compatible; breaking changes in /api/v2.
- OpenAPI contract and integration/authorization tests precede third-party SDK promises.

## Process monitoring

- `GET /processes/events` includes Sysmon process creation (1) and termination (5), and Windows Security audit process creation (4688) and exit (4689). Data comes from the corresponding channels in local SQLite. Source availability and collection gaps are reported through the coverage endpoints. The feed also includes local WMI and snapshot observations. An empty process page means there are no matching retained records.
- `GET /processes/{processGuid}/timeline` matches the Sysmon **subject** Process GUID. Supply a hyphenated, nonzero GUID such as `11111111-2222-4333-8444-555555555555`. An unknown GUID returns an empty page with HTTP 200. Security events retain their source PID and cannot be matched by this endpoint.
- Both use the event pagination envelope `items`, `limit`, `returned` and `nextCursor`. Use `channel`, `provider`, `sourceEventId`, `fromUtc`, `toUtc`, `limit` and `cursor` as with the event API. Keep the same filters and GUID when following a cursor. Results are newest first. Narrow timestamp filters reduce work for large histories.
- Each item includes `eventId`, source and observation times, `kind` (`start` or `exit`), `source` (channel, provider, source event/record ID and computer), `payloadStatus`, PID, GUID, image path, command line, parent and exit status where provided. A field that is absent or unreadable is null. `payloadStatus` is `decoded` when at least one process field is available, or `unavailable` when no field could be decoded.
- For local observations, filter by `channel=WindowsProcessWmi` or `channel=WindowsProcessSnapshot`. The `observationMethod` is `eventLog`, `wmi` or `snapshot`. WMI start/exit is `quality=observed` and includes source values such as process name, parent PID or exit status where available.
- Snapshot start and exit mean an appearance or disappearance between process inventories. They are `quality=derived`; `occurredAtUtc` is the sample time. When Windows exposes the creation time, `processStartedAtUtc` and `localProcessInstanceId` are included. The local ID uses computer, PID and creation time. Processes that run entirely between samples can be missed. The initial inventory acts as the baseline.
- Aggregate `GET /monitoring/coverage` has a `processCollector` object with `mode`, `activeSource`, `status`, `fallbackReason`, `lastError`, `lastPollAtUtc` and `droppedEvents`. `droppedEvents` counts local WMI capture/queue drops in the current run. The per-channel coverage and bookmark fields continue to describe Windows Event Log.
- These routes require an explicit `processes.read` scope, which includes access to sensitive command lines. Give it only to clients that need process details. The standard `/events` responses retain their existing summary fields and categories.

See [Sysmon events](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon#events), [Security 4688](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-4688) and [Security 4689](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-4689).

## Network and DNS monitoring

- `GET /network/connections` reads Sysmon Event 3 and Windows Security Events 5156 and 5157. The response includes protocol, direction, reported endpoints and evidence provenance. Security events expose `decision=allowed` or `blocked` from the Windows Filtering Platform (WFP) audit. An allowed decision establishes that WFP permitted the operation. Sysmon connection records have a null `decision`.
- `sourceEndpoint` and `destinationEndpoint` preserve the provider's roles. Their `role` indicates `local` or `remote` when supported. For WFP, the source side is local even for inbound decisions. Sysmon role assignment follows the reported initiation direction. The address, port and hostname are nullable. TCP and UDP ports are exposed where available. Other WFP protocols retain the reported protocol number.
- `GET /network/dns` reads Sysmon Event 22. It exposes the reported `queryName`, numeric `queryStatus` and the provider's `queryResults` string when present. DNS evidence can represent a cache hit or a failed lookup, and is independent of connection success or any subsequent connection.
- Both routes support `channel`, `provider`, `sourceEventId`, `fromUtc`, `toUtc`, `limit`, `cursor` and `processGuid` (hyphenated nonzero GUID). The GUID filter matches the Sysmon subject's named field before pagination. Keep the same filters when following a cursor. The page format matches the event feed.
- `process.attribution` is `processGuid`, `pidOnly` or `unavailable`. Windows Security exposes its reported PID and image when recorded. A Security event will never match a GUID query from its PID. When fields are inaccessible or invalid, the view leaves them null and reports `payloadStatus`.
- Both routes require explicit `network.read`. Sysmon and Security Event Log channels must be enabled locally and readable. Sysmon network logging must be enabled in Sysmon, and Security connection decisions require the matching audit policy. An empty page means there are no matching *retained* records. These routes expose event metadata, not packet contents or decrypted traffic.

See [Sysmon event reference](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon#events) and [WFP connection auditing](https://learn.microsoft.com/en-us/windows/win32/fwp/auditing-and-logging).

## Security model
- Loopback-only by default; require explicit opt-in & secure transport for remote clients.
- Authentication on data endpoints (health response minimized); implemented client-key registry (file-based SHA-256 hash list, up to 32 entries) and read scopes `events.read`, `coverage.read`, `storage.read`, `processes.read`, and `network.read`. The legacy `SYSWITNESS_API_TOKEN` keeps its original three scopes. All known routes are explicitly scoped, unknown paths fail closed.
- Raw evidence API is still unavailable. Future raw evidence access needs independent authorization/redaction before handing data to AI models.
- Current: loopback binding and Host validation; CORS policy hardening, explicit Origin checks, body-size budgets and rate limiting remain future tasks. No mutation or command endpoint exists.
- Detailed read-access audit is still planned; do not log credentials or sensitive payloads. Client registry updates currently require restart; rotate/revoke by editing the local file and restarting.
- Separate local config/setup: **not reachable through API**. See [Client authentication guide](API_CLIENTS_TH.md).

## Explicitly prohibited API routes
No `/firewall/*`, `/protection/*`, `/processes/{id}/kill`, `/commands/*`, `/setup/*`, `/config/update`, arbitrary SQL, shell execution, or control callbacks.
