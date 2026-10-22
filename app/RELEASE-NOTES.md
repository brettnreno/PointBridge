# Release 1.2.0 — production-test candidate

Includes the staged authentication, connection-test/clear, per-mapping reliability, five-minute fallback verification, durable delivery recovery, scrubbed diagnostics, external JSON and multiple-recipient work. A fresh configured Metasys sign-in provider takes precedence over the optional local first-run setup screen. All explicitly admitted users have Administrator access.

The complete-removal helper supports standard-path MSI/manual installations and optional permanent application-data purge. Standard MSI removal preserves ProgramData. Preview, fixed-path, MSI identity and reparse-point protections are included; no uninstall has been executed on metasys-lab.

The self-contained Windows x64 runtime is .NET10.0.13. It does not require installing/upgrading the server's shared .NET6 framework. Build and packages are unsigned.

Verification reports accompany the release. Previous completed suites:60 backend groups,74 adapter assertions,83 authentication/API HTTP assertions,52 extension HTTP assertions and20 diagnostic HTTP assertions. Browser download completion remains unknown after a tool failure; endpoint download/redaction tests passed. Package cabinet hashes and native service/uninstall tables are verified without executing the installer.

Known limitations: same-server-origin Metasys mappings remain rejected; live Metasys watchdog/alarm publishing is disabled/uncommissioned; actual customer versions/OS/service TLS/permissions and real point writes/readback are unverified. No automatic command-priority override, unit conversion, outage history backfill or exactly-once guarantee. Read INSTALL-AND-REMOVE.md before testing.

The user's request to build this test release supersedes the earlier packaging hold. It does not authorize agent deployment or real writes to customer systems.
