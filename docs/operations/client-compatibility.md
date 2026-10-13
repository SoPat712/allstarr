# Client Compatibility

Allstarr exposes either a Jellyfin-compatible surface or a Subsonic/OpenSubsonic-compatible surface in one deployment. A client can only use features it actually requests from the server, so search, offline indexes, lyrics, playlists, favorites, and playback reporting differ between clients.

## Jellyfin Clients

Dashboard sign-in uses `Authorization: MediaBrowser ...`. Jellyfin 12 rejects
the legacy `X-Emby-Authorization` login header with HTTP 400 even for valid
credentials. The live smoke suite can check dashboard login using `ADMIN_BASE`.

These clients have been used successfully with the Jellyfin surface:

- [Feishin](https://github.com/jeffvli/feishin) on desktop
- [Musiver](https://music.aqzscn.cn/en/) on mobile and desktop
- [Finamp](https://github.com/jmshrv/finamp) on Android and iOS
- [Finer Player](https://monk-studio.com/finer) on Apple platforms

The proxy preserves normal Jellyfin authentication and relays unhandled routes. Integrated search, external streams, range requests, artwork, lyrics, favorites, playlists, playback sessions, and InstantMix have explicit compatibility handling, but a client may not expose all of them in its UI. Similar items for a provider song and instant mixes seeded from a provider song, album or artist use the same mix owner as Subsonic: provider recommendations lead a song seed when the provider offers them, then Last.fm suggestions for listeners with a connected Last.fm account, followed by the seed artist's albums and songs. Similar items for a provider artist are the provider's related artists, or none when the provider offers none. Native seeds are relayed to Jellyfin unchanged.

Jellyfin library artist album lists and library album track lists include missing provider releases after owned items. Namesakes must share an owned album before their discography is added. Provider additions follow the listener’s external-label preference; native rows retain their fields. Provider failure or no additions leaves the backend response unchanged.

Jellyfin search hides provider artists whose names match a library artist, because that library artist’s page includes missing provider albums.

Jellyfin search hints compare song, album, and artist relevance before applying the shared result limit. Native results win ties, so unrelated artist or album rows cannot crowd out an exact song match.

## Subsonic And OpenSubsonic Clients

These clients have been used with a Navidrome or other Subsonic-compatible backend:

### Desktop

- [Aonsoku](https://github.com/victoralvesf/aonsoku)
- [Feishin](https://github.com/jeffvli/feishin)
- [Subplayer](https://github.com/peguerosdc/subplayer)
- [Aurial](https://github.com/shrimpza/aurial)

### Android

- [Tempus](https://github.com/eddyizm/tempus)
- [Substreamer](https://substreamer.org/)

### iOS

- Narjo
- Arpeggi

The Subsonic surface accepts normal query and form-post request styles and preserves XML or JSON responses. It supports integrated search, item lookup, streaming, cover art, structured lyrics, stars, playlists and playback observations, and relays the backend's other music methods unchanged. Methods outside music library access (video, podcasts, internet radio, shares, chat, user administration, starting library scans and jukebox control) return Subsonic error 70 with HTTP 404 and never reach the backend.

Subsonic `star` and `unstar` handle provider songs, albums, artists, and external playlists through the shared favorites workflow. Provider songs queue the same kept-copy download as a Jellyfin heart. Mixed requests relay only native IDs, preserving the remaining parameters and body data. Provider actions require a linked Allstarr user.

Allstarr adds provider content to these methods:

- `search2` merges provider songs, albums, artists and playlists with the library like `search3`; provider albums and playlists appear as directory entries.
- OpenSubsonic `getTranscodeDecision` answers direct play for provider songs, so clients such as Feishin stream them without an error. Native songs, including owned copies of provider songs, keep the backend's own decision.
- `getTopSongs` keeps the library's own top songs first and fills the remaining room with provider songs credited to that artist, alone or leading the credit, skipping titles already listed.
- `getArtist` for a library artist appends provider albums the library does not hold. They come only from a provider artist of the same name that shares at least one album with the library, so a namesake's discography is not mixed in; a library artist with no albums takes the same-named provider artist with the most albums.
- `getAlbum` for a library album appends the provider songs it is missing when a provider album has the same title, or the same title with an edition suffix such as "Deluxe Edition", and the same lead artist. Album and song counts are updated; native rows and fields are unchanged.
- `getSimilarSongs` with a provider song, album or artist ID, and `getSimilarSongs2` with a provider artist ID, return a mix built from that provider: its recommendations for a song seed when available, then Last.fm suggestions for listeners with a connected Last.fm account, then the artist's albums and songs. `getArtistInfo` and `getArtistInfo2` for provider artists list the provider's similar artists, or return an empty, valid artist info when there are none.

For transcode decisions, similar songs and artist info, native IDs keep the backend's answer unchanged. For top songs, artists and albums, the backend's response passes through unchanged when providers add nothing or are unavailable; an unavailable provider mix returns an empty song list.

Some clients filter provider playlists out of their dedicated playlist screen even when the same results are visible through global search.

## External song labels and playback sources

By default, injected song titles use `[A]`, or `[A]/[E]` when explicit, including playlist source views, unresolved rows, and missing-local-item fallbacks. These labels do not imply that an unresolved row is playable. Each listener can toggle these labels independently and filter known explicit or clean external tracks in **Listening preferences**. Unknown explicit status stays visible. Native library titles and objects stay unchanged. Existing external IDs remain compatible; the provider embedded in an ID identifies its catalog entry, not necessarily the provider serving its audio. Album, artist, and playlist relationships remain catalog-specific. Clients retaining an older playlist or queue response may need to reload it after an upgrade; a playlist reimport is not required for the title change.

For clients with a device identifier, playback first checks authorized cached copies, then tries configured streaming providers with an exact verified identity for the same recording. Manual source pins remain authoritative. Failed leases, transport failures, unavailable media, empty streams, and non-audio error pages can advance to another eligible source before audio starts. Authentication and policy denials do not bypass authorization, and an active response is never spliced together from multiple providers. A track without verified alternatives stays on its known source; playback does not run a speculative title search.

The administrator Home page distinguishes **Playing from**, **Cached from**, and an unconfirmed catalog source. Hovering the route detail exposes the safe route-reason code recorded for that stream. Native backend playback uses `native-local-library`; external playback reports provider priority or fallback, account class, and cache/remote delivery without exposing account identifiers. Jellyfin song details can include the last opened source for that user/device, with its timestamp; whether a client displays `Overview` or the media-source name depends on the client. The client must request song details again after the stream opens to receive this observation; metadata fetched before playback cannot name a confirmed source. Musiver's in-player display of this information is not yet qualified. Stream responses expose `X-Allstarr-Provider` and `X-Allstarr-Route-Reason`, never account identifiers or signed URLs. Artwork is deliberately not stamped: cached/shared covers cannot reliably represent a per-listener playback route.

Byte-range continuation stays on the opened provider, identity, account, and requested quality. If the short-lived selection is unavailable (for example after a restart), the client must restart playback before seeking. Clients without a device identifier—including standard Subsonic requests that identify only the application name—retain exact-provider playback and seeking; they do not participate in cross-provider failover yet. HEAD probes do not change the reported playback source.

An uncached external song can start progressively before its final encoded size is known; it does not claim a guessed `Content-Length` or byte-range support. Once a complete copy is in the managed cache, both Jellyfin and Subsonic clients receive its exact length and can request byte ranges. External Jellyfin item metadata does not advertise an estimated media size or bitrate as though it were the delivered file. A client that requires random access on the very first cold play may still need a completed cache copy; this is not a guaranteed Musiver-specific fix.

## Known Limitation

[Symfonium](https://symfonium.app/) uses an offline-first local index for search. It may not send the live search requests Allstarr needs in order to merge provider results, so provider discovery through that client is not considered compatible. Local backend playback can still be a separate question from integrated provider search.

## Reporting A Client Problem

Please include:

- client name, version, and operating system;
- `Jellyfin` or `Subsonic` deployment mode and backend/version;
- the exact action that failed;
- whether the item was local, virtual, matched, or newly downloaded;
- XML or JSON response mode for Subsonic clients;
- a short, redacted log excerpt with the correlation ID;
- whether the same action works in the backend's own web client.

Do not post passwords, cookies, API keys, tokens, signed media URLs, or an unredacted `.env`. A reproducible report is welcome even when the client is not listed above.
