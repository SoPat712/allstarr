# Jellyfin v12 music compatibility surface

Allstarr implements a deliberately music-only Jellyfin boundary. This contract is
audited against `apis/specifications/jellyfin/openapi-12.0.0.json`; an operation
that is not classified here is denied by default on the public Jellyfin port.

## Request flow

1. Classify the method, route, and any requested item types.
2. For opaque item routes, resolve the item with Allstarr's internal Jellyfin
   credential and require `Audio`, `MusicAlbum`, `MusicArtist`, `Playlist`, or
   `MusicGenre`.
3. Verify the caller's Jellyfin session in `JellyfinAuthFilter` (except Jellyfin's
   public bootstrap and public music-artwork routes).
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
| Playlists | playlist read/write, item ordering, membership, instant mix | Injected/virtual playlists are intercepted. Local playlist item responses are filtered to music types. |
| Playback | `PlaybackInfo`, `Audio/.../stream`, universal audio, item file/download, bitrate test | Local audio is proxied. External playback info and stream URLs are synthesized by Allstarr. |
| User listening state | favorite, played, and item user-data routes | Permitted only after the referenced item is verified as music. |
| Client lifecycle | authentication, quick connect, capabilities, playback start/progress/stop/ping, logout, profile image, display preferences | Minimal authenticated client-control surface; remote-control/session enumeration is excluded. |

## Explicitly excluded

- Video, movies, shows, episodes, trailers, people, live TV, channels, and
  music videos.
- SyncPlay and remote-control commands for other sessions.
- Plugin, scheduled-task, startup, branding, notification, library-structure,
  system configuration/log/restart/shutdown, user creation/policy, and other
  administrative routes.
- Generic item deletion, metadata refresh, and item/image mutation through the
  public proxy.
- Live-stream opening/closing. Allstarr's external audio sources are direct HTTP
  media sources and do not advertise `RequiresOpening`.

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
