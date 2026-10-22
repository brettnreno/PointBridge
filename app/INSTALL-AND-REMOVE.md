# Point Bridge 1.2.0 production-test candidate

Unsigned Windows x64 test build with its own .NET10 runtime. It does not replace the server's installed .NET6 or Metasys runtime. The MSI requires Windows Server2016 or newer; actual customer OS, certificates, Metasys versions, service permissions and antivirus/application-control compatibility remain unverified.

## Install and initial test

1. Keep this release folder, including Uninstall-PointBridge.ps1, outside Program Files so the removal helper remains available after MSI uninstall. Do not copy previous lab data or keys into the installation.
2. Install MetasysPointBridge-1.2.0-production-test-x64.msi as an administrator. It installs the MetasysPointBridge Windows service under LocalService, configures service restart actions and starts the local console. An elevated deferred custom action runs Windows System32 sc.exe to configure restart actions and fails installation if configuration fails. The installer does not change Metasys objects or the shared .NET installation.
3. Open http://127.0.0.1:5088/ on that server. A new installation has empty connections/mappings, transfers paused, external extensions off, Metasys human authentication disabled and no admitted users. Complete initial administrator setup using the local setup-code.txt in %ProgramData%\MetasysPointBridge. Metasys sign-in can then be configured as described in METASYS-LOGIN.md. API service accounts are separate from human sign-in.
4. Save/Test the API connections, enter a noncritical mapping and Check points & permissions before explicitly starting transfers. API Test proves authentication, not actual point writes. Use normal HTTPS certificate validation; do not bypass trust failures.
5. Confirm readback, a quiet five-minute source/readback check and restart behavior on the selected point. Watch Activity and Download diagnostics. Investigate a journal hold instead of deleting the journal or repeatedly retrying writes.

Do not upgrade an existing active configured installation for an initial test: existing ProgramData settings are deliberately retained by MSI upgrades, and previously armed mappings can resume. Pause transfers in the existing console before an upgrade and keep a protected backup. A fresh installation defaults paused; an upgrade is not a forced reset of operator settings.

Known limitations: same-origin Metasys-to-Metasys mappings are currently rejected, including different points on one server. The user paused that fix before this package was requested. External scalar JSON sources and multiple distinct recipients are optional. No numerical unit conversion, intermediate-reading historian/backfill or exactly-once command guarantee is provided. Live independent Metasys watchdog publishing/notification logic is still unconfigured; local logs cannot alert after host failure. This build is for controlled testing, not a pharmacy reliability certification.

## Standard uninstall (keep settings)

Use Windows Installed Apps / Programs and Features -> Metasys Point Bridge -> Uninstall. Windows Installer stops/deletes the service, removes package files and the shortcut. ProgramData is retained so reinstalling does not destroy credentials, mappings or recovery evidence. An optional HTTPS firewall rule created by Configure-Https.ps1 is cleaned up by the helper below.

## Complete removal, including saved logins

From an elevated 64-bit PowerShell window in the retained release folder:

```powershell
.\Uninstall-PointBridge.ps1 -RemoveData
```

Review its one confirmation. It removes the service/MSI, standard Program Files application directory, the exact MetasysPointBridge-HTTPS firewall rule and the application's event source. -RemoveData permanently erases the standard %ProgramData%\MetasysPointBridge directory: API credentials, local accounts, DPAPI keys, settings, mappings, recovery journal and application logs. This is not reversible unless you retained a separate backup. Without -RemoveData, settings remain.

Preview without changing the host:

```powershell
.\Uninstall-PointBridge.ps1 -RemoveData -WhatIf
```

The helper refuses a service outside the standard installation path, mismatched MSI identity, and junction/symbolic-link cleanup targets. It stops if MSI removal fails or requires reboot, preserving data until removal completes. If deletion is pending, close Services consoles or restart and rerun. Custom installation/data locations are not automatically purged. Retained exports/backups and the release folder must be removed separately if desired.

It does not delete Metasys points, extensions, trends, shared certificates/private-key permissions, shared .NET runtimes or existing Windows event history. A copied destination point can retain its last value after the bridge stops; review that point and any command priority through normal Metasys administration. Do not assume uninstall releases a command slot or reverses point writes.

## Verification boundary

Build, fake-transport regressions, package cabinet hashes, MSI service stop/delete tables and uninstall preview/path checks are verified locally. Installing/uninstalling this MSI has not been executed on metasys-lab, to preserve the running lab and Metasys installation. Actual target-host install/remove behavior remains an acceptance check.
