# Development Roadmap

## M0 — Repository and architecture baseline
- [x] Establish name SysWitness, owner and all-rights-reserved license
- [x] Create public GitHub repository
- [x] Write Thai product specification and architecture decision
- [x] Define documented API, evidence, security and milestone boundaries
- [ ] Verify initial commit published

**Gate:** published documentation is coherent and no private telemetry or secrets are committed.

## M1 — Minimal vertical slice
- [ ] .NET solution and modular projects
- [ ] Read one safe Windows Event Log channel with proper error/coverage handling
- [ ] Normalize and persist actual events to SQLite using stable event identity
- [ ] Serve authenticated localhost-only /api/v1 event query and status
- [ ] Test events, paging, input validation, and no action endpoints
- [ ] Demonstrate restart/replay and document source permissions

**Gate:** observed source event -> evidence -> query response -> tests on real Windows without requiring Dashboard/MCP.

## M2 — Operational hardening
- [ ] Native-source bookmarks, retention, quota, resilience, backpressure and metrics
- [ ] Separate collection privilege boundary, local setup, Health/Coverage views
- [ ] Per-client read scopes, redaction, rotation, audit and misuse tests

## M3 — Deep telemetry
- [ ] Pluggable Sysmon reader and identity link
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
