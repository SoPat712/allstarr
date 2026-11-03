# Client API

The client API adds Allstarr features alongside the normal Jellyfin API.
Jellyfin-compatible apps can use both APIs with the listener's Jellyfin token.

It is off by default. An administrator can enable **Client API for apps** in
the dashboard's server settings under **Defaults for everyone**. Changes apply
immediately. While off, all `/allstarr` routes return 404, title marker headers
are ignored, and the extra CORS header support is disabled.

Use `/allstarr/v1` on the music listener, port 8080. These routes are unavailable
on the dashboard listener and on servers using a Subsonic backend.

Send the same credentials used for Jellyfin, for example an `X-Emby-Token`
header or a MediaBrowser authorization header. Tokens must belong to a user.
API keys and tokens without an Allstarr user are forbidden. Administrators
receive the same personal scope as other listeners.

`GET /allstarr/v1/capabilities` returns `serverVersion`, `apiVersion`, and a
`features` object. Check feature flags before using optional routes.
`titleMarkers`, `itemDetails`, and `playback` are available; the other flags remain false until supported.
The version in the path identifies the contract. Compatible additions stay
within v1; incompatible changes use a new path version.

JSON uses camelCase. Every error has this shape:

```json
{"code":"unauthorized","error":"A valid Jellyfin user token is required."}
```

Codes are `unauthorized`, `forbidden`, `not_found`, `invalid_request`, `conflict`,
and `unavailable`. Unknown `/allstarr` paths return `not_found`.

Send `X-Allstarr-Title-Markers: none` on any music request to omit both `[A]`
and `[E]` title markers. This also works on normal Jellyfin routes. It changes
only that request; saved listening preferences stay the same. Other header
values preserve the normal behavior.

Browser clients may send `X-Allstarr-*` headers. Responses expose
`X-Allstarr-Playback-Request` through CORS.

`GET /allstarr/v1/playback/{requestId}` describes a provider stream. Use the
`X-Allstarr-Playback-Request` value received on that stream response. Samples
are retained for seven days, with at most 500 across the server. Unknown,
expired, and other listeners' or devices' IDs all return 404 `not_found`.
Samples recorded before device tracking was available also return 404.

`GET /allstarr/v1/playback/current` describes the caller's active playback on
this device, including tracks from their own Jellyfin library, which have no
playback request ID. It uses playback reports from the last five minutes;
stopped or inactive playback returns 404 `not_found`. It never selects another
device's playback. Send the same `DeviceId` in the MediaBrowser authorization
header or `deviceId` query parameter used for the stream and playback reports.
A request without a device identity returns 404.

Both routes return the following fields and use `Cache-Control: no-store`:

| Field | Meaning |
| --- | --- |
| `provider` | Serving source `{id,name,iconUrl}`, or `null` for own-library playback. Icons use the authenticated `/allstarr/v1/providers/{id}/icon` route; unavailable icons have a null URL. |
| `accountScope` | `personal`, `shared`, or `null` when unavailable or account-free. Account names and identifiers are never returned. |
| `cached` | Whether the stream came from a retained or cached file. |
| `delivered` | Actual output `{format,codec,bitDepth,sampleRate,bitrate}`, or `null` when unavailable. Individual unknown measurements are null; bitrate is in bits per second. Converted streams describe the converted output. |
| `routeReason` | Recorded route reason, or `null`. Own-library playback uses `native-local-library`. |
| `itemId` | Requested item ID, or `null` when unavailable. |
| `startedAt` | Stream start time in ISO 8601. For own-library playback, the start of the observed playback occurrence. |

The client JSON contract uses strict numeric types; numeric schema properties
do not accept strings. Responses continue to write numbers as JSON numbers.

`POST /allstarr/v1/items/details` accepts `{"ids":["jellyfin-item-id","ext-deezer-song-123"]}`.
Send between 1 and 100 IDs; empty or larger batches return 400 `invalid_request`.
The response is `{"items":[...],"missing":[...]}`. Unknown IDs and IDs outside the
listener's view both appear in `missing`. Returned IDs preserve the requested
spelling and order, including repeats. Responses use `Cache-Control: no-store`.

Every item includes all of these fields, with `null` for unknown values:

| Field | Meaning |
| --- | --- |
| `id`, `isExternal` | Requested ID and whether it identifies a service song. |
| `canonicalRecordingId`, `isrc` | Stored recording identity, when known. |
| `provider` | Catalog source `{id,name,iconUrl}`, or `null`. An unavailable icon has a null URL. |
| `audio` | Measured `{codec,bitDepth,sampleRate,bitrate}`, or `null`. Catalog format advertisements are excluded; bitrate is in bits per second. |
| `explicit` | `true`, `false`, or `null`. |
| `saved` | Whether the listener has hearted the item. |
| `keep` | `{state,progress}`. State is `none`, `inProgress`, `kept`, or `failed`; progress is null when no measurement is stored. |
| `match` | Effective `{state,confidence,authority}`, or `null`. Authority is `automatic`, `household`, or `personal`, with personal choices taking precedence. |
| `lyrics` | Stored `{available,format,source}`. Format is `word`, `line`, `plain`, or `null`. |

Native visibility and native hearts are checked in batched Jellyfin requests using
the listener's token. Service metadata, match choices, and measured audio come
from stored records. A keep requires the listener's active heart reference to
a retained file; another listener's retention does not set this state.
Lyrics availability reads stored choices, retained sidecars, and supported
cached results. A pinned source without stored content returns `available:false`,
its source ID, and `format:null`. This route never requests lyrics from a provider.

`GET /allstarr/v1/providers/{providerId}/icon` returns the same image bytes used
by the dashboard. Send the listener's token on the image request too; a dashboard
cookie alone does not authorize it. The route follows the same opt-in and port
restrictions. Unknown providers and providers without an icon return 404
`not_found`. Successful images have private one-day caching and an ETag;
`If-None-Match` can return 304.

When enabled, the OpenAPI document is public at `/allstarr/v1/openapi.json`.
Its checked-in copy is `apis/specifications/allstarr/client-v1.json`.
OpenAPI operation names are stable for generated clients.
The contract test compares normalized JSON. Set `ALLSTARR_UPDATE_CLIENT_SPEC=1`
when running that test to refresh the copy after an intentional change.
