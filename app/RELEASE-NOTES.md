# Release 1.2.3 — production-test candidate

Adds Metasys 12 API v4 destination selection for the default and additional recipients. Metasys sources remain API v5/v6. API v4 reference lookup, unfiltered attribute schemas, explicit modify permissions and scalar command invocation permissions are handled separately. Prioritized points require a supported adjustment command rather than an implicit attribute write.

API clients refresh expiring bearer tokens before using password authentication again, serialize concurrent renewal, reject invalid token lifetimes, and do not retry credentials on server outages or certificate failures. All versions require explicit command invocation and modify permissions. Normal certificate validation, protected saved credentials, transfer pause controls and write/readback recovery remain enabled. Certificate failures provide safe certificate trust/hostname guidance.

Pause transfers before upgrading. Install over 1.2.2 or earlier supported versions without uninstalling; ProgramData configuration and recovery accounts are retained. Choose API v4 on the Metasys 12 recipient, save/test it, and revalidate mappings before starting transfers. Builds are unsigned and live customer compatibility remains uncommissioned.

The mapping browser now navigates objects, network devices, equipment, spaces and mapped equipment points. It resolves the underlying point rather than the equipment instance ID, supports numbered collection pages, and reads up to 50 visible point values in one read-only batch. Individual failures, quality and priority remain visible. Returned URLs are never blindly followed.

The read-only point inspector is available from a browser point or Activity. It shows current value/type/units/quality/priority, discovered commands, trends, alarms and redacted audits. It supports v4 and v5/v6 trend formats, bounded time ranges, and safe pagination. It cannot acknowledge, discard, annotate or command anything. Audit usernames, free text, parameters and old/new values are omitted. See METASYS-EXPLORER.md for operating limits. No full SDK dependency or insecure stream transport is added. No live customer requests or point writes are performed by the release tests.

## Release 1.2.2 history

Fixes Metasys 13 local-user authentication when private v6 userinfo returns HTTP 404. The same verified HTTPS origin and bearer session are used to request private v5 userinfo. Both responses require the same canonical profile and identity validation. Authentication failures, invalid profiles, redirects and TLS failures do not trigger fallback. No password login is retried and no certificate validation is bypassed.

The customer-run diagnostic confirmed a valid Metasys 13 profile at the v5 route with a matching canonical username and profile ID. An absent optional account status remains unknown; it is not asserted to be Active. Regression coverage exercises v6/v5 selection, rejected profiles, authentication failure, v5 login/session refresh/logout, and one password login per attempt. Verification errors now expose safe stage-specific diagnostic codes without credentials or raw responses.

The MSI version is incremented to upgrade existing 1.2.1 installations while retaining ProgramData. Pause transfers before upgrading. Existing local recovery accounts remain available.

## Release 1.2.1 history

First-run setup verifies a Metasys login against the local server and enables all valid Metasys users with Administrator access. No separate local account, user enrollment, manual identity IDs, JSON editing or service restart is required. Existing local-account installations use User accounts → Configure Metasys sign-in. Passwords and vendor tokens are not saved with settings. Server changes require fresh verification; local recovery remains available.

The MSI requires the local Metasys MIIIDM core service, includes the Signature table needed by AppSearch, and configures recovery with a checked elevated deferred sc.exe action instead of the failing native MsiConfigureServices action. RC2 accepts older Metasys installations without the newer UI Assets registry entry; UI authentication is verified separately during setup. Build checks cover core service present without UI registration, missing Metasys core services, and maintenance after Metasys removal. Version 1.2.1 upgrades 1.2.0 while preserving ProgramData.

The setup HTTP suite covers initial installation-code/CSRF protection, rejected credentials and remote origins, first-user setup, a second user without enrollment, disabled/substituted identities, persistence, immediate revocation, recovery login, and no point writes. Target Metasys versions and actual field operation still require commissioning.

## Release 1.2.0 history

Includes the staged authentication, connection-test/clear, per-mapping reliability, five-minute fallback verification, durable delivery recovery, scrubbed diagnostics, external JSON and multiple-recipient work. A fresh configured Metasys sign-in provider takes precedence over the optional local first-run setup screen. All explicitly admitted users have Administrator access.

The complete-removal helper supports standard-path MSI/manual installations and optional permanent application-data purge. Standard MSI removal preserves ProgramData. Preview, fixed-path, MSI identity and reparse-point protections are included; no uninstall has been executed on metasys-lab.

The self-contained Windows x64 runtime is .NET10.0.13. It does not require installing/upgrading the server's shared .NET6 framework. Build and packages are unsigned.

Verification reports accompany the release. Previous completed suites:60 backend groups,74 adapter assertions,83 authentication/API HTTP assertions,52 extension HTTP assertions and20 diagnostic HTTP assertions. Browser download completion remains unknown after a tool failure; endpoint download/redaction tests passed. Package cabinet hashes and native service/uninstall tables are verified without executing the installer.

Known limitations: same-server-origin Metasys mappings remain rejected; live Metasys watchdog/alarm publishing is disabled/uncommissioned; actual customer versions/OS/service TLS/permissions and real point writes/readback are unverified. No automatic command-priority override, unit conversion, outage history backfill or exactly-once guarantee. Read INSTALL-AND-REMOVE.md before testing.

The user's request to build this test release supersedes the earlier packaging hold. It does not authorize agent deployment or real writes to customer systems.
