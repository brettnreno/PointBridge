# Point discovery and read-only inspection

## Mapping browser

Use Browse source or Browse destination in a mapping. Select Object hierarchy, Network devices, Equipment or Spaces. Open child devices/objects/spaces, equipment belonging to a space, and points mapped to equipment. Equipment entries are containers: only the underlying mapped object's GUID is selected as a point. Non-presentValue equipment mappings cannot be selected. Additional recipients use their own connection, API version and credentials.

Search filters the current page only. Numbered collections show 50 entries per page; use Next/Previous. Object hierarchy APIs have no numbered pages: a branch is limited to 500 objects, with a truncation notice. Open narrower branches or paste an exact point reference when required. No returned foreign URLs are followed.

Read visible values explicitly reads up to 50 visible selectable points with one `POST /objects/batch` containing only `method: GET` and presentValue targets. It does not arm transfers, write, change mappings or start background polling. Per-point failures do not become zeros or success. Returned quality and priority are shown separately. Unknown quality remains Unknown. This snapshot is not proof of physical observation freshness.

## Point inspector

Click Inspect beside a discovered point, or Activity → Inspect Metasys point. Select a configured connection and point GUID/reference, then select Current value, Trend samples, Alarms or Audits. Use Read from Metasys after changing the selection. The time ranges are one hour, 24 hours or seven days. All times sent to Metasys are UTC.

Current value shows type, units, reported reliability, priority and discovered commands without invoking them. Trends normalize the different API v4 versus v5/v6 formats. API v4 trend pages and activity continuation tokens are supported; API v5/v6 trend endpoints have no numbered paging, so narrow the time range when more than 50 samples are returned. If an activity response exceeds 50 records, the inspector truncates and disables Next rather than skipping undisplayed records. Narrow the range to inspect those records. Normal continuation pages pin the same point, time range and activity type; foreign continuation links are rejected.

The inspector has no acknowledge, discard, annotate or command controls/endpoints. Audit usernames, descriptions/free text, parameters, signatures and old/new values are not returned to the browser. It is not a full audit export or an independent alarm notifier. Current API connection settings are never changed by inspection. Point Bridge diagnostics exports remain separate and scrubbed.

## Access and transport

Sign-in and Administrator access are required. Batch reads also require the session CSRF token. Only saved Metasys connections can be selected: no arbitrary URL proxy is exposed. Each browser/inspector operation has a 25-second aggregate deadline; API JSON is limited to 4 MiB and bounded body-read time. Choose a shorter time range if a large history exceeds that limit. The Windows service's API account must have the applicable licensing and read permissions; human UI sign-in does not grant API account permissions.

TLS certificate validation stays enabled. Certificate failures require the correct certificate hostname, a valid certificate and a trusted issuing CA in the Local Computer trust store. Do not disable validation or blindly trust a certificate obtained from an unverified server. The official JCI SDK was reviewed as a protocol reference; its untrusted-certificate stream implementation is not used.

These views help commissioning and troubleshooting. They do not certify live customer compatibility, measurement freshness, write effectiveness or independent alert delivery. Real point writes/readback and installation on customer Windows/Metasys versions require controlled commissioning.
