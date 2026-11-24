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

## Process event projection

The process API decodes structured fields on read from retained Windows Event Log evidence. This works with previously collected records and shares evidence IDs and retention with general events. Collection and bookmark transactions use the original record shape. The source event identifies the lifecycle stage. The decoder transforms PID and exit codes into numbers and preserves source paths and command lines.

Sysmon uses its reported `ProcessGuid` as the subject identity and can report `ParentProcessGuid` separately. In Security 4688, the parent fields describe the reported creator. Security 4688/4689 provide PIDs and may be missing process fields depending on provider version and audit configuration. The projection leaves unknown fields null and makes no cross-source joins based on PID.

## Network and DNS event projection

Network connection and DNS views decode fields from existing Event Log evidence. They preserve the same event ID as the generic summary, source channel/provider/event/record IDs, and collection and occurrence times. Endpoints retain source/destination order and include a role when the provider supplies enough information. Windows Security WFP decisions describe permit/block outcomes at the filtering layer. Sysmon DNS results preserve the provider's string, including any returned aliases or addresses. The Sysmon subject GUID is suitable for exact process filtering. A Windows Security PID alone is not a stable process identity.

## Derived process activity

The activity view groups retained Sysmon 1/5/3/22 by exact Computer plus subject ProcessGuid. Its versioned method is `sysmon_subject_guid_and_computer_v1` and its grouping quality is `derived`. Each item preserves its evidence ID and typed source projection. No correlation edges are written into raw evidence and no new storage schema is required. The grouping establishes equality of reported fields, with explicit limitations rather than an unmeasured confidence score. PID reuse, parent GUIDs, command-line mentions and DNS result strings cannot supply a missing subject GUID. It makes no claim that a DNS event caused a connection or that a connection succeeded. Unknown host or GUID fields are excluded. See [Activity guide](PROCESS_ACTIVITY_TH.md).

## Derived process tree

The tree graph uses exact computer plus Sysmon subject and reported parent GUIDs from creation records. It expands descendants from a requested root and represents other parents as reference-only nodes. A reference is not a fabricated creation event. Relationship `basis=sourceReported` carries the original child ProcessEvent; the graph's `quality=derived` describes its assembly from observations. Missing GUIDs and self references are unresolved evidence, conflicting parents remain visible, and cycles are flagged without unbounded recursion. Depth/node/event budgets explicitly mark partial expansion. Every query in one graph uses one SQLite read snapshot; there are no derived writes or schema changes. See [Tree guide](PROCESS_TREE_TH.md).

## Retention and disk-policy evidence

The new `retention_deletions` table stores per-channel deletion summaries: deletion time, count, oldest/newest observation timestamps, and policy cutoff. A local-only opt-in `SYSWITNESS_RETENTION_DAYS` determines whether old event copies may be deleted; default is OFF. The evidence records and deletion summaries are modified in one SQLite transaction. Collector checkpoints are intentionally unaffected by retention. The API exposes deletion summaries but never issues deletion commands. Soft limits estimate physical database/WAL/SHM bytes plus incoming batch growth; exceeding the estimate pauses ingestion and retains the prior checkpoint. This is **not** a strict filesystem size guarantee or proof of lossless coverage.

## Reliability
Initial Windows System collection backfills at most 256 most recent records, with an explicit `initial_history_outside_backfill_window` coverage record when earlier source records exist. The native Windows EventBookmark and evidence records commit in the same SQLite transaction; source clear, record rollover and invalid bookmark cases are surfaced as `collection_gaps` rather than silently declaring perfect coverage. The collector polls every 5 seconds while idle and drains batches of up to 256 records; it is not a lossless real-time subscription. Event identity is a SHA-256 hash of channel plus raw source XML; process identity, host/boot identity and source record retention semantics require further study. Preserve acquisition time, loss estimates and source limitations. Native sources may have losses we cannot detect; even `recordedGaps=0` is not proof of completeness.
