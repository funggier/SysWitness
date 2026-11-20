# Event and Evidence Data Model

## Identity and provenance
Each event has a stable internal ID and: `schema_version`, `category`, `source.provider`, `source.channel`, `source.record_id`, `source_event_id`, `occurred_at_utc`, `observed_at_utc`, optional `host_id` (locally private), quality flags and raw event reference. A provider's Event ID is **not** unique per occurrence.

## Example normalized event (synthetic only)
```json
{
  "schema_version": "1.0",
  "event_id": "example-01",
  "occurred_at_utc": "2026-11-20T11:32:03Z",
  "observed_at_utc": "2026-11-20T11:32:04Z",
  "category": "network.connection",
  "source": {"provider": "Microsoft-Windows-Sysmon", "channel": "Operational", "record_id": "10001", "event_id": 3},
  "process": {"pid": 4210, "identity": null, "name": "example.exe"},
  "network": {"destination_ip": "203.0.113.20", "destination_port": 443, "protocol": "tcp"},
  "evidence": {"raw_available": true, "quality": "observed"},
  "analysis": {"state": "none"}
}
```
203.0.113.0/24 is a documentation IP range; do not treat the example as a live threat report.

## Three storage tiers
1. **Evidence:** source payload + metadata; append-only logical records, keyed by event ID, optional hash and retention marker.
2. **Normalized:** stable fields for cross-source queries and index.
3. **Derived:** correlation edges, baselines and detection explanations, referencing evidence IDs and rule version, never overwriting evidence.

## Event quality
`observed` = direct source; `derived` = deterministic transform; `inferred` = analysis hypothesis; `unknown` = missing or unsupported. Distinguish optional fields from false/negative evidence.

## Reliability
Deduplicate through source/channel/record identity while respecting log rollover and boot/session identity; bookmark source positions. Capture collector start/stop, dropped buffers, backpressure, and retention gaps. Preserve acquisition time to disambiguate late/out-of-order events. Privacy constraints apply to raw payloads and export.
