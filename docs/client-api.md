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
`titleMarkers`, `itemDetails`, `playback`, `lyricsRich`, `lyricsSources`,
`fixesMatch`, `reports`, `mixes`, `releases`, `follow`, and `events` are available;
the other flags remain false until supported.
The version in the path identifies the contract. Compatible additions stay
within v1; incompatible changes use a new path version.

JSON uses camelCase. Every error has this shape:

```json
{"code":"unauthorized","error":"A valid Jellyfin user token is required."}
```

Codes are `unauthorized`, `forbidden`, `not_found`, `artist_not_found`,
`invalid_request`, `conflict`, and `unavailable`. Unknown `/allstarr` paths
return `not_found`.

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

`GET /allstarr/v1/items/{itemId}/lyrics` returns the listener's selected lyrics.
Native Jellyfin IDs and external song IDs use the same song lookup and source
selection as the Jellyfin lyrics routes: the effective pin takes precedence,
then automatic selection prefers the best timing. Unknown or hidden items and
items without lyrics return 404 `not_found`. Responses use `Cache-Control: no-store`.

```json
{
  "source": {"id":"lyricsplus","name":"LyricsPlus"},
  "format": "word",
  "lines": [
    {"startMs":1000,"endMs":1800,"text":"Hello","part":"lead",
     "words":[{"startMs":1000,"endMs":1800,"text":"Hello"}]},
    {"startMs":1200,"endMs":1600,"text":"Echo","part":"background",
     "voice":"v2","singer":"Second singer",
     "words":[{"startMs":1200,"endMs":1600,"text":"Echo"}]}
  ]
}
```

`format` is `word`, `line`, or `plain`. Times are milliseconds; unknown times
are null. Line timing has an empty `words` array. Plain lyrics have null line
times and empty word arrays. Background vocals keep their own word timing.
`voice` and `singer` are present when supplied by the source.

`GET /allstarr/v1/items/{itemId}/lyrics/sources` lists candidate sources,
their formats, a text preview of up to four lines and 1,000 characters, the
chosen source and layer (`personal`, `household`, or `automatic`), and the
current revision. An unavailable candidate has null format and preview;
`chosen` is null when no lyrics can be selected.

```json
{
  "sources": [{"sourceId":"lyricsplus","format":"word","preview":"Hello\nEcho"}],
  "chosen": {"sourceId":"lyricsplus","layer":"automatic"},
  "revision": {"personal":null,"household":null}
}
```

Pin a candidate with `PUT /allstarr/v1/items/{itemId}/lyrics/source`:

```json
{"sourceId":"lyricsplus","expectedRevision":{"personal":null,"household":null}}
```

Clear the caller's pin with `POST /allstarr/v1/items/{itemId}/lyrics/source/clear`:

```json
{"expectedRevision":{"personal":{"id":"11111111-1111-4111-8111-111111111111","revision":1},"household":null}}
```

Both writes return `{chosen,revision}` and use `Cache-Control: no-store`.
Copy the complete revision object from the last sources or write response into
`expectedRevision`; its stamps are opaque, and numeric revisions must be JSON
numbers. A stale personal or household revision returns 409 `conflict`.
Choosing an unavailable source or a source whose preview changes during the
save also returns 409. A missing or null `expectedRevision` returns 400
`invalid_request`.

These writes always affect the caller's personal pin, including for
administrators. Household pins and other listeners' choices are preserved.
Clearing restores household selection when available, otherwise automatic
selection. No other listener's personal choices are returned.

Source review requires a known canonical recording. Native IDs map through
the accessible library index after a Jellyfin visibility check with the
listener's token. External IDs map through the same provider song identity
used by the Jellyfin lyrics route. An item without a known recording returns
404 from source review and writes; rich lyrics can still be available.

`GET /allstarr/v1/items/{itemId}/match` returns the caller's effective match:
`{state,confidence,authority,current,candidates,revision}`. Authority is
`automatic`, `household`, or `personal`. Each candidate has `candidateId`,
`title`, `artist`, `album`, `durationMs`, `isrc`, `provider` (`id`, `name`,
`iconUrl`), `isExternal`, `confidence`, and `reasons` containing the same
saved evidence shown in the dashboard. Unknown metadata is null. Own-library
candidates identify Jellyfin as their provider. `current` is null when no
candidate is selected, including after rejection.

Choose a returned candidate with `PUT /allstarr/v1/items/{itemId}/match`:

```json
{"candidateId":"opaque-candidate-id","expectedRevision":"opaque-revision"}
```

Reject with `POST /allstarr/v1/items/{itemId}/match/reject` and body
`{"expectedRevision":"opaque-revision"}`. Clear a personal choice or rejection
with `DELETE /allstarr/v1/items/{itemId}/match?expectedRevision=opaque-revision`.
All three writes return the updated match object. Copy the exact opaque string
from the last response; a stale revision returns 409 `conflict`, including
changes to the automatic decision or household choice. Revisions cannot be
reused after a choice is set and cleared. All responses use `Cache-Control: no-store`.
These writes affect only the caller, including administrators. Clearing restores
the household choice when available, otherwise automatic matching.

External song IDs resolve to the newest accessible track snapshot for that
source and listener. When an external playback item has no source snapshot,
its known recording locates the caller's source snapshot. Canonical song IDs
and visible native Jellyfin IDs also resolve through the known recording to
a snapshot owned by that listener. Native IDs
first pass the same Jellyfin visibility check and accessible library-index
lookup used by lyrics source review. Missing snapshots and snapshots outside
the listener's view return 404 `not_found`.

`POST /allstarr/v1/reports` creates a problem report and returns `201 {"id":"..."}`:

```json
{"kind":"wrong-song","note":"The live version plays instead.","itemId":"ext-deezer-song-123","playbackRequestId":"11111111-1111-4111-8111-111111111111"}
```

`itemId` and `playbackRequestId` are optional. Kinds are `wrong-song`,
`wrong-lyrics`, `wont-play`, `bad-quality`, `wrong-info`, and `other`. They appear
in the existing dashboard inbox as `wrong-match`, `wrong-lyrics`, `playback`,
`playback`, `metadata`, and `other`, respectively. Notes must contain 1–2,000
characters after trimming and follow the same text validation and request-size
limit as dashboard reports.

Playback context is captured on the server. An explicit request ID must belong
to this listener and device, follow the same retention rules as playback lookup,
and agree with `itemId` when both are provided. Unknown, expired, or foreign IDs
return 404 `not_found`. Without a request ID, the caller's recent playback on
this device supplies context when available; an optional item ID restricts that
selection. Other listeners' or devices' playback cannot be attached, including
by administrators. Responses use `Cache-Control: no-store`.

The OpenAPI 3.1 document explicitly types enums. Nullable string enums include
null in both their `type` and `enum`. Nullable object references declare
`type: ["object", "null"]` and use `anyOf` with the reference and a null schema,
preserving referenced-object validation while accepting null. Response JSON is
unchanged.

`GET /allstarr/v1/mixes` returns `{"mixes":[...]}` in the same order as the
listener's For you page. An optional `timeZoneId` uses the dashboard's time zone
validation and saved preference. Reads use stored mixes and never generate or
refresh them. Synthetic listeners cannot read mixes.

Each mix has `mixId`, `kind`, `name`, `description`, `state`, `itemCount`,
`durationMs`, `updatedAt`, `coverUrl`, `inApps`, and `playlistId`. `kind` preserves
the dashboard's stable IDs, including `on-repeat`, `rewind`, `release-radar`,
`discover-weekly`, `daily-1` through `daily-4`, mood IDs, `custom`, and `external`.
The typed state maps dashboard readiness to `ready`; pending linked-playlist
matching to `building`; requests for more plays, listening, or history to
`needs-listening`; and other unready cards to `empty`. Uninitialized cards and
cards without playable songs are `empty`.

`itemCount` and `durationMs` describe the stored songs visible through the
listener's library access and listening policy. Unknown song durations add
zero to the total. `updatedAt` is null before a mix has been built. `inApps`
requires a published generation, the listener's publication choice, and the
server's mix publication setting; `playlistId` is its Jellyfin playlist ID,
or null when unpublished.

`GET /allstarr/v1/mixes/{mixId}/items` returns `{mixId,updatedAt,items}`. Items
retain mix order and include `itemId`, `title`, `artist`, `album`, `albumId`,
`artistId`, `durationMs`, `isExternal`, `provider`, and the stored per-song
`reason`. Provider metadata has the same `{id,name,iconUrl}` shape as item
details; library songs have a null provider. Missing relationships and durations
are null. Library songs no longer visible to the caller's token are omitted.
Song, album, and artist IDs use the Jellyfin surface, including native
library IDs, service `ext-...` IDs, stored recording IDs, and Apple catalog
aliases. These JSON routes use `Cache-Control: no-store`.

`GET /allstarr/v1/mixes/{mixId}/cover` serves the same generated PNG as the
dashboard. It uses a private cache with revalidation and returns 304 for a
matching `If-None-Match`. Unknown mixes and other listeners' mixes return 404,
including conditional cover requests.

`GET /allstarr/v1/releases` returns `{releases,followedArtists}` from the
listener's stored New releases inbox and follows, without polling providers.
Each release includes `releaseId`, `title`, `kind`, `releaseDate`,
`artist:{name,artistId}`, `albumId`, and `coverUrl`. Dates preserve the inbox's
precision. Excluded and dismissed entries are omitted. Followed artists have
`artistId` and `name`. Artist and album IDs use the Jellyfin surface; an older
cached release without a recoverable artist relationship has a null artist ID.
Responses use `Cache-Control: no-store`.

`GET /allstarr/v1/releases/{releaseId}/cover` serves the inbox entry's source
album artwork using the same loader and image cache as the dashboard. Available
covers use a private seven-day cache, ETag, and 304 revalidation. A release with
no usable cover returns 204 with a private one-hour cache. A release outside the
listener's inbox returns 404.

Follow an artist with `PUT /allstarr/v1/artists/{artistId}/follow` and unfollow
with `DELETE` on the same path. No body is required. Both are idempotent and
return `{followed,artist:{artistId,name}}`, preserving the requested Jellyfin
artist ID. Service artist IDs map directly to their provider artist. A native
library artist first passes a Jellyfin lookup with the caller's token, then
uses the exact-name source lookup shared with the dashboard's follow-from-history
action. An unresolvable artist returns 404 `artist_not_found`. Writes affect
only this listener and use `Cache-Control: no-store`.

`GET /allstarr/v1/events` (`getEvents`) opens a `text/event-stream` response.
Use the same Jellyfin user token and `DeviceId` as playback. The stream is
personal, including for administrators. A missing device identity still receives
personal changes but cannot receive device playback notifications.

The first flushed frame is always:

```text
event: ready
data: {}

```

Subsequent frames carry an event name and one JSON `data:` line. Item IDs are
the native Jellyfin or `ext-...` IDs accepted by the item routes. Changes may
invalidate more than one spelling of the same recording; refetch the relevant
item or collection. Treat revisions as opaque JSON, and fetch the latest lyrics
sources revision before a write.

| Event | Data |
| --- | --- |
| `keep.updated` | `{itemId,state,progress?}`. State is `none`, `inProgress`, `kept`, or `failed`; progress is 0–1 when measured. |
| `match.updated` | `{itemId,state}` with the effective match state used by the match route. Effective personal choices hide unrelated household and automatic changes. |
| `lyrics.updated` | `{itemId,revision}` after a pin/clear or an effective lyrics change. |
| `mix.updated` | `{mixId}` after regeneration, saving, deletion, or another saved mix change. |
| `releases.updated` | `{count}` with the current New releases inbox size. |
| `playback.updated` | `{itemId}` when the serving route changes during the caller's active stream, only on connections with the same `DeviceId`. |

A comment frame `: ping` is flushed every 25 seconds. Allstarr disables response
buffering and compression for this route and sends `Cache-Control: no-store,
no-transform` and `X-Accel-Buffering: no`. Configure any additional proxy to allow
long streaming responses.

Reconnect with backoff and jitter after EOF or a network failure, and refresh
current state after each `ready`. There is no history or `Last-Event-ID` replay.
Notifications are hints: duplicate type+item changes within a short burst are
combined, and slow consumers can lose older notifications. Each connection holds
at most 64 pending notifications and sends at most 10 per second, with a 100 ms
coalescing window. There are at most 8 connections per listener and 256 across
the server; excess connections return 503 `unavailable`. Connections close on
client disconnect and server shutdown. Clients should share one stream per
running app and periodically reconcile state through ordinary reads.
