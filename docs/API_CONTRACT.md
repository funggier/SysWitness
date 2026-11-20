# Data API Contract (Design)

Base URL: `http://127.0.0.1:<configured-port>/api/v1` (port assigned by local configuration). **All public operations are data reads**; a POST is allowed only for structured, read-only queries.

## Endpoints (implemented and planned)
| Method | Path | Description | Scope |
|---|---|---|---|
| GET | /health | API availability without machine-private details | health.read |
| GET | /system/summary | Approved machine overview | system.read |
| GET | /monitoring/coverage | Aggregate and per-channel source health; optional `?channel=Application` | coverage.read |
| GET | /monitoring/gaps | Source-specific gap history, `?channel=System&limit=50` | coverage.read |
| GET | /events | Filtered and paginated events | events.read |
| GET | /events/{id} | One normalized event | events.read |
| POST | /events/query | Complex *read-only* filter, not arbitrary SQL | events.read |
| GET | /evidence/{id} | Raw evidence if authorized | evidence.read |
| GET | /processes/{id}/timeline | Correlated process timeline | analysis.read |
| GET | /network/connections | Source-backed observed connections | network.read |
| GET | /events/stream | Authenticated SSE with cursor and replay | events.stream |
| GET | /schemas/events | Supported event schemas and field coverage | schema.read |

**Implemented in M1/M2 prototype:** GET /health, GET /monitoring/coverage (aggregate or `?channel=...`), GET /monitoring/gaps (`?channel=...&limit=...`), GET /events (limit, cursor, channel, category, provider, sourceEventId, fromUtc, toUtc) and GET /events/{id}. All other endpoints are **planned**, not implemented. M1 uses a single shared 32+-character API token (X-SysWitness-Token), not yet per-client scoped tokens. Raw event XML remains in local SQLite and is not exposed by the initial API.

## Monitoring coverage rules
- `GET /monitoring/coverage` reports an aggregate status, `configuredCollectors[]` (all locally selected sources), `enabledCollectors[]` (currently collecting sources), `degradedCollectors[]`, and a `channels[]` array. The existing top-level `channel`, `lastRecordId`, `recordedGaps` fields refer to System (or the first configured channel if System is disabled) for backward compatibility.
- `GET /monitoring/coverage?channel=Application` returns only Application status. A configured source that is unavailable or unreadable reports `degraded` and a sanitized error class (not raw exception content). Querying an unconfigured channel returns 404.
- `GET /monitoring/gaps?channel=Application&limit=50` returns that channel's persisted gap records, up to 100, and defaults to System where configured.
- Sources are selected by the *local* environment `SYSWITNESS_EVENT_CHANNELS`, not remotely through API/MCP.

## Query rules
- **Current filters:** `limit` (1..500), `cursor` (opaque base64), `channel`, `category`, `provider`, `sourceEventId` (0..65535), `fromUtc` (inclusive), and `toUtc` (exclusive). UTC filters require timezone-aware timestamps such as `2026-11-20T11:00:00Z`; normalized UTC is used for comparisons. `nextCursor` is returned when there may be more records. Unknown or duplicate parameters produce HTTP 400.
- **Planned filters:** source fields, process identity, IP/port, cross-source correlation and advanced search; do not assume they work today.
- Limit defaults to 100 and is capped at 500; results order by `(occurred_at_utc DESC, event_id DESC)`; cursors support resuming pagination, not snapshot isolation under concurrent writes. No unlimited dumps.
- Bound query cost, duration, and range; error response includes machine-readable code.
- Unknown fields are null/omitted with coverage annotation; never fabricate a value.
- Version under /api/v1; additive fields backward compatible; breaking changes in /api/v2.
- OpenAPI contract and integration/authorization tests precede third-party SDK promises.

## Security model
- Loopback-only by default; require explicit opt-in & secure transport for remote clients.
- Authentication on data endpoints (health response minimized); per-client scopes.
- Independent authorization for raw evidence; redact sensitive details before handing to models.
- CORS/origin/host checks; anti-DNS-rebinding design, bounded body size, rate limits.
- Immutable request audit metadata without logging credentials or sensitive payload by default.
- Separate local config/setup: **not reachable through API**.

## Explicitly prohibited API routes
No `/firewall/*`, `/protection/*`, `/processes/{id}/kill`, `/commands/*`, `/setup/*`, `/config/update`, arbitrary SQL, shell execution, or control callbacks.
