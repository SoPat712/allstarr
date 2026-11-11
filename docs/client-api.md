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

`GET /allstarr/v1/capabilities` returns `serverVersion`, `apiVersion`, a
`features` object, and `library: {id,name,iconUrl,glyphUrl}`. The library identifies
the configured media backend: `jellyfin` / "Jellyfin", `navidrome` / "Navidrome"
when a Subsonic-compatible server identifies itself as Navidrome, or `subsonic` /
"Subsonic server" otherwise. Server identification is cached. Check feature flags
before using optional routes.
`titleMarkers`, `itemDetails`, `playback`, `lyricsRich`, `lyricsSources`, `lyricsVersions`,
`fixesMatch`, `reports`, `mixes`, `releases`, `follow`, `events`, `appearances`,
`similarAlbums`, `playlistKinds`, `history`, `playCounts`, `libraryAdditions`,
`preferences`, `searchStream`, `searchStreamScores`, `searchStreamRanked`, and `searchStreamRankedLibrary` are available.
`stats`, `taste`, `sound`, `playActivity`, `historyImports`, and `historyRecheck`
are true when their respective services are registered. `karaokeModel` is true while
a valid on-device karaoke model is present. `dashboardLink` is false.
When `Admin:Oidc:PublicUrl` is configured, capabilities also includes
`dashboard: {url}` with that public control-panel address, including its base
path. The app can open it in a browser for normal sign-in. The object is omitted
when no public address is configured; no signed-in link is provided.
The version in the path identifies the contract. Compatible additions stay
within v1; incompatible changes use a new path version.

For native library results, `GET /Items?searchTerm=…&allstarrScope=library`
forwards the Jellyfin search without provider or saved-library work. The scope
parameter is removed before forwarding; normal authentication and response
filtering still apply. Other query parameters, including repeated fields, remain
intact. The existing merged search remains available without this parameter.
It waits at most three seconds for providers after Jellyfin answers; late fetches
continue to fill the provider cache for the next search.

`GET /allstarr/v1/search?q=&limit=20&types=Audio,MusicAlbum,MusicArtist,Playlist`
streams external search results as `application/x-ndjson`. Query text must contain
1–200 characters; `limit` is 1–200 per provider and item kind. Omitted `types`
requests every supported kind. Each flushed line is `{provider:"id",items:[...]}`.
Metadata and playlists can arrive in separate lines for the same provider.
Items use the normal Jellyfin search item shape, including title markers, artwork,
and user data; playlists retain the search route's album presentation.
Every item also has a lowercase numeric `score` from the same relevance scorer
used by merged search. Each provider batch is stably sorted by decreasing score.
Artist rows below the scorer's 70-point token band are omitted, except for the
best artist when all candidates fall below that band. Exact and prefix artist
matches score 95–100. Songs and albums can match through their artist credits;
their titles need not contain the query. Limits apply separately to playlists
even though their item presentation uses `MusicAlbum`.

Songbird can check `features.searchStreamRankedLibrary` (or `features.searchStreamRanked`) and request `shape=ranked` to
render the server's complete order without sorting in the app. Each line replaces
the previous snapshot:

```json
{
  "revision": 1,
  "done": false,
  "providers": ["deezer", "qobuz"],
  "top": {"Id":"ext-deezer-artist-123","Name":"Say Now","Type":"MusicArtist","score":100},
  "items": [{"Id":"ext-deezer-artist-123","Name":"Say Now","Type":"MusicArtist","score":100}],
  "sections": {"songs":[],"albums":[],"artists":[{"Id":"ext-deezer-artist-123","Name":"Say Now","Type":"MusicArtist","score":100}],"playlists":[]}
}
```

Revisions increase from 1. `providers` lists answered providers in arrival order.
Each line is a complete replacement snapshot, with these field types:

| Field | Type |
|---|---|
| `revision` | Integer, strictly increasing from 1 |
| `done` | Boolean |
| `providers`, `late` | Arrays of provider-id strings |
| `top` | One item or `null` |
| `items` | Array of items |
| `sections` | Object containing item arrays named `songs`, `albums`, `artists`, `playlists` |

Missing keys mean empty: missing arrays or section keys are `[]`, a missing
`sections` object is `{}`, and a missing or null `top` has no item. Earlier lines
omit `late`; the done line includes it. Items retain the existing Jellyfin item
JSON plus numeric `score`, keyed by a stable string `Id`. Library matches are included
in `top`, `items`, and `sections` (songs, albums, artists, playlists), ranked by the same
scorer and rules merged search uses for library rows; native rows win ties and an owned exact
match wins `top` over external lookalikes. Library items keep their native library Ids.
Library results are read once per request and included from revision 1 if ready, otherwise
from the first snapshot after they arrive. Render All as `top`
followed by `items`, deduplicated by `Id`, keeping the server's order.
`features.searchStreamRanked` is advertised only when this ranked shape is
available; `features.searchStreamRankedLibrary` is advertised when library matches are included
in ranked snapshots.

Only sections selected by `types` are present, and each contains at most `limit`
items across all providers. `items` mixes all requested types in the same relevance
order as merged `/Items` search, and also contains at most `limit` items. An omitted
`limit` defaults to 40 for ranked snapshots; default provider batches still use 20.
Items keep their `Id` across snapshots. `top` selects
the best exact or near-exact artist (95 points or higher), otherwise the best item
overall, or `null` when there are no items. An exact-name artist is pinned as `top`
for the rest of the stream. The top item also appears in its section and remains
in `items` wherever relevance places it, when within the mixed limit. When rendering
All as `top` followed by `items`, skip the repeated top `Id` in `items`.

The mixed list and sections use the same scores as merged search. After the first snapshot, an item
moves above another only when it leads by at least five points, one score band;
smaller changes retain the previous order. The first snapshot waits for the
preferred provider and another provider to answer, or for 400 ms, whichever comes
first. Later snapshots are at least 250 ms apart and fold intervening batches
together. The final state is always flushed with `done:true` and a `late` list;
earlier snapshots omit `late`. Provider work has the same ten-second cap; a final
flush can take up to another 250 ms to preserve snapshot spacing. These holds
reduce visual churn and add latency; they are not search-speed improvements.
Omitting `shape` keeps the existing per-provider envelope and final line.

Searches run concurrently. Before emitting rows, the route searches the accessible
native library to suppress matching songs, albums, and artist names, and checks
known native recording links. In default provider batches, native rows are not emitted;
ranked snapshots include them. Duplicate item IDs
and matching recordings already sent are suppressed; the first arriving provider
wins. Album matching uses normalized title and artist. Native search can determine
the first-batch latency. Saved-ID and owned-copy enrichment has a shared 50 ms
window per batch. When that window expires, the original playable external rows
remain; a slow match does not hold up the batch. The merged route uses the same
enrichment bound. Owned native copies are read in batches rather than one request
per song. The final line is `{done:true,late:["provider-id"]}`;
`late` names providers whose batches could not finish within the whole stream's
ten-second cap. Disconnecting cancels that stream's provider work. Concurrent
streams share provider fetches until their last reader disconnects.

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

`capabilities.libraryAdditions` contains `{canAddDirectly,requiresApproval,defaultUnit}`.
The first two fields follow the existing household approval rule and the listener's
stored role; `defaultUnit` is the listener's promotion profile default (`song` or
`album`). An administrator using the client API still sees only their own work.

`POST /allstarr/v1/library/additions` accepts `{itemId,unit}` and returns 202 with
`{id,itemId,requestedItemId,unit,state,reason,libraryItemId?}`. `unit` must be `song` or `album`. A song addition
adds just that song, tagged with its real album. An album addition uses the
existing promotion service to add the release. External album IDs are accepted
with `unit: "album"`; song IDs may also anchor an album addition. Library roots,
quality checks, acquisition, approval, and duplicate handling use the existing
promotion settings. A listener who needs approval files a request instead of
starting acquisition. Missing library setup returns a plain setup error.

An optional `Idempotency-Key` header (1–300 characters, not blank) identifies a
submission for that listener for 24 hours. Repeating the key with the same
`{itemId,unit}` returns the existing addition in its current state with 200.
Reusing it with a different item or unit returns 409. Another listener's key is
independent. After 24 hours the key can be reused; normal promotion duplicate
handling still applies.

`GET /allstarr/v1/library/additions` returns an array of the listener's additions
and requests using that same shape. `itemId` is the representative recording's
canonical `ext-allstarr-song-...` ID. An approved request keeps its request `id`;
its linked run is not duplicated in the list. `requestedItemId` preserves the
exact submitted ID, including provider song and album IDs; older additions may
omit it. Different submitted rows can share acquisition while retaining their
own addition receipts. States are `adding`,
`waitingForApproval`, `needsLook`, `done`, `declined`, and `cancelled`. `reason`
is plain text; a declined request includes the household reviewer's note when
provided. `done` means the promotion placed the music and queued a backend scan;
the scan may still be running.

`libraryItemId` is omitted until the addition is `done` and the local library
index has matched its representative recording to a native media-server item
in the target library. It identifies that song, including for an album addition
anchored by a representative song. Apps can use it for queue, heart, and playlist
handover. The scan match publishes another update with the native ID. Responses
and events currently omit `progress`.

`POST /allstarr/v1/library/additions/{id}/cancel` cancels a pending request or
the listener's own promotion, including a run linked to an approved request.
It retains any library files already placed. A listener cannot cancel another
listener's shared run; that returns 409. Foreign IDs return 404. Reads and writes
use `Cache-Control: private, no-store`.

`GET /allstarr/v1/preferences` returns `{heartAction}`. PATCH the same route with
`{heartAction: "heart" | "keep" | "addToLibrary"}` to save that listener's choice.
The default is `keep`. `heart` saves only a heart; `keep` also keeps a copy as
before; `addToLibrary` saves a heart and adds the song through the same library
addition path, including household approval. Changing the preference affects
new hearts; it does not remove existing copies or cancel accepted additions.
Removing a heart does not remove music already added to the library. Other
listening preferences retain their saved overrides or household inheritance.

`GET /allstarr/v1/me/stats?period=7d|30d|year|all&year=&timeZoneId=` returns
the signed-in listener's dashboard stats: totals, coverage, top songs, artists,
albums and genres, timeline, hourly counts, streaks and newly heard artists.
Omit `period` for the last 30 days. Use either `period` or a calendar `year`
from 1 through 9998; combining them or supplying an invalid time zone returns
400 `invalid_request`, with the same validation message as the dashboard.
`timeZoneId` uses a system time zone ID such as `America/New_York`; omitted
values use the server's time zone. Calendar windows include their start and
exclude their end.

Song items have `{title,artist,album,playCount,artworkUrl}`; artist items have
`{name,playCount,artworkUrl,id?}`; album items have
`{title,artist,playCount,artworkUrl}`. Artwork URLs point to the music listener's
`GET /allstarr/v1/me/stats/art?artist=&title=|album=` route. Omit both `title`
and `album` for an artist photo. Send the Jellyfin token on image requests too.
The route uses the dashboard's artwork lookup and listener-scoped cache;
missing artwork returns 204. Images use private seven-day caching, and missing
images use private one-hour caching.

Artist totals count each credited artist individually. Structured library
`ArtistItems`, owned service-song credits and stored import enrichment take
precedence over display text. Without structured credits, only `, `, ` & `,
` feat. `, ` ft. ` and ` x ` separate names. Known single artist names, including
`Tyler, The Creator`, stay intact. An artist's optional `id` is a known library
artist ID or a service artist ID from verified stored credits; unknown IDs are
omitted.

Artist pictures use the artist identity from an accessible played library
track first, then a verified artist index when available, then a cover from
the listener's own plays. This server has no artist index, so that middle step
is skipped. Artist-name searches are never used for these pictures. The app
should use the supplied `artworkUrl`, which can include `libraryArtistId`.

`GET /allstarr/v1/me/taste` returns
`{lately:{tags,artists},usually:{tags,artists},latelyWeight,paused,useImportedHistory,nudges}`.
Each tag or artist is `{name,strength}`, with strength from 0 to 100. Nudges are
`{kind,name,mode,expiresAt}`; `expiresAt` is null for an ongoing nudge. This route
reads the listener's existing taste profile and settings.
The route returns the last computed profile, including after a restart, while
refreshing changed or expired inputs in the background. It shares this behavior
with the dashboard. Nudges and learning settings reflect the current saved
values; learned layers can briefly be stale. Before a first full computation,
the route returns the saved live learning while building the full profile.

`GET /allstarr/v1/me/sound` returns
`{songs,skippedSongs,librarySongs,traits,tempo,libraryTempo,at}`. Traits are
`{name,difference,lean}`: difference compares finished songs with the library,
and lean compares finished songs with skipped songs. Unknown lean or tempo
values are null. The existing sound service requires at least 20 finished
songs with analysis and 20 other analyzed library songs; insufficient history
or unavailable analysis returns 404 `not_found`. Apps can show an empty state
until a profile is available.

`GET /allstarr/v1/me/history?limit=50&before=` returns `{plays,next?}`. `limit`
must be between 1 and 200. Plays are newest first, with a stable order when
timestamps match. Pass the opaque `next` value unchanged as `before` to read
older plays; `next` is omitted at the end. Invalid paging returns 400.
Each play has `{itemId,title,artist,album,durationMs,artworkUrl,playedAt,isExternal,provider}`.
`playedAt` is an ISO 8601 timestamp. Unknown metadata is null, except unknown
title or artist text is empty. Artwork uses the same authenticated, same-origin
`/me/stats/art` route as stats. A known local library copy uses its native
Jellyfin item ID; otherwise a recording with an eligible enabled provider route
uses its `ext-` recording ID. Imported plays without a playable item have
`itemId: null`. Imports are included regardless of the taste imported-history
setting. Counts and history use the stats rule: completed plays, or stopped
plays that reached 30 seconds. Artist/album IDs and album/playlist/mix context
are omitted because the listening records do not retain those references.

`GET /allstarr/v1/me/plays?limit=50&before=` returns the dashboard's recent
Activity plays as an array of `{play,provider,artworkUrl}`. `play` contains the existing
play-trace fields: `id`, `startedAt`, `outcome`, `request`, `resolution`,
`received`, `delivery`, `differences`, `timing`, and `listenerId`. The timing
includes start phases; received/delivery data describes the recorded audio.
`provider` supplies display metadata and authenticated icon URLs when known;
library plays use `capabilities.library`. `artworkUrl` uses `/me/stats/art`.
The service clamps `limit` to 1..100. Use the last play's `id` as `before`;
an empty array ends paging. Traces follow the dashboard's existing seven-day,
500-sample retention, so they are separate from durable listening history.
`GET /allstarr/v1/me/plays/{id}` returns one `{play,provider,artworkUrl}` detail; the app
can also derive the dashboard's copied diagnostics from that detail. Missing,
expired or another listener's IDs return 404, including paging references.

The Recap import-status panels are available through
`GET /allstarr/v1/me/history/imports` (up to 50 recent imports),
`GET /allstarr/v1/me/history/imports/{id}` (one import), and
`GET /allstarr/v1/me/history/imports/recheck` (stored match-quality summary).
They reuse the dashboard's preview/progress and recheck schemas. Another
listener's import ID returns 404. Reading an expired preview leaves its state
unchanged. Upload, confirm, delete, unmatch and all other writes are unavailable.

All `/me` reads are personal, including for administrator tokens, and use
`Cache-Control: private, no-store` for JSON responses.

Browser clients may send `X-Allstarr-*` and `Idempotency-Key` headers. Responses expose
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
| `provider` | Serving source `{id,name,iconUrl,glyphUrl}`; own-library playback uses the same provider object as `capabilities.library`. Icons use the authenticated `/allstarr/v1/providers/{id}/icon` route; unavailable icons have a null URL. |
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
| `provider` | Catalog source `{id,name,iconUrl,glyphUrl}`; library items always use `capabilities.library`. An unavailable icon has a null URL. |
| `audio` | Measured `{codec,bitDepth,sampleRate,bitrate}`, or `null`. Catalog format advertisements are excluded; bitrate is in bits per second. |
| `explicit` | `true`, `false`, or `null`. |
| `saved` | Whether the listener has hearted the item. |
| `keep` | `{state,progress}`. State is `none`, `inProgress`, `kept`, or `failed`; progress is null when no measurement is stored. |
| `match` | Effective `{state,confidence,authority}`, or `null`. Authority is `automatic`, `household`, or `personal`, with personal choices taking precedence. |
| `lyrics` | Stored `{available,format,source}`. Format is `word`, `line`, `plain`, or `null`. |

Song details additionally expose optional `playCount` and `lastPlayedAt` for
the signed-in listener, including imports and plays of the same recording
through other item IDs. Unplayed songs have `playCount: 0` and omit
`lastPlayedAt`. These values come from Allstarr's listening records, including
recording and indexed-library links, rather than Jellyfin's user play counts.
Unresolved imports without such a link cannot be assigned to an item. Album
and artist details omit both fields.

`provider` is always present for library items in details, mix items, local match
candidates and the current local match, and playback facts. Apps must keep using
`isExternal` to distinguish library items (`false`) from service items (`true`);
the presence of `provider` does not indicate an external service.

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

A normal GET also warms the provider caches. Known retained, embedded, sidecar,
and cached provider lyrics can return immediately. Otherwise eligible providers
run concurrently, with word timing preferred over line timing, then plain text;
the listener's source order breaks ties. A saved song choice takes precedence.
The response uses the best result available within the lookup budget, or returns
earlier when no unfinished source can improve it. If no source has answered yet,
404 can be temporary: background completion can make lyrics available later.

The `LyricsLookup` configuration section controls `ResponseBudgetMilliseconds`
(default 1500), `SourceTimeoutMilliseconds` (1000),
`BackgroundTimeoutMilliseconds` (15000), and `Concurrency` (4, maximum 4).
These budgets cover lyrics lookup after item access and metadata resolution.
Concurrent requests for the same listener, song, and source policy share work.
Up to four lookups retain background work at once. Started provider reads continue
after the response, within their source timeout and the overall background cap,
and store successful results in the existing provider caches. A later improvement
publishes `lyrics.updated` with the existing `{itemId,revision}` payload; refresh
the lyrics and sources on that event. A separate prefetch route is unnecessary:
call the ordinary GET when the next song is known.

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

`GET /allstarr/v1/items/{itemId}/lyrics?sourceId=<id>` reads a candidate's full
lyrics in the same rich shape, with the same item access checks as source review.
It does not pin, publish client events, or change the effective lyrics. Unknown
or unavailable versions return 404 `not_found`; previews use `Cache-Control: no-store`.
Omit `sourceId` to read the effective lyrics.

`GET /allstarr/v1/items/{itemId}/lyrics/sources` lists candidate versions,
their formats, a text preview of up to four lines and 1,000 characters, the
chosen source and layer (`personal`, `household`, or `automatic`), and the
current revision. A listed candidate has `state` `ready` or `pending`.
Versions with no letters or digits, and lookups that found nothing, are omitted.
Pending candidates have null format and preview;
`chosen` is null when no lyrics can be selected. Each listed candidate also includes
`providerId`, `providerName`, and nullable provider metadata: `title`, `artist`,
`album`, and `durationMs` (an integer in milliseconds). Ready candidates are
ordered by `rank`, best first, with pending candidates after them. Lists contain at
most five versions per provider and twenty candidates overall. Source discovery
uses the same response budget and returns the sources known so far. Pending
sources can become ready on a later read as background lookups fill the caches.

Candidates also have optional `score`, `rank`, and `reasons` fields. `score` is
between 0 and 1 and uses the exact scorer used for automatic selection, including
a manual source when its text is usable. `rank`
is a unique position among ready candidates, starting at 1 for the best;
equal scores prefer the richer timing, then the provider's default version, then
the order the versions were found. Pending candidates have null score, rank, and
reasons. Personal and household
pins override automatic selection without changing scores or ranks. Without a pin,
`chosen` is the best ready candidate automatic selection can use.

Clients should prefer `rank` for ordering and fall back to their own ordering
when these fields are absent. Scores compare versions of this recording; they
are not confidence percentages across different songs. Real word timing scores
above line timing, then plain text. A cue covering several words, or several words
sharing one timestamp, does not count as real word timing. A single word with an
explicit timed duration does. Timing fit uses the matched recording's duration,
falling back to the item's precise duration or duration in seconds. It uses the
last timestamp in the cached body, excluding inferred line ends. A timestamp
below 60% of the duration or beyond the duration by more than 2 seconds or 2%
(whichever is greater) receives a penalty, so fitting line timing can outrank
mismatched word timing. Unknown duration adds no fit adjustment. Scoring loads no
additional lyric bodies and keeps the existing response budget.

`reasons` contains short machine-readable strings: `synced_words`, `synced_lines`,
or `plain_text`, with `duration_fits` or `duration_mismatch` when duration can be
checked. Clients should tolerate additional reason strings in future responses.

Version IDs use `<providerId>:<versionKey>`, preserving the provider's record ID
when available. Otherwise the key is the first sixteen lowercase hexadecimal
characters of SHA-256 over normalized title, artist, album, and duration metadata.
Normalization applies Unicode compatibility normalization, lowercase, and collapsed
whitespace. Singleton providers may retain a plain provider ID. LRCLIB search
returns multiple lyric records; the other current lyrics adapters return one
lyrics result per lookup. lrc.red and Paxsenix can search several metadata
matches, but their existing lyrics fetches resolve one record. Existing caches, request budgets, and singleton fetch
counts are preserved; old best-only LRCLIB cache entries refresh once.

```json
{
  "sources": [{"sourceId":"lrclib:123","providerId":"lrclib","providerName":"LRCLIB",
    "title":"Two Moons Over Ohio (Live)","artist":"Example Artist","album":null,
    "durationMs":271000,"format":"line","preview":"Hello\nEcho","state":"ready",
    "score":0.7,"rank":1,"reasons":["synced_lines","duration_fits"]}],
  "chosen": {"sourceId":"lrclib:123","layer":"personal"},
  "revision": {"personal":null,"household":null}
}
```

Pin a version ID unchanged, or use a plain provider ID to select that provider's
best version. A missing pinned version falls back to the same provider's best
available version, then the usual automatic rules. The saved pin and revision
remain intact. Playback, Jellyfin lyrics, and lyrics search honor the same choice.

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
Choosing a source that no longer offers the song also returns 409. A change to
that version's text does not return 409. A missing or null `expectedRevision` returns 400
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
`iconUrl`, `glyphUrl`), `isExternal`, `confidence`, and `reasons` containing the same
saved evidence shown in the dashboard. Unknown metadata is null. Own-library
candidates and the local `current` choice use `capabilities.library` as their
provider. `current` is null when no candidate is selected, including after rejection.

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
and agree with `itemId` when both are provided. Both hyphenated GUIDs and
32-hex header forms are accepted. Unknown, expired, or foreign IDs
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
validation and saved preference. Reads do not generate listening-based mixes.
Connected personal accounts also contribute service playlists automatically,
without adding them to Jellyfin. Only service feeds added to apps are listed;
unadded service feeds are omitted. Discovery is cached per account for six hours;
a failed refresh retains the previous list. Spotify discovery covers saved
service playlists through the existing web-player client. Apple Music reads
personal recommendations and library playlists; Last.fm and ListenBrainz use
their existing selections. Synthetic listeners cannot read mixes.

Each mix has `mixId`, `kind`, `name`, `description`, `state`, `itemCount`,
`durationMs`, `updatedAt`, `coverUrl`, `inApps`, `playlistId`, `section`, `source`,
and `sourcePlaylistId`. Service feeds have `kind: "external"`,
`section: "yourServices"`, and `source` equal to the provider id. A synced
playlist and its discovered feed share one card. Other mixes have a null
`source` and `sourcePlaylistId`. `kind` preserves
the dashboard's stable IDs, including `on-repeat`, `rewind`, `release-radar`,
`discover-weekly`, `daily-1` through `daily-4`, mood IDs, `custom`, and `external`.
The typed state maps dashboard readiness to `ready`; pending linked-playlist
matching to `building`; requests for more plays, listening, or history to
`needs-listening`; and other unready cards to `empty`. Uninitialized cards and
cards without playable songs are `empty`. For a discovered service feed,
`ready` means the playlist is available to open; its playable songs are checked
when its items are read.

`itemCount` and `durationMs` describe the stored songs visible through the
listener's library access and listening policy. Unknown song durations add
zero to the total. For an unopened service feed, `itemCount` is the service's
song count when supplied (otherwise zero), and duration is unknown (zero).
Opening its items loads songs through the selected account, applies listening
policy, prefers matching local copies, and checks streaming availability.
`updatedAt` is null when the source supplies no update time. `inApps`
requires a published generation, the listener's publication choice, and the
server's mix publication setting. Spotify and Apple Music feeds follow the
existing playlist link's enabled state. `playlistId` is its Jellyfin playlist ID,
or null when unpublished.

`GET /allstarr/v1/mixes/{mixId}/items` returns `{mixId,updatedAt,items}`. Items
retain mix order and include `itemId`, `title`, `artist`, `album`, `albumId`,
`artistId`, `durationMs`, `isExternal`, `provider`, and the stored per-song
`reason`. Provider metadata has the same `{id,name,iconUrl,glyphUrl}` shape as item
details; library songs use `capabilities.library` as their provider. Missing
relationships and durations are null. Library songs no longer visible to the caller's token are omitted.
Song, album, and artist IDs use the Jellyfin surface, including native
library IDs, service `ext-...` IDs, stored recording IDs, and Apple catalog
aliases. Unknown mixes and service feeds not added to apps return 404 `not_found`. These JSON routes use `Cache-Control: no-store`.

`GET /allstarr/v1/mixes/{mixId}/cover` serves the same cover as the dashboard
and the mix's playlist image in Jellyfin and Subsonic apps: a JPEG 2×2 of the
covers of four songs from different albums (one cover when the mix has fewer),
or, for a mix without song art, a PNG gradient in colours of its own. The list's
`coverUrl` adds `?v=` with the cover's version, and a request for the current
version may be kept for a year (`immutable`); the URL changes when the cover
does, including when the mix's songs change. A collage still missing a picture
is sent with a one-minute cache and no version, so apps fetch it again. Other
requests use a private cache with revalidation and return 304 for a matching
`If-None-Match`. Unknown mixes, unadded service feeds, and other listeners' mixes return 404,
including conditional cover requests.

`GET /allstarr/v1/items/{itemId}/appearances` (`appearances`) lists where an
album's or song's songs appear: the listener's mixes and the playlists Allstarr
syncs for them. It returns `{itemId,songCount,appearances}`. Each appearance has
`kind` (`mix` or `playlist`), `id`, `title`, `subtitle`, `coverUrl`,
`matchedSongCount`, and `playlistId`. A mix's `id` is its `mixId` and its cover
is the mix cover route; `playlistId` is set when the mix is in the listener's
apps. A playlist's `id` and `playlistId` are its Jellyfin playlist ID, and its
subtitle names the service it comes from. Playlists the listener made in Jellyfin
themselves are not included. Results are ordered by `matchedSongCount`, then
mixes before playlists. Library IDs, service `ext-...` album and song IDs, and
albums built from saved playlist songs are accepted; other item types return
400. An external song the listener has not stored and the provider does not
know returns 404, as do other unknown items. Responses use `Cache-Control: no-store`.

`GET /allstarr/v1/items/{itemId}/similar?limit=12` (`similarAlbums`) returns
`{itemId,basedOn,albums}` for an album. Allstarr asks the album's own service,
then Deezer, then the listener's other sources for the artist's related artists, and
picks one album for each: the listener's most played album by that artist when
the library has one, otherwise the artist's newest album from the service
(singles and compilations only when nothing else exists). `basedOn` names that
service, or is null when none had related artists. Each album has `id`,
`title`, `artist`, `artistId`, `year`, `songCount`, `coverUrl`, `source`
(`library` or the service ID), `isExternal`, and `provider`. Service albums use
`ext-...` IDs that open and play through Allstarr. `limit` is 1–24. Results are
kept for six hours for that listener. Another listener does not receive them,
and a change to clean-only or the metadata sources that listener allows is read
again. The server keeps a bounded number of these answers. Songs and other item types return 400.
Responses use `Cache-Control: no-store`.

Jellyfin's own `Items/{albumId}/Similar` and `Albums/{albumId}/Similar` return
the same albums, in the same order, as ordinary album items: library albums
exactly as Jellyfin describes them and service albums with `ext-...` IDs. Each
item also carries `AllstarrSource` (`library` or the service ID) and
`AllstarrProvider` (`{id,name,iconUrl,glyphUrl}`), and the response carries
`AllstarrBasedOn`. Songs and artists keep Jellyfin's usual Similar answer.

Playlists Allstarr syncs from a service (`playlistKinds`) carry
`AllstarrPlaylistKind` on their Jellyfin item, in playlist lists, `Items?Ids=`
and `Items/{id}`: `Yours` for a playlist the listener made, `Feed` for one the
service makes for them (Discover Weekly, Daily Mix, Top 50, ListenBrainz and
Last.fm recommendations), and `Followed` for another user's playlist. A
playlist whose owner isn't known yet is `Yours`. Allstarr's mixes and playlists
made in Jellyfin have no kind. Every kind lists and plays as a playlist, but
songs from `Feed` and `Followed` playlists don't fill the Albums, Songs and
Artists lists unless the listener turns on Show songs in my library for that
playlist. Other Jellyfin apps ignore the field.

Mixes in the listener's apps are Jellyfin playlists too (`AllstarrPlaylistKind`
`Mix`, or the synced kind for a ListenBrainz or Last.fm mix), so apps can list
them, read their songs with `Playlists/{id}/Items` and show
`Items/{id}/Images/Primary` like any playlist. Their items also carry
`AllstarrMixKind` (`daily-1` … `daily-4`, `discover-weekly`, mood IDs, `custom`,
`external`), `AllstarrMixSection` (`madeForYou`, `moods`, `custom`,
`yourServices`), `AllstarrMixState` (`ready`; mixes that aren't ready aren't
playlists), `AllstarrMixSource` for a synced mix, and `DateLastContentAdded`
when its songs last changed. The image tag changes whenever the cover does.
Each song in a mix's `Playlists/{id}/Items` carries `AllstarrReason`, why the
mix picked it. Mixes that aren't in the listener's apps yet, and the For you
order, still come from `GET /allstarr/v1/mixes`.

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

Favouriting an artist through Jellyfin (`POST /UserFavoriteItems/{artistId}`)
follows it the same way, and removing the favourite unfollows it, so one heart
works in every app. Artists favourited before, or in an app that went straight
to the media server, are followed the next time the listener's releases are read
(checked at most every ten minutes, ten new library artists at a time), so
`followedArtists` keeps up with artist favourites.

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
| `libraryAddition.updated` | `{id,itemId,requestedItemId?,unit,state,reason,libraryItemId?}` for a change to the listener's promotion, including a native library match after placement. Refresh the additions list. |
| `libraryRequest.updated` | `{id,itemId,requestedItemId?,unit,state,reason,libraryItemId?}` for the listener's request, including approval, decline, cancellation, and linked promotion progress. Refresh the additions list. |
| `match.updated` | `{itemId,state}` with the effective match state used by the match route. Effective personal choices hide unrelated household and automatic changes. |
| `lyrics.updated` | `{itemId,revision}` after a pin/clear or an effective lyrics change. |
| `mix.updated` | `{mixId,playlistId}` after regeneration, saving, deletion, or another saved mix change; `playlistId` is the mix's Jellyfin playlist id. |
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

`GET /allstarr/v1/search/lyrics?q=<text>&limit=20` searches lyrics already held
by Allstarr. Trimmed queries must contain 2–200 characters; `limit` is 1–50
(default 20). Invalid values return 400 `invalid_request`. Empty searches return
200 with `{"results":[],"truncated":false}`. Responses use `Cache-Control: no-store`.
The capability flag is `features.lyricsSearch`.

Each result has `itemId`, `title`, `artist`, `album`, `albumId`, `isExternal`,
`line` (`text`, `startMs`, `endMs`, `synced`, `before`, `after`), `highlights` (each with
`start` and `length`), `score`, and `source` (`id`, `name`). IDs use the same
native and external IDs as Jellyfin Items. Unknown album IDs, neighbouring
lines at the edges, and untimed lyric timestamps are null. `line.synced` is true
when `startMs` is available, including zero; clients can offer playback from that
line. If the selected source has no timing, search borrows `startMs` and `endMs`
from a visible synced source for the same item or canonical recording. It matches
normalized line text and uses the occurrence nearest the selected line's position.
The selected source, text, highlights, and ranking stay the same. Without a
matching timed line, `synced` is false and timestamps remain null. Highlights use
UTF-16 offsets within `line.text`; they never extend into neighbouring lines.
Scores range from zero to one. The response contains one best line per song,
ordered by score, and `truncated` indicates additional matching songs.

Matching ignores case, accents, punctuation, apostrophe style, and whitespace.
Phrases can span into the next line; those results identify the first line.
Exact phrases rank above all query words in one line, followed by partial
query-word and word-prefix matches. Search applies the listener's effective
lyrics pin, automatic source order, and timing preference, just like the lyrics
route.

Search caches listener visibility and source availability for up to five seconds.
Lyrics-index, library-change, and settings-change versions invalidate that cache;
current library permissions remain part of its key, and native hits still pass the
caller's batched Items check. Kept-copy availability checks are batched and cached
for up to five seconds. Debug logs record stage durations and result counts without
query text or listener data.

Search reads a separate, rebuildable SQLite FTS index. It makes no lyric search
or metadata requests to music services. Completed fetches, lyrics reads, and
source reviews update stored source variants. Successful cache writes notify a bounded watch list of
known song lookups and queue stored-content refreshes, including writes made
outside the lyrics route. Retained-sidecar publication also queues a refresh.
A background worker indexes Jellyfin's local and embedded lyrics, including
sidecars exposed by its lyrics endpoint. It processes pages of 128 library
records, paces backend reads at 250 ms, and reuses the library index's change
fingerprints to skip unchanged songs. A bounded change queue expedites updates;
periodic metadata passes recover missed notifications and populate existing
stored lyrics after startup. New library lyrics become searchable after the
library index observes their source change and background processing completes.

Library hits require current permission to the indexed library and pass a
batched Jellyfin Items check with the caller's token. Album IDs come from
those visible items. Service hits require a provider available through the same routing checks used for playback.
Account-free and permitted shared-account lyrics for public catalog songs can
be shared; personal account content and retained lyrics stay scoped to their
listener. Other listeners' personal source content and pins are excluded. Cached account content is tied to the current
routed account revision; retained canonical IDs also require a playable source
or the caller's retained-file access. The index follows personal and household pin
changes immediately without rereading songs.

`GET /allstarr/v1/models/karaoke` returns metadata for the newest valid on-device
karaoke model: `{version,sha256,sizeBytes,computeUnits,format,url,license}` where
`url` is `/allstarr/v1/models/karaoke/{version}/file`. If no valid model is available,
it returns 404 in the API's standard `not_found` error shape. The capability flag
is `features.karaokeModel`.

`GET /allstarr/v1/models/karaoke/{version}/file` (and `HEAD`) streams the model file
with `Range` support (206 and 416), `ETag: "<sha256>"`, `Content-Type: application/octet-stream`,
`Cache-Control: private, max-age=31536000, immutable`, and `Content-Disposition: attachment; filename="..."`.
Unknown or invalid versions return 404 `not_found`. Model files stream from disk and
are never loaded into memory.
