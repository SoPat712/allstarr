# Lyrics sources

Allstarr looks for lyrics in one household order. Set it in **Settings → Music sources → Lyrics**. Listeners don't have their own order. Lyrics reach Jellyfin and Subsonic apps through their normal lyrics requests, and apps that use the [client API](../api/README.md).

## Default order

| # | Source | Needs |
| --- | --- | --- |
| 1 | Media server lyrics | Nothing; lyrics your media server already has |
| 2 | Kept LRC | Nothing; `.lrc` files beside downloaded or kept songs |
| 3 | Spotify | A connected Spotify account; see [Spotify](#spotify) |
| 4 | Apple Music | An Apple Music account and the optional [Apple gateway](apple-download-provider.md) |
| 5 | lrc.red | Nothing |
| 6 | LyricsPlus | Nothing; optional self-hosted instance |
| 7 | Musixmatch | Nothing; optional token |
| 8 | AMLL TTML DB | Nothing |
| 9 | NetEase Lyrics | Nothing |
| 10 | QQ Music Lyrics | Nothing |
| 11 | Kugou Lyrics | Nothing |
| 12 | LRCMux | Nothing |
| 13 | LRCLib | Nothing |
| 14 | Unison | Nothing |
| 15 | Genius | Nothing; plain lyrics only |
| 16 | SpicyLyrics | A free server key, connected in Sources → Accounts, and a known Spotify track |

Lyrics extensions you install join the end of the list. Drag sources to reorder them; the order saves as you drop. A saved order that lacks media server lyrics or Kept LRC gets them back at the top. Retired sources are removed from saved orders at startup.

## How a source is chosen

A version a listener picked for a song wins. Otherwise Allstarr tries sources in order. Only sources set to **On** in Sources → Services are tried automatically; **Manual only** sources answer only when someone picks them. A source that fails or has no lyrics doesn't stop the rest.

Order beats timing: an earlier source with plain lyrics wins over a later one with timed lyrics. Between equal sources, word timing beats line timing, and line timing beats plain text. Allstarr never invents timestamps.

Up to four sources are asked at once. A later source's answer waits up to 1.5 seconds for earlier sources, and returns sooner when none of them can answer. If nothing has answered after three seconds, the lookup moves on through the rest of the order. Sources with rate limits, credentials or challenge checks join the parallel lookups only after recent successful use; otherwise they are asked one at a time. When lookup capacity is busy, lyrics can be briefly unavailable instead of building an endless queue. Answers that arrive late still fill the cache for next time.

With **Clean only** in effect, lyrics come only from your media server or kept files, and only for songs proven clean.

### Saving a chosen version to the song's file

A listener's pick is theirs alone. To make an administrator's pick the lyrics of the song itself, turn on **Save a chosen lyrics version to the song's file** in **Settings → Music sources → Lyrics** (off by default). From then on, when an administrator picks another version for a library song, in an app or under **Wrong lyrics?** in Matches, Allstarr rewrites the lyrics in the song's file a few seconds later: line times in the lyrics tag, word times in `WORDLYRICS`, as for a song Allstarr added. Jellyfin is handed the same version, with its word times when it has them, so its own apps show it too; other media servers read the lines at their next scan. The **Wrong lyrics?** panel says when a choice will also be written to the file.

Only the lyrics tags change, and the file keeps its owner and permissions. The file must sit in one of the music folders under **Settings → Library**. The lyrics it held before are kept as a small file in `replaced-lyrics` in Allstarr's data folder. Removing the pick later does not put the old lyrics back.

### Media server lyrics

Jellyfin and Subsonic song requests check the song with the media server before asking other sources. When the media server's lyrics win, its original response is kept, including language, offsets and extra cues. If a Subsonic lookup fails and no other source answers, the media server's own response is returned.

Allstarr reads the [Jellyfin 10.11 lyrics format](https://github.com/jellyfin/jellyfin/tree/v10.11.0/MediaBrowser.Model/Lyrics) and [OpenSubsonic structured lyrics](https://opensubsonic.netlify.app/docs/responses/structuredlyrics/), keeping millisecond times.

### Kept LRC

**Kept LRC** reads the `.lrc` file beside a downloaded song or a household kept copy, including copies in a custom kept folder. Service songs use their exact source; your own songs use the listener's accessible library index. File names, cache paths and similar titles never link a lyrics file to a song.

A Personal account's downloads are readable only by that account's owner. Files are skipped, so another source can answer, when the account or user is disabled, the file was removed or is a symbolic link, the audio is missing, the text isn't valid UTF-8, or the file is larger than 2 MiB. Reading lyrics never writes either file. A global `offset` tag is applied once; positive offsets show lyrics sooner, as in OpenSubsonic. Kept lyrics stay available whichever source they first came from.

### LyricsPlus

LyricsPlus needs no account. It looks a song up by title, artists, album and duration. Without a server URL, Allstarr tries `lyricsplus.binimum.org`, `lyricsplus-seven.vercel.app`, `lyricsplus.prjktla.workers.dev` and `lyrics-plus-backend.vercel.app` in that order. Public instances come and go.

To use your own instance, save **Optional self-hosted URL** in **Sources → Services → LyricsPlus**. HTTP is fine for a private service. The URL must not contain credentials, a query or a fragment; a path prefix is kept. A saved instance is used on its own, with no fallback to public instances. Clear the field to go back to public instances. A version 2 `LYRICSPLUS_URL` comes over through the [legacy import](legacy-env-import.md).

LyricsPlus can return plain, line-timed and word-timed lyrics. Apps receive line timing as LRC with millisecond precision; Allstarr keeps the word timing it receives without making up word boundaries. Only successful answers are cached.

### Musixmatch

Musixmatch can get a token automatically, with no account. To use your own app connection, choose **Connect account** in **Sources → Accounts**, pick Musixmatch and paste its **Debug Info** or user token. Use a Personal account for one listener or Shared for the household. The token is stored encrypted; don't paste it into a URL or a support message.

Musixmatch checks the matched title and artist, then prefers word-timed (richsync) lyrics, then line-timed, then plain. A rejected token shows as needing attention on the account. Regional access depends on Musixmatch.

## Spotify

Spotify lyrics run inside Allstarr with a connected Spotify account. No separate container is needed.

1. In **Sources → Accounts**, connect or edit a Spotify account. Use Personal for one listener or Shared for the household.
2. Paste the `sp_dc` cookie, or the full Cookie header, into **Spotify browser session (sp_dc)**. It is stored encrypted; don't share it in URLs or logs.
3. Make sure the account is on and Spotify sits where you want it in the Lyrics order.
4. Use the account's **Test** to check its connection and lyrics. A working connection doesn't mean every song has lyrics.

Lyrics always need an account; they never use the anonymous session that public playlist imports use. An expired or rejected cookie shows as needing attention on the account, and other sources answer meanwhile. Cached Spotify lyrics are kept apart per account. What a free account or a region can get depends on Spotify.

The old `spotify-lyrics` container and Compose profile are retired. Old `spotify` or `spotify-lyrics` lines in `.allstarr-profiles` are ignored, and the next update removes the old container. `SPOTIFY_LYRICS_API_URL` is ignored. The legacy import still brings over a Spotify cookie from version 2; if the cookie lived only in the old container, paste it into the account yourself.

## Lyric translation for apps

**Translate lyrics for apps** in **Settings → Music sources → Lyrics** is on by default. Apps that use the [client API](../api/README.md) can ask for a translation; the result is shared with everyone who asks for the same lyrics. Allstarr tries Google, then MyMemory, then your own LibreTranslate server when you save its address in **Optional LibreTranslate URL**. The client API itself is off until you turn on **Client API for apps** in Settings → Apps.
