# External JSON sources and multiple recipients

These features are disabled by default. Enable them explicitly only after reviewing source freshness, destination permissions, and the security upgrade notes in SECURITY-UPGRADE.txt.

## Configuration workflow

1. In Admin / Connections explicitly enable extended sources/recipients. Configuration changes pause transfers.
2. Save each additional Metasys recipient with its own URL, API version and service account. Save/Test/Clear credentials use the existing protected connection mechanisms. Human sign-in credentials remain separate.
3. Add a JSON source with an HTTPS GET URL, JSON Pointer paths for its scalar value, observation timestamp and units, expected units/type, polling interval and maximum observation age. A timestamp must include a timezone. Sources start disabled.
4. Prefer a header API key or bearer token and enter it locally. Blank secret preserves the saved secret only for the same origin/authentication settings; Clear removes it. All saved URL query values are encrypted and masked with `__POINTBRIDGE_SAVED_QUERY__`. Keep that marker to retain them, enter a complete URL to replace them, or remove the query to clear them. Existing query URLs migrate automatically with recovery bindings preserved. OAuth token acquisition/renewal and interactive API authentication are not implemented.
5. Test the saved source. This explicitly reads it without enabling transfers. Enable it only after reviewing its value, timestamp, units and quality semantics.
6. In the mapping choose JSON as the source, the saved source and a recipient. Choose a destination point and validate it using the existing Metasys type/unit/writability/command preflight. Add another recipient as a separate mapping; review its destination and enable it explicitly.

One configured field source shares a sample/request budget across its recipient mappings. Separate source IDs have separate budgets even when their URLs match. Explicit source tests make separate reads. Different recipients have independent confirmation/failure state; one recipient's success does not declare the others successful.

## Units and timing

There is no numerical unit conversion. Different unit labels require explicit confirmation that they describe the same physical scale. Never use that confirmation to turn Celsius into Fahrenheit or convert pressure units. Select compatible destination units. The initial adapter requires explicit unit metadata; it is not an arbitrary scripting, array aggregation or formula engine.

Polling defaults to 300 seconds; configurable range is 15–86400 seconds. Maximum observation age is required, 1–604800 seconds, with no automatic acceptable-age default. Source timestamps, not HTTP arrival time, establish observation freshness. Cached observations can expire between polls. Null, malformed, wrong-type/unit, regressed, excessively future or old observations stop delivery instead of becoming zero or a reused last-good value. Optional explicit quality checks reject bad/missing configured quality. Without such metadata, quality is reported as unknown, not proven good.

Requests have a configurable 1–30 second timeout, a 1 MiB response limit and at most three attempts for selected transient HTTP errors. Short Retry-After delays are bounded; longer delays defer subsequent attempts. Normal TLS verification applies. Redirects, cookies and automatic Windows credentials are disabled. Source auth is attached only to the source client's request; Metasys recipient clients use their own credentials and transports.

## Delivery and compatibility

Existing mappings remain Metasys-to-default-recipient mappings when new fields are absent. Loading old configuration does not enable extensions or rewrite it. The first explicit schema upgrade retains config.json.v1-before-extension; protected credentials in that local backup remain sensitive. No lab settings belong in a distributable.

JSON mappings reuse the existing validated write/readback path. Each mapping has bounded processing, independent health and conservative uncertain-write handling. Destination drift is diagnosed rather than blindly overwritten. Configured command and priority semantics still apply. Paused instances and disabled extension mappings cannot perform background source reads or writes. A manual Test action is an explicit read-only exception.

This is latest-value replication, not a historian: intermediate readings during outages are not backfilled. Metasys destination trends may hold old values during failures. A recent HTTP response is not proof of a fresh physical measurement. Durable recovery records now retain pending/confirmed operation fingerprints and delivered external observation time across restart; see RECOVERY-AND-DIAGNOSTICS.md for the conservative readback policy and its limitations.

## Weather.gov example

The [official NWS API documentation](https://www.weather.gov/documentation/services-web-api) describes free access with unpublished reasonable rate limits, an identifying User-Agent requirement, and the station endpoint `/stations/{stationId}/observations/latest`. It also warns that upstream quality processing can delay observations by up to 20 minutes. That delay is not a chosen acceptable age for this application.

`weather-source.disabled.example.json` supplies the temperature paths but deliberately leaves the station URL, User-Agent, destination units and maximum age unconfigured. Select and review the station locally, confirm its returned field/unit/timestamp structure, and choose an acceptable age. No weather API request was made by the fixture tests. Weather data is an example external source, not a substitute for pharmacy instrumentation.

## Failure alerting boundary

Authenticated health endpoints expose separate source contact/quality, observation age, worker progress and destination confirmation. The watchdog consumer has offline crash/stall/outage tests. Live Metasys health publishing, independent supervisory logic and existing alarm notification delivery remain unconfigured. In-app logging cannot notify anyone after the app or host dies. See WATCHDOG-OFFLINE.md for the independent commissioning contract.
