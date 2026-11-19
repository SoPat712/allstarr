# Client compatibility

Allstarr offers either a Jellyfin-compatible surface or a Subsonic/OpenSubsonic-compatible surface, one per deployment. An app can only use features it actually asks the server for, so search, offline indexes, lyrics, playlists, favorites and playback reporting differ between apps. An app that searches its own offline copy of the library shows service results only where it asks the server.

New apps built for Allstarr should use the [client API](../api/README.md). Existing apps use the compatibility routes below.

## Jellyfin apps

**Connect apps** offers these for a Jellyfin server, with setup steps for each:

- [Finer Player](https://monk-studio.com/finer) on iPhone
- [Musiver](https://music.aqzscn.cn/en/) on mobile
- [Feishin](https://github.com/jeffvli/feishin) on desktop
- [Finamp](https://github.com/finamp-app/finamp) on Android and iOS
- [Jellyfin Web](https://github.com/jellyfin/jellyfin-web) in a browser, at `/web/` on the music-client address
- [Symfonium](https://symfonium.app/) on Android (paid after a trial)

Allstarr keeps normal Jellyfin sign-in and relays the music routes it allows; everything else is refused by the [Jellyfin music surface](jellyfin-music-surface-v12.md) policy. Integrated search, service streams, range requests, artwork, lyrics, favorites, playlists, playback sessions and Instant Mix have explicit handling, but an app may not show all of them.

Similar items and Instant Mix for a service song, album or artist use the same mix builder as Subsonic: the service's own recommendations lead a song seed when it has them, then Last.fm suggestions for listeners with a connected Last.fm account, then the seed artist's albums and songs. Similar items for a service artist are that service's related artists, or none. Library seeds go to Jellyfin unchanged.

Library artist album lists and library album track lists add missing service releases after the albums and songs you own. Matched songs or a saved artist link connect a service artist to a library artist. Without one, a same-named artist must share an album you own before its discography is added. Service rows follow the listener's external-label choice; library rows keep their fields. If a service fails or adds nothing, Jellyfin's response is unchanged. This applies only to unfiltered views: play-state, genre, tag, year and other filters keep Jellyfin's own answer. Sorting keeps library rows in Jellyfin's order, followed by the added service rows. The album must already be known from an earlier item or browse response; library folders and playlist views never trigger a catalog lookup.

Search hides a service artist behind a library artist only when matched songs or a saved artist link connect them. A matching name alone is not proof. Search hints compare song, album and artist relevance before applying the result limit, and library results win ties, so unrelated artist or album rows can't push out an exact song match.

Saved service music does not appear in library browsing. It appears in favorites views.

## Subsonic and OpenSubsonic apps

**Connect apps** offers these for a Subsonic server:

- [Feishin](https://github.com/jeffvli/feishin) on desktop
- [Tempo](https://github.com/CappielloAntonio/tempo) on Android
- [Symfonium](https://symfonium.app/) on Android (paid after a trial)

Other Subsonic apps may work too; see [reporting a client problem](#reporting-a-client-problem).

The Subsonic surface accepts query and form-post requests and keeps XML or JSON responses. It handles integrated search, item lookup, streaming, cover art, structured lyrics, stars, ratings, playlists and playback reports, and relays the media server's other music methods unchanged. Methods outside music library access (video, podcasts, internet radio, shares, chat, user administration, starting library scans and jukebox control) return Subsonic error 70 with HTTP 404 and never reach the media server.

`star` and `unstar` handle service songs, albums, artists and service playlists through the shared favorites workflow. A starred service song queues the same kept-copy download as a Jellyfin heart. Mixed requests relay only the library IDs, keeping the other parameters and body. Service actions need a linked Allstarr user; for an unlinked user, mixed requests relay library IDs and return error 50 for the refused service actions. Requests with only service items return a plain `ok`. Library `id` favorites are also recorded in Allstarr; library `albumId` and `artistId` actions are only relayed.

Allstarr adds service content to these methods:

- `search2` merges service songs, albums, artists and playlists with the library, like `search3`; service albums and playlists appear as directory entries.
- `getStarred`, `getStarred2` and `getAlbumList2` with `type=starred` add the service songs, albums and artists you starred. Other album lists are the media server's own.
- `getPlaylists` adds Allstarr's own playlists, such as imported playlists and mixes added to apps.
- OpenSubsonic `getTranscodeDecision` answers direct play for service songs, so apps such as Feishin stream them without an error. Library songs, including owned copies of service songs, keep the media server's decision.
- `getTopSongs` keeps the library's own top songs first and fills the remaining room with service songs credited to that artist, alone or leading the credit, skipping titles already listed.
- `getArtist` for a library artist adds service albums the library doesn't have. Matched songs or a saved artist link come first; without one, a same-named service artist must share an album with the library.
- `getAlbum` for a library album adds the service songs it is missing when a service album has the same title, or the same title with an edition suffix such as "Deluxe Edition", and the same lead artist. Album and song counts are updated; library rows and fields are unchanged.
- `getAlbumInfo` and `getAlbumInfo2` for a service album return its notes, MusicBrainz ID, Last.fm link and cover when known.
- `getSimilarSongs` with a service song, album or artist ID, and `getSimilarSongs2` with a service artist ID, return a mix from that service: its recommendations for a song seed when available, then Last.fm suggestions for listeners with a connected Last.fm account, then the artist's albums and songs. `getArtistInfo` and `getArtistInfo2` for service artists list the service's similar artists, or return a valid empty answer when there are none.
- `setRating` on a service item saves the listener's rating in Allstarr.

For transcode decisions, similar songs and artist info, library IDs keep the media server's answer. For top songs, artists and albums, the media server's response passes through unchanged when services add nothing or are unavailable; an unavailable service mix returns an empty song list.

Some apps filter service playlists out of their playlist screen even when the same results show in global search.

## External song labels and playback sources

By default, songs added from services have `[A]` in their titles, or `[A]/[E]` when explicit, including playlist views, unresolved rows and missing-library-item fallbacks. In Jellyfin apps, service artist and album names also get `[A]`. `[A]` means content Allstarr added; it doesn't mean Apple Music or name the service that delivers the audio. A label doesn't mean an unresolved row can play. Each listener can turn the labels off and filter known explicit or clean songs in **Settings → Listening**. Unknown explicit status stays visible unless **Clean only** is in effect; Clean only needs proof that a song is clean, including songs from your own library. Library titles keep the media server's spelling. The service named inside an external ID identifies its catalog entry, not necessarily the service serving the audio. Apps that kept an older playlist or queue may need to reload it after an upgrade; a playlist reimport is not needed.

For apps that send a device identifier, or a Subsonic application name for that listener, playback first checks kept copies the listener may use, then picks streaming sources that are verified copies of the same recording. The listener's **Playback start** choice (Settings → Listening, with the household default in Settings → Sound quality) balances measured audio quality, recent start times and cached copies. Sources pinned by hand always win. A failed lease, network failure, missing media, empty stream or non-audio error page can move on to another eligible source before audio starts. Sign-in and policy refusals never bypass permissions, and a response is never stitched together from several services. Playback can find verified alternatives through a bounded recording lookup using metadata Allstarr already has; browsing never starts that work. Automatic fallback never picks **Manual only** or **Off** sources.

Each request tries at most four sources, sharing a 15-second start budget when alternatives are known. Original downloads, including the Download route Feishin uses, follow the same Playback start choice before the first response. Later byte ranges stay with the chosen service, account revision and original file. An explicit download from one service stays with that service and waits up to the ordinary 30-second operation timeout, not the longer download-preparation allowance. These start limits don't cut off audio or cache filling after playback opens. A failed source item cools down for 30 seconds, or for the reported rate-limit wait up to five minutes. Repeated network or media failures also count towards the source's health.

Extensions that download before streaming share one 15-second limit across availability checks, signed tickets, retries, backoff and quality fallbacks. Once real audio starts, the full transfer time applies, so downloading or converting an accepted song can carry on. Artwork and JSON responses don't count as audio. This limit stops silent waits; it doesn't guarantee that a service starts within a few seconds.

Stream responses carry `X-Allstarr-Provider` and `X-Allstarr-Route-Reason`, never account IDs or signed URLs. Library playback reports `native-local-library`; service playback reports priority or fallback, account kind, and cache or remote delivery. A problem report's playback details show the confirmed source (**Playing from**, or **Catalog source** when the stream hasn't confirmed it), the route and the seek behaviour. Jellyfin song details can include the last opened source for that user and device, with its time; whether an app shows `Overview` or the media-source name is up to the app. The app must ask for song details again after the stream opens to see it. Artwork is deliberately not stamped, because shared covers can't represent one listener's playback route.

## Seeking, ranges and conversion

Byte-range and time-offset requests first reuse a complete cached original from an allowed source, including when the first request is a range or Allstarr has restarted. Service health, cooldowns, rate budgets and sidecar failures don't block this cache read; permissions, source modes, account ownership and revisions still apply. Otherwise, a continuation prefers the encoding that was already opened; when that is missing or unavailable, normal source selection runs again. Requests with neither a device identifier nor a Subsonic application name keep exact-service playback and seeking. Subsonic application names identify a playback session within the listener's account, so apps should send a distinct device identifier when they can.

After a mid-stream drop or an eight-second stall, Allstarr can reconnect up to three times at the current byte offset, but only when both responses share a strong ETag, the same total length and a matching range. Otherwise the response ends without mixing encodings, and the next request skips the failed source. HEAD requests don't change the reported playback source. Original-file Download and File requests keep their own source choice for later ranges, so apps that play through those URLs can seek without recording a download as a play.

Service GET requests accept one byte range, including a suffix range; Allstarr checks the returned offset, end and length. A source that can't do ranges returns its full HTTP 200 audio without `Content-Range` or `Accept-Ranges`, so apps read and skip forward. A mismatched 206 from a service is rejected as a media failure. HTTP 416 is only for an offset past the end of a file of known length; malformed byte requests return 400. HEAD ignores ranges. Completed extension downloads serve ranges from their verified file and keep it for ten seconds after the last response closes. These checks follow [HTTP range semantics](https://www.rfc-editor.org/rfc/rfc9110.html#section-14).

An uncached service song can start playing before its final size is known; it doesn't claim a guessed `Content-Length` or range support. Once the complete copy is cached, Jellyfin and Subsonic apps get its exact length and can request ranges. Jellyfin item details for service songs don't advertise an estimated size or bitrate as if it were the delivered file. An app that needs random access on the very first play may still need the cached copy.

When **Cache played songs** is on, the first read starts shared read-ahead for every service. Another allowed request for the same service, account revision, quality and song reads the bytes already fetched instead of starting another full download. If the first request disconnects, the fill keeps running while other readers need it, or for a ten-second grace. A seek past the cached part opens its own upstream range when the source supports it; otherwise it waits up to eight seconds for that offset and a verified total length before sending range headers. A seek that can't open in time returns a retryable HTTP 504, and later reads stop after eight seconds without bytes. Ordinary cold playback has up to sixty seconds to prepare its first audio; a request from byte zero can stay HTTP 200 while the final length is unknown. Partial responses keep their declared length and fail on an early end. A buffering failure is never reported as 416. Library streams keep the media server's behaviour.

With **Cache played songs** on, playing a service File or Download response also keeps the complete original. A partial response can start one shared full-file fill through its chosen source; the requested range stays available while that fill opens. The fill can finish after the app closes its range, within the usual transfer and capacity limits. HEAD requests and unused responses don't start it. Original copies are cached separately, bound to their account revision, and never replaced by converted bytes.

Jellyfin `StartTimeTicks` (100-nanosecond ticks) and Subsonic `timeOffset` or `offset` (seconds) seek service playback. A time seek reuses complete cached audio when it can. An uncached request can seek by converting incoming audio while the original keeps filling the cache; MP4 files that can't stream still need a complete temporary file. Allstarr copies packets when the container allows it and re-encodes with the same codec only when needed. A completed seek file advertises the remaining length; a progressive conversion leaves out a guessed length and range support. `X-Content-Duration` carries the original duration when known, and `X-Allstarr-Start-Seconds` gives the requested position. Negative or malformed positions return 400; a position at or past the end returns 416.

Time seeks share the conversion limit and return HTTP 503 with `Retry-After: 5` when it is full. Completed-file seeks have a 150-second preparation limit, a 60-second conversion limit and a 512 MiB temporary-file limit. Temporary seek files are removed when their response closes. A rejected audio profile is logged with the reason, cleaned app and device names and the relevant audio settings, never the URL or credentials, and the reason is attached to the playback record used by problem reports.

App limits on codec, container, bitrate, sample rate, channels and bit depth can choose on-the-fly conversion instead of refusing a playable original. Jellyfin prefers accepted AAC targets; Subsonic follows its requested format and bitrate. PlaybackInfo and response headers describe what is actually delivered. An original that fits passes through unchanged, and the cache always keeps the verified original. A strict bitrate limit with no known source bitrate uses bounded conversion; unknown metadata alone never makes a song unavailable. HTTP 406 is only for a request with no supported direct or converted target.

Service audio is checked before response headers are sent, using the start of the file, measured format facts and a decoded sample. Complete cache files and durable downloads are also checked for decoded length and timeline. Download conversion checks declared file sizes and the decoded duration before encoding, so a complete-looking header can't hide missing audio. Short or cancelled transfers publish nothing, and normal fallback can try the next source.

Extensions that download first share one download across cancelled requests and concurrent ranges. When Tidal, Qobuz, Amazon or another extension needs post-processing, the final file must finish converting and pass its checks before it can serve ranges, because original offsets don't describe the converted file. A seek joins the running conversion and waits within the same eight-second limit; a seek that times out can retry without cancelling or repeating the conversion. Formats that can be delivered progressively can serve bytes earlier.

Sequential streams send response headers before reading the part of the file that gives duration hints. They can get duration from catalog metadata or a measured duration. Allstarr fills in missing FLAC sample totals, verified MP3 Xing/Info/VBRI frame counts and consistent single-track MP4 durations without changing the encoded length. Existing values stay as they are. CRC-protected MP3 tags, edited or fragmented MP4 files and other structures fall back to sequential playback. The Apple gateway's older `/api/stream` route starts with an empty ID3 tag, which Allstarr strips before delivery and caching. Duration hints set the timeline; real seeking still depends on available bytes and the app. See the [FLAC STREAMINFO specification](https://datatracker.ietf.org/doc/html/rfc9639#section-8.2) and the format handling in [FFmpeg MP3](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mp3dec.c) and [MOV](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mov.c).

Original Apple downloads can keep the ALAC M4A file instead of converting it to FLAC first. Range replay and cache checks use the returned file. A startup check can read up to 4 MiB when MP4 metadata and artwork come before the audio; the complete-file cache check still applies. See [Apple downloads](apple-download-provider.md) for how the gateway delivers playback.

## Playback reports and Home

Jellyfin start, progress and stop reports for service songs include a small audio item with the song's external ID and the service's title, artists, album and duration. Progress refreshes the existing session; reports for library items keep their original payloads. Song details sent by the app are not used for this item. This follows Jellyfin's [`PlaybackProgressInfo.Item`](https://github.com/jellyfin/jellyfin/blob/v10.11.0/MediaBrowser.Model/Session/PlaybackProgressInfo.cs) contract and its [session handling](https://github.com/jellyfin/jellyfin/blob/v10.11.0/Emby.Server.Implementations/Session/SessionManager.cs#L357). How the Jellyfin dashboard shows these sessions depends on the server and app versions.

Home moves the shown position forward between reports while the app says it is playing, and holds it when paused. It estimates for at most fifteen seconds after the last report, then holds that estimate with `~` and **Awaiting update**. A missing position or duration shows as a dash. Source checks and unrelated requests don't refresh the position. These are the app's reports; they don't prove that a seek or a lyric tap played correctly on the device.

## Similar songs and mixes

Similar songs and Instant Mix can use a connected Last.fm account, then the public [ListenBrainz similar-recordings dataset](https://labs.api.listenbrainz.org/similar-recordings) as a last resort. ListenBrainz needs the seed to have a MusicBrainz recording ID already, and needs no account. Both are on by default and can be turned off separately in **Settings → Mixes and discovery**; they don't affect scrobbling. The ListenBrainz lookup has a five-second limit, a bounded response size and a fifteen-minute cache. When neither helps, the artist fallback still works. Library seeds still use the media server.

Service mixes replace each service song with a library copy the listener can play, in the same position, keeping its full library metadata and the mix order. Up to four of these lookups run at once. If the library copy can't be read, the service song stays. Verified copies of the same recording collapse into one; matching names alone never do.

## Upcoming songs

Allstarr always prepares the next song in an app's queue; there is no setting. After 30 seconds of the current song, it looks up the next song's lyrics if it comes from a service, and, for an Apple Music song, fills its audio into the song cache. Audio is prepared only when **Cache played songs** is on and there is room. Nothing is prepared while playback is paused, at the **Data saver** quality, or when the app has already opened the next song. At most two listeners' songs are prepared at a time. If playback starts during preparation, it takes over the same transfer without opening another download. Preparation never changes what plays.

## Playback diagnostics

Administrators can read recent service playback starts at `GET /api/admin/diagnostics/playback-starts?limit=50`. The database keeps at most 500 requests for seven days, across restarts. Each record has a generated request ID, catalog labels Allstarr already had, the chosen or last tried source, the account kind, cache use, the times to response headers and first body write, and timed phases for routing, account, lease, source, checks, retries, segments and processing. Phases can overlap and must not be added together. A missing phase or label means it wasn't available, not zero. Headers and first byte describe the server's response, not when sound came out of the device.

Records show whether a request was opening, started, completed, failed or cancelled; one left open by a restart stays incomplete. Records store no URL, credentials, device ID, account ID, raw error or service payload, and don't appear in Activity. The `X-Allstarr-Playback-Request` response header names the record.

Signed-in users can read `GET /api/admin/play-traces?limit=50` and `GET /api/admin/play-traces/{id}` on the administration port. Listeners see their own requests; administrators see all. Traces share the same seven-day, 500-request limit. Service stream requests, including Feishin's Download route, keep the stored song details, match evidence, chosen source and account label, cache use, requested delivery limits, observed audio facts and tags, and start times. HEAD, inspection and preparation requests are left out. Reading traces makes no service calls.

- `GET /api/admin/play-traces/{id}/diagnostics` returns a redacted JSON document for a problem report, without listener, session, device, account and private match IDs, account labels or artwork references.
- `POST /api/admin/play-traces/{id}/report` takes a unique `requestId` and a `note` and reports the frozen playback details.
- `POST /api/admin/play-traces/{id}/version` saves a match through the normal match service, with `targetType`, a library `libraryTrackId` or `externalProvider` and `externalId`, and `authorityScope` (`personal`, or `household` for administrators).
- `POST /api/admin/play-traces/{id}/forget` clears the chosen manual match and resolves again, or rematches an automatic choice.

Personal choices belong to the caller. Changing an existing choice needs `expectedAuthority` with its ID and revision; a stale revision returns 409. Actions that need a missing source snapshot return 404. Traces never change listening history or matching by themselves.

## Reporting a client problem

Please include:

- the app name, version and operating system;
- whether Allstarr runs in Jellyfin or Subsonic mode, and the media server and its version;
- the exact action that failed;
- whether the song was from your library, a service, matched, or newly downloaded;
- XML or JSON response mode for Subsonic apps;
- a short, redacted log excerpt with the correlation ID;
- whether the same action works in the media server's own web app.

**Report a problem** in the dashboard can attach the playback details for you. Don't post passwords, cookies, API keys, tokens, signed media URLs or an unredacted `.env`. A reproducible report is welcome even for an app not listed here.
