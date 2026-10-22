# Metasys sign-in — 1.2.0 production-test candidate

One ordinary username/password form uses the configured Metasys authentication server when enabled. Every explicitly admitted canonical identity receives Administrator access. There is no Operator tier or automatic admission of every Metasys user. A failed Metasys login never silently falls back to local password validation. Local maintenance authentication is retained; there is no daily provider selector.

Shipping defaults disable this provider and contain no server origin or grants. Initial local administrator setup is available on a fresh installation. Human sign-in and source/destination API service-account credentials are independent; API permissions are still required for actual reading/writing.

Merge the MetasysLogin section from metasys-login.disabled.example.json into the local ProgramData websettings.json. Set the actual HTTPS authentication origin and explicit canonical Username, StorageId, ProfileId and ServerOrigin for each admitted user, then enable the provider and restart the Point Bridge service. Role is Admin. Do not guess IDs or reuse lab grants. Establish canonical identity locally on the actual Metasys version before admission; do not put user passwords in configuration or chat.

The adapter uses the private Metasys UI JSON challenge/login, canonical /userinfo, refresh and logout contract observed on the metasys-lab Metasys16 lab. This is not a claimed supported public vendor authentication API. Other versions and service-account TLS context must be verified on the target server. Explicit disabled/locked/expired status is denied; an absent optional status is unknown. Refresh success alone does not establish every account policy. Terms acceptance is handled by the user through the normal Metasys UI.

Passwords are used for the login operation and are not saved by this provider. Remote tokens live only in bounded server-side memory sessions; browser cookies contain no vendor token. Canonical identity and configured origin/grant binding are revalidated. Provider failures revoke external sessions rather than silently granting access. Logout removes the local session and attempts remote cleanup.

Default deployment is normal paused operation, not the lab-only Acceptance mode. Do not copy lab websettings.json, credential files, encryption keys or acceptance directories to a customer server. See INSTALL-AND-REMOVE.md and RECOVERY-AND-DIAGNOSTICS.md.
