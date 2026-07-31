# Jellyfin v12 music compatibility surface

Allstarr implements a deliberately music-only Jellyfin boundary. This contract is
audited against `apis/specifications/jellyfin/openapi-12.0.0.json`; an operation
that is not classified here is denied by default on the public Jellyfin port.

## Request flow

1. Classify the method, route, and any requested item types.
2. For opaque item routes, resolve the item with Allstarr's internal Jellyfin
   credential and require `Audio`, `MusicAlbum`, `MusicArtist`, `Playlist`, or
   `MusicGenre`. `DELETE /Items/{id}` has the narrower requirement that the
   resolved native item is a `Playlist`.
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
| Browse and search | `Items`, user `Items`, `Search/Hints`, `Artists`, `Albums`, `Songs`, `Genres`, `MusicGenres` | Untyped generic requests are constrained to music kinds. Search interleaves local and enabled provider results. |
| Recommendations | `Items/Latest`, `Items/Suggestions`, `UserItems/Resume`, instant mixes, similar items | Generic queries are constrained to music/audio. Opaque item routes require a verified music item. |
| Library discovery | `Library/MediaFolders`, `UserViews`, `Items/Root`, `Items/Counts`, `Items/Filters*` | Views and folders are filtered to the configured/detected music library. Non-music counts are zeroed. |
| Metadata and art | item detail, music item images, ancestors, collections, external IDs, remote images | Local opaque IDs are type-checked before proxying. External resources are synthesized by Allstarr. |
| Lyrics | audio/item lyrics and lyric-provider routes | Uses the provider-agnostic lyric orchestration path for external music. |
| Playlists | playlist read/write, item ordering, membership, sharing, instant mix, native deletion | Injected playlists are intercepted. Hybrid links resolve an exact scoped writable Jellyfin target; pure virtual/provider-only links are read-only. |
| Playback | `PlaybackInfo`, `Audio/.../stream`, universal audio, item file/download, bitrate test | Local audio is proxied. External playback info and stream URLs are synthesized by Allstarr. |
| User listening state | favorite, played, and item user-data routes | Permitted only after the referenced item is verified as music. |
| Client lifecycle | authentication, quick connect, capabilities, playback start/progress/stop/ping, logout, profile image, display preferences | Minimal authenticated client-control surface; remote-control/session enumeration is excluded. |

Jellyfin's anonymous bootstrap contract is preserved for public server info,
public users and user images, GET/POST ping, UTC time, Quick Connect
enabled/initiate/connect, Quick Connect authentication, and music artwork.
Quick Connect authorization still requires an authenticated user.

## Explicitly excluded

- Video, movies, shows, episodes, trailers, people, live TV, channels, and
  music videos.
- SyncPlay and remote-control commands for other sessions.
- Plugin, scheduled-task, startup, branding, notification, library-structure,
  system configuration/log/restart/shutdown, user creation/policy, and other
  administrative routes.
- Item deletion other than a backend-verified native playlist, metadata
  refresh, and item/image mutation through the public proxy.
- Lyric upload, deletion, and remote-result installation. Read-only local and
  provider lyric lookup remains supported.
- Live-stream opening/closing. Allstarr's external audio sources are direct HTTP
  media sources and do not advertise `RequiresOpening`.

## Qualification evidence

`allstarr.Tests/Fixtures/Protocols/jellyfin-openapi-qualification.json` is the
machine-readable 12.0 review record. It classifies all 364 operations:
32 client-control, 45 music-scoped, 36 requiring a verified music item, one
requiring a verified native playlist, and 250 denied by default. Dashboard
configuration APIs under `/web/ConfigurationPage(s)` remain denied even though
static Jellyfin web bootstrap assets are public.

`jellyfin-openapi-10.11-qualification.json` applies the same policy to all 388
operations in Jellyfin 10.11.11. It records the 25 version-only operations:
query-form artist instant mix and the old music-genre root, legacy audio HLS and
playing-item reports are allowed with their documented checks; video HLS,
network-share, recording, encoding-control, and other non-music operations are
denied. In total 124 operations are allowed and 264 are denied.

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
native target after exact tenant, owner, backend, protocol, library, and
enabled-state resolution. Pure virtual and provider-only playlists return
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
output semantics are in `tools/tests/README.md`. The recorded live runtime is
Jellyfin 10.11.11. Jellyfin 12 is covered by the pinned OpenAPI and deterministic
contracts, but real 12.x runtime parity remains explicitly blocked until such a
runtime is available.

## Apple Music cold playback

For an uncached `apple-download` track, Allstarr opens the compatible sidecar's
`api/stream/{id}` response with `ResponseHeadersRead`. The sidecar
streams FFmpeg's FLAC stdout after Apple fetch/decryption instead of waiting
for a second complete converted file. The gateway returns FLAC, so Allstarr
relays those exact bytes immediately while teeing the
same bytes to a temporary file. Metadata resolution happens concurrently and is
used only when the completed cache file is published. A partial artifact is never
registered. Completed cache files support normal byte ranges and seeks.

`applemusic` and `apple-download` are canonical aliases on this path. An Apple ID
therefore never incurs an Apple-to-Apple Odesli translation.
