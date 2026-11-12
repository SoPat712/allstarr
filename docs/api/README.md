# Client API

The Allstarr client API gives music apps access to tracks, lyrics, playback,
matches and listener state. It runs on the music listener, normally port `5274`,
with a Jellyfin backend. Subsonic deployments use the Subsonic compatibility
surface instead. The administrator can disable the client API; disabled requests
return `404`.

Use [v2](v2.md) for new clients. It is the native API, with a shared track shape,
optional views, batched reads and signed playback links. [v1](v1.md) remains
available for existing clients and for operations that v2 does not yet expose.
Both versions use the same listener permissions and core services.

Endpoints are resources, not screens. A client combines resources to build its
interface. Use the v2 batch resource to make several ordinary reads at once.

Existing Jellyfin and Subsonic apps do not need to implement this API. They use
their normal protocol through Allstarr. See [client compatibility](../operations/client-compatibility.md).

## Authentication

Use the listener's Jellyfin access token. A server API key or dashboard cookie
does not establish a listener. Allstarr verifies the token with Jellyfin and
applies that listener's library access, source accounts and preferences.

For v2, send `Authorization: Bearer <token>`. For v1, send `X-Emby-Token: <token>`
or Jellyfin's `Authorization: MediaBrowser ... Token="<token>"` header.
Never put the listener token in a URL. V2 rejects query credentials with `401`.
The v2 event stream and ping require the Bearer header specifically.

Examples use `BASE=http://localhost:5274` and an `ALLSTARR_TOKEN` environment
variable containing your listener token. Use HTTPS when connecting remotely.

```bash
BASE=http://localhost:5274
curl -H "Authorization: Bearer $ALLSTARR_TOKEN" "$BASE/allstarr/v2/tracks/$TRACK_ID"
```

The signed stream URL returned by v2 playback is a separate, expiring media
credential. A player can use it without sending the Jellyfin token. Keep signed
URLs private. Returned public artwork URLs can also be loaded without a token.

## Specifications and versions

The checked-in OpenAPI files are
[client-v1.json](../../apis/specifications/allstarr/client-v1.json) and
[client-v2.json](../../apis/specifications/allstarr/client-v2.json). They define
the routes, parameters and JSON schemas covered by these guides. A running
Jellyfin deployment also serves the documents at `/allstarr/v1/openapi.json` and
`/allstarr/v2/openapi.json` while the client API is enabled.

The path selects the API version. The application version is separate.
The v1 capabilities response reports available features; check it before showing
optional controls. Do not infer feature availability from the server version.
Treat IDs and continuation cursors as opaque. Preserve unknown response fields
and tolerate additional fields when reading JSON.

## Errors

Ordinary failures return JSON with `code` and `error`. Use the HTTP status and
code for behavior; display the short error text as needed.

| Status | Code | Meaning |
| --- | --- | --- |
| 400 | `invalid_request` | Invalid parameters, names or request body. |
| 401 | `unauthorized` | Missing or rejected listener credentials. |
| 403 | `forbidden` | No available listener or insufficient permission. |
| 404 | `not_found` | Unknown, hidden, disabled or unavailable resource. |
| 409 | `conflict` | State changed; refresh before saving again. |
| 416 | `range_not_satisfiable` | Requested byte or time range cannot be served. |
| 503 | `unavailable` | Temporary upstream or local capacity failure. |

Some operations return a more specific code in the same shape. The v1 provider
icon route uses Problem Details for invalid options. Media and already-started
event streams have their own response types; do not assume every response is
JSON. A `304` has no body and means the client's stored representation is current.

## Limits and retries

There is no general requests-per-minute quota on ordinary client API reads.
Provider limits and local capacity still apply. Honor `Retry-After` when present,
use bounded backoff for temporary failures, and stop retrying rejected credentials.

- A v2 track list accepts at most 100 IDs. A batch accepts 1–20 reads.
- Event streams share a limit of eight connections per listener and 256 total
  across v1 and v2. Excess connections return `503`. Reuse a connection.
- Provider audio defaults to six concurrent streams per listener. Local media
  conversion defaults to two concurrent jobs. Administrators can change these
  limits; capacity failures return `503` with `Retry-After`.
- Password sign-in has a separate budget of 20 requests per minute per client
  address and 60 globally, returning `429`. It does not count ordinary token
  reads, playback or seeks. See [dashboard access](../operations/webui-access.md).
