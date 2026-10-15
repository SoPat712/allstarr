# Lyrics sources

Set the household source order in **Integrations → Routing → Lyrics**. Allstarr tries that order, prefers the first timed result and keeps the first plain result as fallback. An unavailable source does not prevent the remaining sources from being tried. Only enabled sources and accounts authorized for the listener can participate.

Listeners can save their own source order in **Listening preferences → Lyrics source order**. This takes precedence over the household order in both protocols. Saving display preferences alone preserves the existing order; **Reset to household defaults** removes personal choices and follows the current household settings again. Concurrent edits require reloading the latest preferences before saving.

**Media server lyrics** participates in the same movable order. It is first in the default order; an existing saved order is retained. Native Jellyfin and Subsonic song requests verify the requested song before querying other sources. When backend lyrics win, their original response is preserved, including language, offsets, enhanced cues and additional metadata. Plain backend lyrics remain a fallback while later sources are checked for timing. A failed Subsonic backend lookup keeps its original response if no other source succeeds.

Native parsing follows the [Jellyfin 10.11 lyrics DTOs](https://github.com/jellyfin/jellyfin/tree/v10.11.0/MediaBrowser.Model/Lyrics) and [OpenSubsonic structured lyrics](https://opensubsonic.netlify.app/docs/responses/structuredlyrics/). Verified times retain millisecond precision, and plain text is not assigned invented timestamps.

## LyricsPlus

LyricsPlus requires no account. It looks up a song using its title, artists, album and duration. With no server URL configured, Allstarr tries `lyricsplus.binimum.org` and then `lyricsplus.prjktla.workers.dev`. Public service availability can change; a configured source is not a promise that either instance is online or has a particular song.

To use your own instance, open **Integrations → Services → LyricsPlus** and save its optional self-hosted URL. HTTP is supported for a private service. The URL must not contain credentials, a query or a fragment. A path prefix is preserved. A configured instance is used exclusively; Allstarr does not send the lookup to public instances when it fails. Clear the field to use public fallback again. `LYRICSPLUS_URL` remains available as a bootstrap/import alias.

The [LyricsPlus V2 API](https://github.com/ibratabian17/LyricsPlus/blob/8b2e1fe3f92536158d471177f7f17f9820407c5b/docs/endpoints.md) supplies plain, line and word results. Allstarr exposes verified line timestamps as LRC with millisecond precision; it does not claim word-level timing after conversion. Successful responses are cached by endpoint configuration, metadata and requested format. Misses, malformed responses and failures are not cached as successful lyrics.

## Musixmatch

Open **Integrations → Accounts**, connect Musixmatch and paste the user token from the Musixmatch desktop application's debug information. Use a Personal account for one listener or Shared for the household. Allstarr stores the token through its encrypted account secret store. Do not paste it into a service URL, a public instance or a support message.

The native desktop lookup checks the matched title and artist, then prefers richsync lines, subtitle LRC and plain lyrics. Successful content is cached separately for the exact account, revision, secret reference, metadata and requested format. A rejected token needs attention in the account form. Allstarr does not create an anonymous Musixmatch session or fall back to website sign-in. The [desktop compatibility reference](https://github.com/Myzel394/navidrome-musixmatch-plugin/tree/22dc7c1e9c12ba7c900dd8abbd52c74efd9d4310/plugin/musixmatch) describes this unofficial endpoint; regional access and token validity depend on Musixmatch.

## Other sources

See [Spotify lyrics](spotify-lyrics.md) for encrypted Spotify account setup and migration from its retired container. Apple lyrics require the selected Apple account and optional gateway described in [Apple setup](apple-download-provider.md). LRCLIB and typed lyrics extensions use the same ordered resolver. Timed lyrics remain timed, and plain text is never given invented timestamps.
