# Metasys API integration notes

Verified 2026-10-21 against official [v5 specification](https://jci-metasys.github.io/api-landing/api/v5) and [v6 specification](https://jci-metasys.github.io/api-landing/api/v6). Documentation verification only; no live ADX tested.

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

Do not infer that any point is writable. Validate type, units, ranges, quality, permissions, and command schema. Make explicit write priority available only for supported command schemas. Keep this bridge one-way. Treat long-outage recovery as current-state synchronization; it cannot promise every intermediate transition.
