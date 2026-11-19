# Jellyfin music surface

Allstarr exposes a deliberately music-only Jellyfin boundary. The policy is
checked against the Jellyfin 12.0.0 and 10.11.11 OpenAPI documents in
`apis/specifications/jellyfin/`. An operation outside the allowlist is denied by
default on the music-app port. Only Jellyfin 10.11.11 has been tested against a
live server; see [qualification evidence](#qualification-evidence).

## Request flow

1. Classify the method, route, and any requested item types.
2. For opaque item routes, resolve the item with Allstarr's internal Jellyfin
   credential and require `Audio`, `MusicAlbum`, `MusicArtist`, `Playlist`, or
   `MusicGenre`. Native item updates and deletion require a verified `Playlist`;
   these checks use the caller's credential when present.
3. Verify the caller's Jellyfin session in `JellyfinAuthFilter` (except Jellyfin's
   public bootstrap and public music-artwork routes). Verification uses
   `Users/Me`; the 10.11 API-key fallback may verify an explicit user only for
   native relay and never binds that declared user to an Allstarr actor. For
   query-key-only native file/download requests, where 10.11 returns 400 from
   `Users/Me`, authenticated `System/Info` verifies the key without creating a
   user or enabling synthesized/provider work.
4. Intercept Allstarr virtual resources or proxy the constrained request upstream.

The policy executes before the websocket proxy. `/socket` is the only websocket
route and Jellyfin itself must accept the forwarded client credential before
Allstarr upgrades the client connection.

## Supported music operations

| Area | Routes | Behavior |
| --- | --- | --- |
| Browse and search | `Items`, user `Items`, `Search/Hints`, `Artists`, `Albums`, `Songs`, `Genres`, `MusicGenres` | Untyped, nonrecursive root requests return native music library and playlist folders. Other untyped generic requests are constrained to music kinds. Search interleaves local and enabled provider results. |
| Recommendations | `Items/Latest`, `Items/Suggestions`, `UserItems/Resume`, instant mixes, similar items | Generic queries are constrained to music/audio. Opaque item routes require a verified music item. |
| Library discovery | `Library/MediaFolders`, `UserViews`, `Items/Root`, `Items/Counts`, `Items/Filters*` | Views and folders are filtered to the configured/detected music library. Non-music counts are zeroed. |
| Metadata and art | item detail, music item images, ancestors, collections, external IDs, remote images | Local opaque IDs are type-checked before proxying. External resources are synthesized by Allstarr. |
| Lyrics | audio/item lyrics and lyric-provider routes | Uses the provider-agnostic lyric orchestration path for external music. |
| Playlists | playlist read/write, item ordering, membership, sharing, instant mix, native deletion | Injected playlists are intercepted. Hybrid links resolve a target through the viewer’s backend read and edit permissions; pure virtual/provider-only links are read-only. Service-token writes to existing targets first verify that the viewer can edit the playlist. |
| Playback | `PlaybackInfo`, `Audio/.../stream`, universal audio, item file/download, bitrate test | Local audio is proxied. External playback info and stream URLs are synthesized by Allstarr. |
| User listening state | favorite, played, and item user-data routes | Permitted only after the referenced item is verified as music. |
| Client lifecycle | authentication, quick connect, capabilities, playback start/progress/stop/ping, logout, profile image, display preferences | Authenticated session enumeration and remote-control commands are relayed too; Jellyfin enforces its session permissions. |

Jellyfin's anonymous bootstrap contract is preserved for public server info,
public users and user images, GET/POST ping, UTC time, Quick Connect
enabled/initiate/connect, Quick Connect authentication, and music artwork.
Quick Connect authorization still requires an authenticated user.

After authentication, clients can discover music libraries with `Items` or
`Users/{userId}/Items` without a parent, search, or item-type filter. An omitted
`Recursive` value has the same root-discovery behavior as `Recursive=false`.
The response preserves each native music library and playlist folder and applies
pagination after filtering other library types. Native playlist-folder IDs remain
unchanged, and folder-child queries retain their music-only scope. Saved service
songs do not appear in library browsing; they appear in favorites views.

## Explicitly excluded

- Video, movies, shows, episodes, trailers, people, live TV, channels, and
  music videos.
- SyncPlay.
- Plugin, scheduled-task, startup, branding, notification, library-structure,
  system configuration/log/restart/shutdown, user creation/policy, and other
  administrative routes.
- Item updates or deletion other than backend-verified native playlists,
  metadata refresh, and image mutation through the public proxy.
- Lyric upload, deletion, and remote-result installation. Read-only local and
  provider lyric lookup remains supported.
- Live-stream opening/closing. Allstarr's external audio sources are direct HTTP
  media sources and do not advertise `RequiresOpening`.

## Qualification evidence

`allstarr.Tests/Fixtures/Protocols/jellyfin-openapi-qualification.json` is the
machine-readable 12.0 review record. It classifies all 364 operations:
41 client-control, 45 music-scoped, 36 requiring a verified music item, two
requiring a verified native playlist, and 240 denied by default. Dashboard
configuration APIs under `/web/ConfigurationPage(s)` remain denied even though
static Jellyfin web bootstrap assets are public.

`jellyfin-openapi-10.11-qualification.json` applies the same policy to all 388
operations in Jellyfin 10.11.11. It records the 25 version-only operations:
query-form artist instant mix and the old music-genre root, legacy audio HLS and
playing-item reports are allowed with their documented checks; video HLS,
network-share, recording, encoding-control, and other non-music operations are
denied. In total 134 operations are allowed and 254 are denied.

The same contract separately classifies synthesized IDs. Core detail, artwork,
playback-info, download/file, audio stream, lyrics read, similar/instant-mix,
and favorite routes are handled by Allstarr. Unimplemented editing,
remote-image, ancestors/collections, rating, played, and generic user-data
routes are denied for synthesized IDs rather than forwarding an Allstarr-only
identifier to Jellyfin.

External and virtual playlist IDs are separately checked. Pure virtual reads
are projected by Allstarr. Writable hybrid aliases use the same injected
projection for browse, item detail, definition, and entries; update, membership,
reorder, ACL, and instant-mix operations rewrite only the playlist ID to the
native target after exact viewer, backend, protocol, enabled-state, and
backend read/edit permission resolution. Pure virtual and provider-only playlists return
`409 Playlist is read-only`; unknown scoped links return 404. Native writes are
method/body/query preserving passthroughs. Native deletion is relayed only
after the backend reports `Type: Playlist`.

Injected Jellyfin playlists retain every published source position. Matched
local rows preserve the complete native Jellyfin item DTO, playable provider
rows expose synthesized media sources, and unmatched rows remain visible as
metadata-only `allstarr-unresolved-*` items with `PlayAccess: None`. All routes
that could play or download an unmatched row return 404 before any Jellyfin or
provider request.

Synthesized albums, artists, songs, genres, images, and instant-mix routes have
their own machine-readable supported/denied matrix. Resource types cannot be
substituted into sibling routes. Primary image paths support bounded resizing
and JPG/PNG/WebP output when overlays are zero; unsupported synthetic image
types, overlay rendering, and formats are denied instead of being falsely
relayed. Native long image routes remain byte/header-preserving relays.

Merged search hints preserve native objects and stable source order, then apply
the client `Limit` once to the final native-plus-external result.

The safe live comparison is `tools/tests/live_jellyfin_smoke.sh`; usage and
output are described in [the test tools guide](../../tools/tests/README.md). The
only live runtime tested so far is Jellyfin 10.11.11. Jellyfin 12 is covered by
the pinned OpenAPI document and fixed contract tests only, not by a live server.

## Apple Music cold playback

For an uncached Apple Music song, Allstarr uses the gateway's `api/audio/{id}`
route when the gateway advertises `audio-file`. The gateway streams the audio
while it prepares it: lossless sources arrive as FLAC and AAC sources as
fragmented MP4. This first response has no `Content-Length` or byte-range
support. It carries the catalog duration in `X-Allstarr-Duration-Seconds`, and
Allstarr adds FLAC duration guidance as the bytes arrive. Concurrent requests
for the same song, account and quality share one preparation. Allstarr waits
within the usual download-backed opening limit. A failure before the first
audio bytes returns an error, so another service can be tried; an account
mismatch stays an account error. A failure after audio starts ends the transfer,
and Allstarr does not cache the partial file.

Once preparation finishes, the gateway caches the complete file. Later requests
get its real length and byte ranges, so apps can seek freely. A HEAD request
prepares the file completely before it answers.

Older gateways use `api/stream/{id}`: an empty 10-byte ID3v2.4 tag followed by
FLAC. Allstarr strips the tag and can add duration guidance. These streams
become seekable once the song cache holds the complete file; HEAD reports only
known media headers. Managed downloads use `api/download/{id}`, which returns a
complete FLAC file.

The Apple Music service is `apple-musickit`. The older names `apple-download`
and `applemusic` map to it, so an Apple ID never needs a song-link lookup to
reach Apple.
