# Release 1.2.1 — production-test candidate

First-run setup verifies a Metasys login against the local server and enables all valid Metasys users with Administrator access. No separate local account, user enrollment, manual identity IDs, JSON editing or service restart is required. Existing local-account installations use User accounts → Configure Metasys sign-in. Passwords and vendor tokens are not saved with settings. Server changes require fresh verification; local recovery remains available.

The MSI requires local Metasys core and UI registration, includes the Signature table needed by AppSearch, and configures recovery with a checked elevated deferred sc.exe action instead of the failing native MsiConfigureServices action. Version 1.2.1 upgrades 1.2.0 while preserving ProgramData.

The setup HTTP suite covers initial installation-code/CSRF protection, rejected credentials and remote origins, first-user setup, a second user without enrollment, disabled/substituted identities, persistence, immediate revocation, recovery login, and no point writes. Target Metasys versions and actual field operation still require commissioning.

## Release 1.2.0 history

Includes the staged authentication, connection-test/clear, per-mapping reliability, five-minute fallback verification, durable delivery recovery, scrubbed diagnostics, external JSON and multiple-recipient work. A fresh configured Metasys sign-in provider takes precedence over the optional local first-run setup screen. All explicitly admitted users have Administrator access.

The complete-removal helper supports standard-path MSI/manual installations and optional permanent application-data purge. Standard MSI removal preserves ProgramData. Preview, fixed-path, MSI identity and reparse-point protections are included; no uninstall has been executed on metasys-lab.

The self-contained Windows x64 runtime is .NET10.0.13. It does not require installing/upgrading the server's shared .NET6 framework. Build and packages are unsigned.

Verification reports accompany the release. Previous completed suites:60 backend groups,74 adapter assertions,83 authentication/API HTTP assertions,52 extension HTTP assertions and20 diagnostic HTTP assertions. Browser download completion remains unknown after a tool failure; endpoint download/redaction tests passed. Package cabinet hashes and native service/uninstall tables are verified without executing the installer.

Known limitations: same-server-origin Metasys mappings remain rejected; live Metasys watchdog/alarm publishing is disabled/uncommissioned; actual customer versions/OS/service TLS/permissions and real point writes/readback are unverified. No automatic command-priority override, unit conversion, outage history backfill or exactly-once guarantee. Read INSTALL-AND-REMOVE.md before testing.

The user's request to build this test release supersedes the earlier packaging hold. It does not authorize agent deployment or real writes to customer systems.
