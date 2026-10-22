# Metasys API integration notes

Verified 2026-10-21 against official [v5 specification](https://jci-metasys.github.io/api-landing/api/v5) and [v6 specification](https://jci-metasys.github.io/api-landing/api/v6). Documentation verification only; no live ADX tested.

## Metasys 12 destination compatibility

API v4 destinations use `GET /objectIdentifiers?fqr=...`. The v4 attribute collection has no `attributeFilter=writableBasic`: inspect its partial property schema, explicit effective `canModify` permission, and `readOnly`/`prioritized` flags. Prioritized properties require a discovered scalar adjust command and explicit `effectivePermissions.canInvoke` permission; they are not silently patched. Source connections remain v5/v6. Public REST API accounts and licensing are separate from human Metasys UI sign-in.

The official [v4 specification](https://jci-metasys.github.io/api-landing/api/v4) and [JCI .NET client](https://github.com/jci-metasys/basic-services-dotnet/blob/main/MetasysServices/MetasysClient.cs) were reviewed for identifier lookup, property writes, commands, read-only batch envelopes and token refresh. The SDK's stream transport accepts untrusted certificates; it is not incorporated. Point Bridge retains normal TLS verification and its own bounded write/readback recovery.

All supported API clients refresh a still-valid, expiring token using `GET /refreshToken` with its bearer token. Only a rejected refresh (401) or an already expired token permits password reauthentication. Outages, TLS failures and malformed refresh responses do not trigger credential retries or replay writes. The mapping browser reads a maximum of 50 distinct visible point GUIDs per read-only batch; it always submits `method: GET` and preserves per-request statuses. All supported versions require explicit `canModify`/`canInvoke` confirmation for the applicable write method.

The richer browser uses documented object, network device, equipment, space and equipment-point endpoints. Equipment point entries identify equipment instances; extract the mapped object GUID only from a same-origin/version `objectUrl`. The inspector uses object-scoped `GET /activities?activityType=alarm|audit`, not deprecated alarm/audit collection operations, and projects only safe diagnostic fields. API v4 trends use `startTime`, `endTime`, numbered pages, nested `value`, `timestamp` and `isReliable`. API v5/v6 use `startSampleTime`, `endSampleTime`, scalar `value`, `sampleTime` and `reliability`, with no numbered trend pages. No SDK certificate-bypass behavior is adopted.

## Shared v5/v6 contract

Base: `https://HOST/api/vN`, N independently configured for each server.

| Action | Request / response |
|---|---|
| Login | `POST /login` JSON `{ "username":"USER", "password":"PASSWORD" }` → `{ "accessToken":"...", "expires":"UTC timestamp" }` |
| Authenticate | `Authorization: Bearer TOKEN` |
| Renew | `GET /refreshToken` with current bearer token |
| Browse | `GET /objects`; children `GET /objects/{id}/objects`; follow pagination `next` |
| Resolve reference | `GET /objects/identifiers?fqr=URL_ENCODED_REFERENCE` → bare JSON GUID string |
| Read | `GET /objects/{id}/attributes/presentValue` → `item.presentValue` |
| Inspect schema | `GET /objects/{id}?includeSchema=true` |
| Edit writable attribute | `PATCH /objects/{id}` JSON `{ "item": { "presentValue":71.5 } }` |
| Discover commands | `GET /objects/{id}/commands`; inspect `commandBodySchema` and permissions |
| Command example | `PUT /objects/{id}/commands/adjustCommand` JSON `{ "parameters":[72.5], "annotation":"Bridge update" }` |

Accounts require API Access Type. Point operations require Monitoring and Commanding licensing. Reference lookup prose contains a stale `/objectIdentifiers` example; operation path and curl agree on `/objects/identifiers`.

## Streaming and version differences

`GET /stream` opens SSE. Initial `hello` event data is stream GUID. Subscribe using `GET /objects/{id}/attributes/presentValue` with `METASYS-SUBSCRIBE: STREAM_GUID`. Save response `METASYS-SUBSCRIPTION-LOCATION`; DELETE that location to unsubscribe.

- v5 (introduced with Metasys 13): `object.values.update` contains `{subscriptionIds:[],item:{id,itemReference,presentValue},condition:{}}`.
- v6 (documented for Metasys 14): example wraps updates in `{summary:"CoVs",value:[{item:{id,itemReference,presentValue},condition:{}}]}`. Parse both layouts defensively; confirm against deployment.
- v6 adds `object.values.heartbeatImproved` with `activeSubscriptions` and `dateTime`.
- `object.values.notification` requires rereading the named attribute.
- Command success: v5 HTTP 200; v6 HTTP 202 accepted. Verify destination readback separately.
- Refresh tokens before expiry. Reconnect with current token and `Last-Event-Id`; HTTP 204 requires a new stream and resubscription. Buffering is brief; reconcile current values after outages.

## Implementation decisions

Request failures identify the configured connection (`source`, `destination`, or the additional recipient identifier), HTTP status, method and request path. Query values, account names, credentials, bearer tokens and raw server responses are not included. Start/check errors and per-mapping worker HTTP errors retain this context. Scrubbed diagnostic exports continue to omit raw error messages and paths.

Saved connections share an in-memory bearer session and HTTP transport across mapping validation, runtime workers and browsing. Each operation acquires a reference-counted client lease with separate stream subscription bookkeeping. Closing a lease does not close another operation's connection. Refresh is serialized across leases; the token/expiry pair is replaced atomically. API v4 reported expirations longer than an hour are capped at 55 minutes before renewal, following JCI's documented lifetime caveat.

Changing a server, account, API version or protected password retires the old cached client after the settings save succeeds. Clearing credentials/removing a recipient prevents new leases from using its old token; already admitted operations release their transport when finished. Application shutdown releases the cache. Tokens remain in memory only. A saved connection test actually contacts the server with token refresh (or an initial login), rather than merely reporting that a cached token exists. Tests with explicitly entered new credentials use an isolated temporary client.

The public API v4/v5/v6 specifications document `/login` and `/refreshToken`, but no logout/revocation operation. Disposing an HTTP client is not a server logout, and Point Bridge does not invent a logout request or misuse the separate private human UI logout API. Reusing/refreshing the existing bearer avoids unnecessary password logins; unreferenced local tokens are discarded, while server token expiry remains controlled by Metasys. A 403 is surfaced without replaying the denied operation or retrying a password login.

Do not infer that any point is writable. Validate type, units, ranges, quality, permissions, and command schema. Make explicit write priority available only for supported command schemas. Keep this bridge one-way. Treat long-outage recovery as current-state synchronization; it cannot promise every intermediate transition.
