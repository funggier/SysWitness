# Data API Contract (Design)

Base URL: `http://127.0.0.1:<configured-port>/api/v1` (port assigned by local configuration). **All public operations are data reads**; a POST is allowed only for structured, read-only queries.

## Endpoints (planned)
| Method | Path | Description | Scope |
|---|---|---|---|
| GET | /health | API availability without machine-private details | health.read |
| GET | /system/summary | Approved machine overview | system.read |
| GET | /monitoring/coverage | Enabled sources, gaps, provider status | coverage.read |
| GET | /events | Filtered and paginated events | events.read |
| GET | /events/{id} | One normalized event | events.read |
| POST | /events/query | Complex *read-only* filter, not arbitrary SQL | events.read |
| GET | /evidence/{id} | Raw evidence if authorized | evidence.read |
| GET | /processes/{id}/timeline | Correlated process timeline | analysis.read |
| GET | /network/connections | Source-backed observed connections | network.read |
| GET | /events/stream | Authenticated SSE with cursor and replay | events.stream |
| GET | /schemas/events | Supported event schemas and field coverage | schema.read |

Examples above are a **target contract**, not a promise of current implementation.

## Query rules
- Structured filters only: utc time range, source, category, process identity, IP/port, event id.
- Limit (default 100, max 500 initial target); stable cursor and ordering; no unlimited dumps.
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
