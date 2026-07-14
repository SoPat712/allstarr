# Client Compatibility

Allstarr exposes either a Jellyfin-compatible surface or a Subsonic/OpenSubsonic-compatible surface in one deployment. A client can only use features it actually requests from the server, so search, offline indexes, lyrics, playlists, favorites, and playback reporting differ between clients.

## Jellyfin Clients

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
