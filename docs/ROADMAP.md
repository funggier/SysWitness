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
- [x] Implement independently scoped read-only client keys with SHA-256 hashes and HTTP 401/403 tests
- [ ] Add per-event/channel authorization, key rotation without restart and further security/integration tests (M2)
- [x] Demonstrate native EventBookmark recovery across real API restart; document source permissions and bounded initial backfill

**Gate:** observed source event -> evidence -> query response -> restart/replay smoke on real Windows without Dashboard/MCP. **Passed locally on 2026-11-20**, with source-coverage limitations documented.

## M2 — Operational hardening
- [x] Store Windows Event Log native bookmark and evidence in the same SQLite transaction; record initial-backfill and known reset/rotation coverage gaps
- [x] Opt-in age-based retention, durable per-channel deletion summaries and read-only storage status API; test that bookmarks are unchanged
- [x] Approximate disk soft budget that pauses ingestion without advancing source checkpoints; verified with oversized batch fixture
- [x] Free disk guard checks available bytes on the database volume and preserves source bookmark when reserve cannot be verified or met; tested with deterministic simulated free-space conditions
- [ ] Full storage pressure accounting, safe SQLite compaction, I/O error recovery, resilience/backpressure tests and richer source loss metrics
- [x] Initial Windows GitHub Actions build and fixture test succeeded on commit `62c3b0f`; CI verification for subsequent commits remains ongoing
- [x] Multi-channel native Windows Event Log readers for System/Application, separate bookmarks/health/gaps, optional Security/Sysmon with degraded-source isolation
- [ ] Separate collection privilege boundary, local setup, full Health/Coverage views
- [x] Read scopes by API category (`events.read`, `coverage.read`, `storage.read`) and per-client hash registry
- [ ] Per-channel scopes, redaction, rotation without restart, detailed read audit and misuse tests

## M3 — Deep telemetry
- [x] Decode process start/exit, parent fields and source provenance from Sysmon and Windows Security; expose the process feed and per-GUID Sysmon timeline
- [x] Collect live process changes through WMI where permitted, with an automatic Windows snapshot fallback and coverage reporting
- [ ] Verify against live Sysmon and Security lifecycle events on a Windows account with accessible sources
- [x] Decode Sysmon network and DNS records and Windows Security WFP audit decisions with source-aware process attribution
- [ ] Verify live collection of Sysmon network/DNS and Security WFP records under an account with source access
- [ ] ETW/WFP/network collectors with measured impact and loss reporting
- [ ] File/Registry/service/device source adapters with explicit coverage limitations
- [ ] Workload performance and disk impact benchmarks

## M4 — Analytics
- [x] Initial retained Sysmon activity timeline grouped by exact subject GUID and recorded host, combining lifecycle/DNS/network with derived quality, provenance and combined read authorization
- [ ] Process tree/parent-child graph, additional source identity relationships and cross-source timelines with supported confidence
- [ ] Deterministic rule engine and optional AI-assisted investigation as client
- [ ] Source-visible evidence exports; acceptance tests for inference vs fact

## M5 — Clients
- [ ] Web Dashboard uses identical API paths
- [ ] OpenAPI and minimal SDKs / examples
- [ ] Separate read-only MCP Adapter with no host-control tools

## M6 — Release preparation
- [x] Self-contained Windows x64 ZIP with local first-run config/token, relative paths, no installer, verified folder relocation and persisted evidence
- [ ] Add broader packaging verification (clean Windows, different filesystems and architecture support)
- [ ] Windows 10/11 verification and source-provider compatibility
- [ ] Security, storage, privacy, dependency-license and compatibility audits

**Permanent exclusions:** no blocking, quarantining, killing, remote shell, or protection commands.
