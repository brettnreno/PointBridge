# Durable delivery recovery and local diagnostics

Implemented in staged source only. No release or change to the running acceptance app is included.

## Crash recovery

`delivery-journal.json` stores one current record per mapping: hashed mapping identity, hashed source/destination/command binding, scalar value fingerprint, operation ID, intended/submitted/confirmed phase, update time, and external observation time when available. No point values, connection credentials or vendor tokens are stored. Hashes are fingerprints, not encryption; the journal remains local operational data subject to the service-data ACL and backup policy.

Before sending any write, the bridge flushes an intended record and then a submitted record. A received HTTP acknowledgment alone does not confirm delivery. Matching destination readback is required before a confirmed record is committed. An interrupted operation remains pending even if it may never have reached the server. After restart, current source quality/freshness and mapping preflight still apply; a pending value fingerprint must match reliable destination readback before the bridge resolves it and considers a newer source value. If readback differs, further writes are held with `prior-write-unconfirmed`. A previously confirmed unchanged value is checked but not replayed to overwrite drift.

External observation time is retained with the write record. A sample older than that recorded observation is rejected after restart. This is a floor for delivered operations, not a persistent historian of all polls. Metasys COV event ordering remains unproven, so notifications trigger fresh reads instead of writing event payloads. Command type/priority, endpoints/accounts, resolved objects and source interpretation are part of the binding. A changed binding holds the mapping with `journal-config-changed` instead of applying an old operation to a new target. Renaming a mapping alone does not change its binding.

Updates use a same-directory temporary file, WriteThrough plus Flush(true), and atomic replacement. A checksum and version detect malformed/corrupt records; an incomplete temporary file causes a hold rather than a fallback to possibly older state. An exclusive local file lease prevents concurrent bridge instances using the same data directory. Storage failure or a corrupt journal holds writes with a controlled diagnostic. No automatic journal reset, silent deletion, or pending-record eviction is provided.

The journal is bounded to 1 MiB and at most4096 records (the byte limit may be reached first). Removed mapping records remain to prevent accidental identity reuse; reaching a limit holds new writes for review. It is current recovery state, not a growing per-transfer archive. Preserve it with the data directory across updates. Deleting it, restoring an older copy, losing the disk, or storage that does not honor flush/atomic replacement can lose recovery evidence. Do not delete it to clear an error. Rebinding, unresolved delivery and corruption need explicit operator review of the actual destination and configuration before any recovery/reset procedure.

This does not guarantee exactly-once commands. A matching value can be produced by another writer; present-value readback does not prove ownership of a priority slot. A command or lower-priority value held by another command can remain unresolved. The tool does not automatically override it. Physical power-loss guarantees depend on the filesystem/storage; synchronous disk operations can themselves stall, which must be covered by the independent watchdog. Simulated crash windows and restart tests do not replace commissioning actual service/host failure behavior.

## Troubleshooting download

Signed-in users can select **Download diagnostics** in the header. `GET /api/diagnostics/export` returns a local JSON attachment, with no upload and no server-side saved export. It includes the executing assembly SHA256/version, runtime/config schema, counts and transfer settings, journal availability/pending counts, pseudonymous mapping health/times/counters, and up to100 allowlisted activity entries.

It excludes connection addresses, usernames, point names/references/values, credentials, tokens, cookies, auth headers, arbitrary log/error text, vendor response bodies, raw files and the journal's operation/value fingerprints. Unknown codes/text are withheld. Raw security audit lines are not exported because they contain actor/route identifiers. Local audit files remain under their existing rotation policy. Exports have at most256 health entries and a512 KiB hard cap; truncation is explicit. A failed size check produces no download. Browser/API cache policy is no-store. Treat exports as operational metadata despite these exclusions.

Synthetic secret-canary tests cover arbitrary settings and messages, encrypted test API secrets, vendor token strings and HTTP output. The HTTP test verifies authentication, attachment/no-store headers, size cap and zero point writes. Build SHA256 identifies the exact executing assembly even if its version label is reused during development.

## Remaining commissioning boundary

Live independent Metasys watchdog publishing and notifications are still disabled/unconfigured. Real target object schemas, write/priority/readback semantics, coherent multi-point health commits, supervisory node logic and notification receipt must be commissioned. Logs/downloads cannot alert when the app or host is dead. No customer access, live point writes or pharmacy safety certification is implied by these local tests.
