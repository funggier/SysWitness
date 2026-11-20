# Development Roadmap

## M0 — Repository and architecture baseline
- [x] Establish name SysWitness, owner and all-rights-reserved license
- [x] Create public GitHub repository
- [x] Write Thai product specification and architecture decision
- [x] Define documented API, evidence, security and milestone boundaries
- [x] Verify initial commit published

**Gate:** published documentation is coherent and no private telemetry or secrets are committed.

## M1 — Minimal vertical slice
- [x] .NET solution and modular projects
- [x] Read Windows System Event Log with native checkpointed polling, bounded initial history and explicit coverage limitations (not lossless)
- [x] Store actual System events to local SQLite using stable evidence-derived identity (basic normalization)
- [x] Serve token-authenticated localhost-only /api/v1 events, coverage and gap history (initial single-token model)
- [x] Implement bounded cursor pagination, structured filters and automated HTTP smoke tests (401, 400, localhost Host rejection, data-only API and live System events)
- [ ] Add independent per-client authorization and further security/integration tests (M2)
- [x] Demonstrate native EventBookmark recovery across real API restart; document source permissions and bounded initial backfill

**Gate:** observed source event -> evidence -> query response -> restart/replay smoke on real Windows without Dashboard/MCP. **Passed locally on 2026-11-20**, with source-coverage limitations documented.

## M2 — Operational hardening
- [x] Store Windows Event Log native bookmark and evidence in the same SQLite transaction; record initial-backfill and known reset/rotation coverage gaps
- [x] Opt-in age-based retention, durable per-channel deletion summaries and read-only storage status API; test that bookmarks are unchanged
- [x] Approximate disk soft budget that pauses ingestion without advancing source checkpoints; verified with oversized batch fixture
- [ ] Full storage pressure accounting, safe SQLite compaction, free-disk guard, resilience/backpressure tests and richer source loss metrics
- [x] Initial Windows GitHub Actions build and fixture test succeeded on commit `62c3b0f`; CI verification for subsequent commits remains ongoing
- [x] Multi-channel native Windows Event Log readers for System/Application, separate bookmarks/health/gaps, optional Security/Sysmon with degraded-source isolation
- [ ] Separate collection privilege boundary, local setup, full Health/Coverage views
- [ ] Per-client read scopes, redaction, rotation, audit and misuse tests

## M3 — Deep telemetry
- [ ] Parse Sysmon events into structured process/network identity (opt-in Sysmon Event Log *reading* is supported but not decoded/correlated)
- [ ] ETW/WFP/network collectors with measured impact and loss reporting
- [ ] File/Registry/service/device source adapters with explicit coverage limitations
- [ ] Workload performance and disk impact benchmarks

## M4 — Analytics
- [ ] Process tree/correlation graph, timelines, explanation/provenance links
- [ ] Deterministic rule engine and optional AI-assisted investigation as client
- [ ] Source-visible evidence exports; acceptance tests for inference vs fact

## M5 — Clients
- [ ] Web Dashboard uses identical API paths
- [ ] OpenAPI and minimal SDKs / examples
- [ ] Separate read-only MCP Adapter with no host-control tools

## M6 — Release preparation
- [ ] Portable-friendly packaging and first-run setup documentation
- [ ] Windows 10/11 verification and source-provider compatibility
- [ ] Security, storage, privacy, dependency-license and compatibility audits

**Permanent exclusions:** no blocking, quarantining, killing, remote shell, or protection commands.
