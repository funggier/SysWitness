# ADR-0001 — Observation-only Core and Data API

Date: 2026-11-20
Status: Accepted

## Context
The user wants an extremely detailed Windows-first system observation service with an API central enough to be useful to their future apps and AI/MCP tools. Protection and control are separate concerns and must not be delegated to this system.

## Decision
SysWitness collects, stores, searches, correlates, reports, and exports **data only**. The public API and MCP adapters cannot configure monitoring, alter the system, or call protection tools. Local setup for granting Windows source access is distinct from read-only API runtime. A separate future protection app has its own execution privileges, policy, and control tools and may consult SysWitness as an independent observer.

## Consequences
- Easier audit and lower risk of AI/tools abusing OS privileges.
- UI and MCP can be replaced without affecting data collection.
- Collector source permissions/setup remain an operational requirement, not a client feature.
- Data read-only does not mean disclosure-free: auth, scoping and privacy remain mandatory.
- No guarantee of total host visibility; coverage and losses are first-class data.
