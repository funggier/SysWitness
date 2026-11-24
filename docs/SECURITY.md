# Security, Privacy and Non-Goals

## Threat model
SysWitness sees sensitive host information: accounts, paths, command lines, DNS and endpoint addresses, potentially embedded credentials. Risks include unauthorized reading, remote exposure, prompt injection via logs, sensitive exports, compromised collectors, and evidence tampering.

## Minimum required controls
- API binds only to 127.0.0.1 by default; do not assume localhost is trusted.
- Access to process paths and command lines requires `processes.read` in the local client registry. Existing clients and the legacy token keep their previous permissions.
- The combined Process Activity endpoint requires both `processes.read` and `network.read`; neither scope alone may expose the other category. Computer is a required exact-match filter, not a per-host authorization policy. Raw XML remains private. Existing single-scope routes keep their permissions.
- Authenticate data clients and authorize scope per client (currently category-level event summaries, coverage, storage and process details). Rate limits and query-duration enforcement remain future hardening work.
- Protect data files with restrictive filesystem ACLs; separate API/runtime permissions from elevated collection.
- Store no API tokens in Git or client-side source. `SYSWITNESS_CLIENT_KEYS_FILE` stores hashes, not raw credentials; protect the local file and use strong random client keys. Revocation/rotation currently requires changing the file and restarting SysWitness.
- Do not feed raw evidence to cloud AI without explicit consent and minimization.
- Treat event bodies as untrusted strings, never instructions to AI.
- No debug endpoints invoking PowerShell, SQL, OS commands or executable plugins.
- Do not grant AI privileged operating system access through the Monitoring service.
- Document audit source limitations and keep setup elevated only when explicitly required.
- Secure transport + explicit authorization if remote access is added.
- Validate every collected input field, retain raw origin, and sanitize output for HTML/JSON clients.

## No collection of passwords
No keylogger, credential harvesting, clipboard scraping, browser secret extraction or unrequested plaintext application content. Packet capture (if ever implemented) must be explicitly opt-in and privacy-budgeted.

## Licensing
Public repository, copyright (c) 2026 Kittiwat Kitikun, All Rights Reserved. Dependencies have their own licenses; audit their terms before distributing executables.

## Responsible vulnerability reporting
Avoid posting private host logs, tokens or real credentials in public issues. Contact repository owner privately via GitHub profile for sensitive reports.
