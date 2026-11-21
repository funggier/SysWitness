# Third-party notices and release audit

SysWitness application code and project documentation: **Copyright (c) 2026 Kittiwat Kitikun. All Rights Reserved.**

The dependencies below are **not** relicensed under the SysWitness proprietary notice. Each retains its own upstream terms:

| Package | Pinned version | Upstream package license |
|---|---:|---|
| System.Diagnostics.EventLog | 10.0.0 | MIT |
| System.Management | 10.0.12 | MIT |
| System.CodeDom | 10.0.12 (transitive) | MIT |
| Microsoft.Data.Sqlite | 10.0.12 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 (transitive) | Apache-2.0 |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 (transitive) | Apache-2.0 |

The Windows self-contained portable ZIP also embeds the Microsoft .NET Runtime and ships `DOTNET_RUNTIME_LICENSE.txt` and `DOTNET_RUNTIME_THIRD_PARTY_NOTICES.txt` copied from the .NET distribution on the Windows build host. The .NET MIT license applies to the WMI packages, and their packaged notice files are copied to `System.Management-THIRD-PARTY-NOTICES.txt` and `System.CodeDom-THIRD-PARTY-NOTICES.txt`. The Apache-2.0 license text is included as `APACHE-2.0-LICENSE.txt` in the ZIP. The application-owned source remains All Rights Reserved; upstream licenses continue to apply to their respective components.

The exact transitive dependency graph must be reviewed before every binary release. This list documents known direct/transitive packages and redistribution notices; it is not a substitute for verifying every redistributed component and any required upstream NOTICE files.

License metadata was checked in local NuGet manifests during November 2026, including SQLitePCLRaw's Apache-2.0 package metadata. All Rights Reserved governs the original SysWitness contributions; it does not remove rights granted by third-party licenses.
