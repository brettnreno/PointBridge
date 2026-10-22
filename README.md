# Point Bridge

Point Bridge is a Windows service with a local web console for copying scalar data into Metasys points. It supports Metasys REST API v5/v6 source and recipient connections, optional read-only JSON API sources, and multiple recipient servers. The repository branch is **master**.

Current version: **1.2.1 production-test candidate**. Builds are unsigned. Controlled local/fake-endpoint tests pass; customer installation, actual point behavior and independent Metasys notifications still need commissioning. It is not a pharmacy safety certification or a replacement for independent monitoring.

## What it does

- First-run setup verifies a login against Metasys installed on this computer and enables sign-in for all valid Metasys users. Every authenticated user has Administrator access. API service-account credentials remain separate from human sign-in. Existing installations configure this under User accounts.
- Connection Save, Test and Clear credentials; point discovery/reference entry; type, unit, range, writability and supported-command/priority validation.
- Metasys COV notifications trigger fresh source reads. Independent periodic source/readback checks default to300 seconds, including points whose values remain constant.
- Per-mapping workers, bounded shared concurrency/deadlines, quality checks, destination readback, and conservative handling of uncertain writes.
- A bounded durable journal records operation fingerprints and phases across restart. It never blindly replays an uncertain write; readback must resolve it first.
- Optional timestamped scalar JSON sources with header/bearer authentication and shared polling across recipients. No numerical unit conversion is performed.
- Rotating application/audit logs, health endpoints and a scrubbed local diagnostic download.

Fresh installations have no users, connections, passwords, grants or mappings. Transfers, remote access, external extensions and live health publishing start disabled. Existing ProgramData settings survive upgrades, including the operator's armed/paused choice: **pause an existing installation before upgrading**.

## Requirements

### Build machine

- 64-bit Windows, PowerShell7, Git, a stable .NET10 SDK, and Node.js for HTTP/JavaScript tests.
- `global.json` selects the .NET10 SDK family with feature-band roll-forward. GitHub Actions installs the current stable10.0.x SDK. No .NET8 SDK is required.
- Package restore needs NuGet access on the build machine. Windows Installer COM, makecab.exe and expand.exe are used to author and verify the MSI; WiX is not required.

### Target machine

- Windows Server2016 or later, x64. See [Microsoft's Windows/.NET compatibility table](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
- The MSI bundles its own .NET10 runtime. It does **not** require replacing the shared .NET6/Metasys runtime already on the server. The exact bundled patch is recorded in apiTool.runtimeconfig.json.
- Appropriate Metasys API licensing/permissions and normal HTTPS trust. API authentication success does not prove permission to write a particular point or command.
- Elevated rights for service installation/removal. Service configuration/data are local to the target computer.

## Clone and build

After configuring your GitHub SSH key:

```powershell
git clone git@github.com:brettnreno/PointBridge.git
Set-Location PointBridge
git switch master
pwsh -NoProfile -File .\scripts\Build.ps1
```

Build.ps1 runs the fake-only tests, publishes the self-contained Windows x64 executable, copies operator documentation/helpers, authors an MSI, verifies its cabinet, and creates a bundle with SHA256 checksums. It does not install or start a service, configure Metasys, or perform live transfers.

Outputs:

| Path | Contents |
| --- | --- |
| artifacts/packages/ | MSI, bundle ZIP, manifest, checksums, uninstall helper and install/remove instructions |
| artifacts/runtime/ | Self-contained executable/runtime and web assets |
| artifacts/logs/ | Build/test output and MSI authoring result |

To package again after a completed test run: `pwsh -File .\scripts\Build.ps1 -SkipTests`. Use `-NoRestore` only after restoring all projects and the Windows runtime target into the package cache. Do not commit artifacts or runtime/data directories.

## Run from source

Use an isolated development data directory so production settings cannot be picked up accidentally:

```powershell
$env:METASYS_BRIDGE_DATA = Join-Path $PWD 'work\dev-data'
dotnet run --project .\app\apiTool.csproj
```

Open **http://127.0.0.1:5088/** on that computer. Leave the console open; Ctrl+C stops this development instance. A fresh deployment asks for initial local administrator setup. The setup code is in setup-code.txt in the selected data directory; read it locally, never commit it. Configuring an admitted Metasys provider changes the normal sign-in form to Metasys authentication. Local recovery authentication remains a maintenance interface, without an everyday provider selector.

Default service data: `%ProgramData%\MetasysPointBridge`. Configuration, saved API credentials, accounts, DPAPI keys, journal and logs are separate from immutable Program Files binaries. Do not copy keys/data from the development machine to a customer system.

## Install the Windows service

1. Keep the bundle's uninstall helper outside Program Files.
2. Install the MSI as an administrator. It installs/starts the MetasysPointBridge service under LocalService, with restart actions and a loopback console shortcut.
3. Open http://127.0.0.1:5088/ and complete setup using the installation code, local Metasys HTTPS address and a verified Metasys login. All valid users on that server can then sign in with Administrator access. Existing local-account installations use User accounts → Configure Metasys sign-in.
4. In Admin / Connections configure and test source/destination API accounts. Credentials entered for Test are not saved unless you select Save.
5. Configure a noncritical mapping and use Check points & permissions. Verify type/units, command semantics and priority, then explicitly start transfers only for the approved target.
6. Verify actual readback, quiet-period reconciliation, restart behavior and the independent failure alarm before relying on copied trends/values.

The optional Install-Service.ps1 is an alternative for manually deployed runtime files; do not install the same service twice. Configure-Https.ps1 can configure a trusted server certificate and optionally a named inbound firewall rule. Remote access defaults off. Do not bypass certificate validation to resolve TLS failures.

## Metasys sign-in

See [app/METASYS-LOGIN.md](app/METASYS-LOGIN.md). First-run setup and User accounts configure the local Metasys server in the app, verify a login, and enable all valid Metasys users with Administrator access. No JSON editing or restart is required. The installer requires local Metasys core services and Metasys UI registration. Server addresses configured in the app must resolve to this computer.

The authentication adapter uses the private Metasys UI JSON contract observed on a Metasys16 lab server. It is not represented as a supported public vendor login API. Different versions and account-state policies require target-side verification. Passwords are not saved by this provider; tokens stay in bounded server-side sessions and never enter browser cookies or logs. A provider failure does not silently fall back to local password checking.

## Mapping and delivery behavior

COV is an invalidation signal: the app rereads current source state rather than trusting event payload values or unproven event ordering. A quiet point still receives an independent five-minute source/readback check; other points' events cannot postpone it. Checks are staggered and concurrency is bounded. Stable healthy values do not cause repeated writes.

Writes use the selected validated attribute/command and declared priority. Matching readback confirms the destination; a higher-priority command or changed destination is diagnosed rather than blindly overwritten. Before sending, the recovery journal flushes intended/submitted fingerprints; only matching readback resolves pending delivery. Source observation ordering/freshness applies to external samples after restart. Disk/journal corruption, changed binding or uncertain delivery holds writes for review. Do not delete the journal to clear an error.

This is **latest-value replication**, not a historian. Intermediate changes during an outage are not backfilled. A destination trend may continue recording a held value during a fault. API contact alone does not prove a physical sensor has a fresh measurement. There is no exactly-once command or priority-slot ownership guarantee.

Current restriction: Metasys source and destination **must use different server origins**. Different points on the same origin still fail preflight. Do not use alternate DNS/IP aliases to evade that restriction.

## External JSON sources and fan-out

See [app/EXTERNAL-SOURCES.md](app/EXTERNAL-SOURCES.md). Explicitly enable extensions, save a disabled HTTPS GET source with value/timestamp/unit paths, expected units/type, polling and maximum age, and test it read-only. Enable reviewed sources and create a distinct mapping per recipient. A configured field source shares requests across its recipients; each recipient has independent readback/failure status.

The weather.gov sample is disabled and incomplete by design. Select a station, identifying User-Agent, matching destination units and acceptable observation age locally. The NWS can publish delayed observations; no acceptable pharmacy age is assumed. Header API keys/bearer secrets are protected locally. No OAuth acquisition/refresh, arbitrary scripts, numerical conversions or paid API calls are built into the sample.

## Logs, diagnostics and independent alerts

Activity files rotate at approximately2 MiB with two backups and keep200 in-memory entries. Audit files rotate at approximately5 MB with one previous file. Successful transfers are not logged individually forever; controlled failure/recovery transitions and counters provide troubleshooting detail. Windows event-history retention and any operator-redirected console files follow their own policies.

Download diagnostics produces a local authenticated JSON attachment, capped at512 KiB, with build hash, config counts, journal state and allowlisted health/log fields. It excludes point values/names/references, addresses, usernames, credentials, tokens, headers, cookies, raw responses and arbitrary messages. There is no automatic upload. The HTTP export/redaction tests pass; automated browser download completion was not verified after a browser-tool failure.

See [app/RECOVERY-AND-DIAGNOSTICS.md](app/RECOVERY-AND-DIAGNOSTICS.md) and [app/WATCHDOG-OFFLINE.md](app/WATCHDOG-OFFLINE.md). Independent watchdog logic is simulated/tested; **live Metasys health publishing and notification logic remain uncommissioned**. In-app logs cannot alert when the app/host is dead. Commission native health points, an independent supervisory node and the existing Metasys notification route. An advancing process heartbeat alone must not mask stalled transfer workers.

## Tests

```powershell
pwsh -NoProfile -File .\scripts\Test.ps1
```

The suites link the real staged code and use in-memory HTTP transports or loopback fake endpoints. No Metasys credentials, customer access or paid APIs are required. The script starts only its own fake server on port15089 and stops that exact process afterward. Synthetic data goes into a unique temporary directory, outside uploaded artifacts.

Coverage includes backend v5/v6 contracts, preflight/priority, COV/reconnect, quiet-point checks, stalled/slow workers, partial failures, readback/quality/drift, uncertain writes and crash windows, corrupt/incomplete/disk-full journal, identity substitution/revocation/outages, JSON rate/time/size/freshness errors, migration, fan-out and export secret canaries. Passing simulations do not certify actual customer point behavior.

## GitHub Actions

[.github/workflows/build.yml](.github/workflows/build.yml) runs on pushes to master, pull requests targeting master and manual dispatch. A Windows runner installs .NET10, tests, publishes and authors the MSI. Artifacts are retained30 days; build/synthetic-test logs14 days. Workflow permissions are read-only and no repository secrets are required. It does not deploy, install, activate transfers, connect to customer servers, publish GitHub Releases or use SSH keys.

After a successful run, open Actions -> Build and test Point Bridge -> the run -> Artifacts. Download the Windows package and inspect checksums/instructions. Hosted Actions execution must be verified after the workflow is pushed; local validation alone does not mean GitHub has run it.

## Completely uninstall

Standard Windows Installed Apps uninstall stops/deletes the service and removes binaries/shortcut while retaining ProgramData. For complete default-path application-data removal, run the retained helper from an elevated64-bit PowerShell window:

```powershell
.\Uninstall-PointBridge.ps1 -RemoveData -WhatIf
.\Uninstall-PointBridge.ps1 -RemoveData
```

The second command permanently removes saved API credentials, local accounts/keys, settings, mappings, journal and application logs, after one confirmation. The helper also removes the exact owned HTTPS firewall rule and event source. It refuses unexpected service/version/MSI identities and junction/symbolic-link cleanup paths. A failed removal or pending reboot preserves data until resolved. Custom locations and separately retained backups/exports are not automatically purged.

It does not delete Metasys points/trends, release command priorities, reverse past writes, remove shared certificates/private-key permissions, uninstall shared .NET, or clear Windows event history. Review destination state through normal Metasys administration. Installer/uninstaller execution on a customer host remains an acceptance check; local verification inspected MSI tables/cabinet and helper guards without installing it on the lab server.

## Repository hygiene

Keep development on master as requested. Never commit ProgramData, websettings.json, config.json, users.json, setup codes, logs, private keys/certificates, DPAPI keys, production exports or credentials. .gitignore excludes common mutable/build files; it is not a substitute for reviewing git diff --cached before pushing. Synthetic fixture passwords in tests are deliberately fake. No license has been assigned by this repository preparation.
