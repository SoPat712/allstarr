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

The proxy preserves normal Jellyfin authentication and relays unhandled routes. Integrated search, external streams, range requests, artwork, lyrics, favorites, playlists, playback sessions, and InstantMix have explicit compatibility handling, but a client may not expose all of them in its UI.

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

The Subsonic surface accepts normal query and form-post request styles and preserves XML or JSON responses. It supports integrated search, item lookup, streaming, cover art, structured lyrics, stars, playlists, playback observations, and catch-all relay. Some clients filter provider playlists out of their dedicated playlist screen even when the same results are visible through global search.

## External song labels and playback sources

Injected song titles use `[A]`, or `[A]/[E]` when explicit, including playlist source views, unresolved rows, and missing-local-item fallbacks. These labels do not imply that an unresolved row is playable. Native library titles stay unchanged. Existing external IDs remain compatible; the provider embedded in an ID identifies its catalog entry, not necessarily the provider serving its audio. Album, artist, and playlist relationships remain catalog-specific. Clients retaining an older playlist or queue response may need to reload it after an upgrade; a playlist reimport is not required for the title change.

For clients with a device identifier, playback first checks authorized cached copies, then tries configured streaming providers with an exact verified identity for the same recording. Manual source pins remain authoritative. Failed leases, transport failures, unavailable media, empty streams, and non-audio error pages can advance to another eligible source before audio starts. Authentication and policy denials do not bypass authorization, and an active response is never spliced together from multiple providers. A track without verified alternatives stays on its known source; playback does not run a speculative title search.

The administrator Home page distinguishes **Playing from**, **Cached from**, and an unconfirmed catalog source. Jellyfin song details can include the last opened source for that user/device, with its timestamp; whether a client displays `Overview` or the media-source name depends on the client. The client must request song details again after the stream opens to receive this observation; metadata fetched before playback cannot name a confirmed source. Musiver's in-player display of this information is not yet qualified. Stream responses also expose `X-Allstarr-Provider`, never account identifiers or signed URLs. Artwork is deliberately not stamped: cached/shared covers cannot reliably represent a per-listener playback route.

Byte-range continuation stays on the opened provider, identity, account, and requested quality. If the short-lived selection is unavailable (for example after a restart), the client must restart playback before seeking. Clients without a device identifier—including standard Subsonic requests that identify only the application name—retain exact-provider playback and seeking; they do not participate in cross-provider failover yet. HEAD probes do not change the reported playback source.

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
