# Point Bridge 1.2.8 security candidate

Start with INSTALL-AND-REMOVE.md and SECURITY-UPGRADE.txt. This unsigned Windows x64 candidate bundles its own .NET10 runtime. The operator has confirmed successful use of the earlier release in their Metasys environment; this security update passes automated and isolated Windows-service tests, but actual MSI upgrade/rollback and full-server reboot remain unverified. It is not a pharmacy reliability certification.

Features: single Metasys sign-in for explicitly admitted administrators; separate API service accounts; API Save/Test/Clear; Metasys COV with five-minute quiet-point reconciliation; type/unit/command validation; bounded independent mapping workers and readback; durable conservative restart recovery; external scalar JSON sources; multiple recipients; rotating logs and scrubbed local diagnostic download.

Fresh defaults contain no credentials, accounts, grants or mappings. Transfers start paused, external extensions off, remote access off, live watchdog publishing off and Metasys human authentication unconfigured. Local first-run setup bootstraps administration. Existing installations retain their ProgramData settings during MSI upgrades: pause existing transfers before upgrading.

Current limitation: Metasys source and destination must use different server origins. Different points on the same origin remain rejected; the user paused that change. Live independent Metasys alarm publishing/notification logic is not commissioned. No unit conversion, historian/backfill or exactly-once command promise is provided.

Standard uninstall retains settings. For complete application-data removal run Uninstall-PointBridge.ps1 -RemoveData from an elevated PowerShell console; -WhatIf previews it. Keep the helper outside the installed directory. It does not undo Metasys point writes, erase shared certificates/.NET installations, or clear Windows event history.

Documentation: METASYS-LOGIN.md, EXTERNAL-SOURCES.md, RECOVERY-AND-DIAGNOSTICS.md and WATCHDOG-OFFLINE.md.
