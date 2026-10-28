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

Jellyfin library artist album lists and library album track lists include missing provider releases after owned items. Namesakes must share an owned album before their discography is added. Provider additions follow the listener’s external-label preference; native rows retain their fields. Provider failure or no additions leaves the backend response unchanged. Expansion applies only to unfiltered views; play-state, genre, tag, year and other selection filters retain the native response. Normal client sorting and display options keep the native rows in backend order, followed by missing provider rows. An album parent must already be known from an authenticated item or browse response. Unknown parents, library folders and playlist browses do not trigger a catalog detail lookup.

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

Subsonic `star` and `unstar` handle provider songs, albums, artists, and external playlists through the shared favorites workflow. Provider songs queue the same kept-copy download as a Jellyfin heart. Mixed requests relay only native IDs, preserving the remaining parameters and body data. Provider actions require a linked Allstarr user. For an unlinked user, mixed requests relay native IDs and return error 50 for the refused provider actions. Provider-only successful requests return a plain `ok` envelope. Native `id` favorites remain recorded locally; native `albumId` and `artistId` actions are only relayed.

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

By default, injected song titles use `[A]`, or `[A]/[E]` when explicit, including playlist source views, unresolved rows, and missing-local-item fallbacks. External artist and album names also use `[A]` in Jellyfin clients. `[A]` means Allstarr external content; it does not mean Apple Music or identify the service delivering audio. These labels do not imply that an unresolved row is playable. Each listener can toggle these labels independently and filter known explicit or clean external tracks in **Listening preferences**. Unknown explicit status stays visible. Native library titles and objects stay unchanged. Existing external IDs remain compatible; the provider embedded in an ID identifies its catalog entry, not necessarily the provider serving its audio. Album, artist, and playlist relationships remain catalog-specific. Clients retaining an older playlist or queue response may need to reload it after an upgrade; a playlist reimport is not required for the label change.

For clients with a device identifier, or a Subsonic application name scoped to the listener, playback first checks authorized kept copies, then selects streaming sources with an exact verified identity for the same recording. The listener’s [Playback start preference](../user-guide.md#listening-preferences) balances measured audio quality, recent start timings, and available cached copies. Manual source pins remain authoritative. Failed leases, transport failures, unavailable media, empty streams, and non-audio error pages can advance to another eligible source before audio starts. Authentication and policy denials do not bypass authorization, and an active response is never spliced together from multiple providers. Playback can establish verified alternatives through a bounded recording-availability lookup using already known recording metadata; browse requests do not initiate that work. Automatic fallback never selects Manual only or Off sources. Each request tries at most four sources. With known alternatives, source selection shares a 15-second start budget; `ProtocolExecution:SourceStartTimeoutSeconds` can adjust that limit from 1 to 150 seconds. Canonical original downloads, including the Download route used by Feishin, use the same Fast start, Balanced, or Best quality source race before the first response. Later byte ranges retain the selected provider, account revision, and original representation. An explicit provider download stays with that provider and uses the ordinary operation timeout (30 seconds by default) for readiness, rather than waiting through the longer download-preparation allowance. These start deadlines do not limit an audio response or cache fill after playback opens. Failed source items cool down for 30 seconds, or the reported rate-limit delay up to five minutes. Repeated transport or media failures also feed the existing source-health circuit.

The administrator Home page distinguishes **Playing from**, **Cached from**, and an unconfirmed catalog source. Hovering the route detail exposes the safe route-reason code recorded for that stream. Native backend playback uses `native-local-library`; external playback reports provider priority or fallback, account class, and cache/remote delivery without exposing account identifiers. Jellyfin song details can include the last opened source for that user/device, with its timestamp; whether a client displays `Overview` or the media-source name depends on the client. The client must request song details again after the stream opens to receive this observation; metadata fetched before playback cannot name a confirmed source. Musiver's in-player display of this information is not yet qualified. Stream responses expose `X-Allstarr-Provider` and `X-Allstarr-Route-Reason`, never account identifiers or signed URLs. Artwork is deliberately not stamped: cached/shared covers cannot reliably represent a per-listener playback route.

Byte-range and time-offset requests first reuse a complete cached original from an authorized recording source, including when the first request is a range or the service has restarted. Upstream health, circuit, rate-budget and sidecar failures do not block this complete-cache read; provider permissions, source modes, account ownership and revision checks still apply. Otherwise continuation prefers the previously opened encoding; when that selection is missing or unavailable, the gateway selects an eligible source again using the normal playback rules. Requests with neither a device identifier nor a Subsonic application name retain exact-provider playback and seeking. Subsonic application names identify a playback session within the listener account; clients should send a distinct device identifier when available. After a mid-stream interruption or eight-second stall, the gateway can make up to three bounded reconnect attempts at the current byte offset only when both responses share a strong ETag, exact total length and matching byte range. Otherwise the response ends without mixing encodings. The next playback or range request skips a failed source and can select another eligible source. HEAD probes do not change the reported playback source. Original-file Download/File requests retain a separate temporary source selection for later ranges, so clients that play through those URLs can seek canonical playlist tracks without recording a download as playback. The same account, revision, backend and original-file quality checks apply. External GET requests accept one byte range, including a suffix; the gateway checks the returned offset, end and declared body length. A source that cannot honor ranges returns its full HTTP 200 audio without Content-Range or Accept-Ranges, allowing clients to read and skip forward. A mismatched provider 206 response is rejected as an upstream media failure. HTTP 416 is reserved for offsets outside a known-length file; malformed byte requests return 400, and an unavailable remembered playback selection falls back through the normal source selector. HEAD ignores ranges. Completed extension download artifacts serve bounded ranges from their verified file and retain their transient workspace for a ten-second reattachment grace after the last response closes. Provider Test reports byte seeking only when a partial response matches its requested offsets and includes the total encoded size. An ignored range stays sequential even when the provider advertises seeking; a mismatched partial response fails the test. An unknown total is not evidence of full random access. These checks follow [HTTP range semantics](https://www.rfc-editor.org/rfc/rfc9110.html#section-14).

Provider playback accepts Jellyfin `StartTimeTicks` and Subsonic `timeOffset` (or `offset`) in seconds. A time seek reuses complete managed audio when available. An uncached request can seek by converting incoming audio while the original continues filling the shared cache; non-streamable MP4 input still needs a complete temporary file. The core seeks the local file with packet copying when the container preserves the remaining timeline, and uses the same codec to encode only when needed. Raw AAC duration is checked against stored packet times rather than a bitrate estimate. A completed seek file advertises the remaining encoded length; progressive conversions omit a guessed length and byte-range support. `X-Content-Duration` carries the original duration when known, and `X-Allstarr-Start-Seconds` identifies the requested position. Negative or malformed positions return HTTP 400; a position at or beyond the verified end returns 416. Byte-range playback and original File/Download delivery retain their existing behavior.

Time seeks share the local conversion capacity limit and return HTTP 503 with `Retry-After: 5` when it is full. Completed-file seeks retain their 150-second preparation deadline, 60-second conversion deadline and 512 MiB temporary-file limit. Progressive conversions verify initial output before sending headers and run within the request and source-stall limits. Completed cache files are pinned while used; temporary seek files are removed when their response closes. Rejected audio profiles log the reason, sanitized client/device names and the relevant audio settings without the request URL or credentials. The reason is also attached to the request trace and an existing scoped playback observation for problem reports.

Client codec, container, bitrate, sample-rate, channel and bit-depth constraints can select local streaming conversion instead of rejecting a playable original. Jellyfin prefers accepted AAC targets; Subsonic honors its requested format and bitrate. PlaybackInfo and response headers describe the delivered target. A fitting original passes through unchanged, and the managed cache always stores the verified original. A restrictive bitrate with no provable source bitrate uses bounded conversion; unknown metadata alone does not make the song unavailable. HTTP 406 is reserved for a request with no supported direct or conversion target.

Remote candidates are checked before response headers using a preserved audio prefix, measured format facts and a decoded sample. Complete cache and durable-download publication additionally verify decoded length and the recording timeline.

An uncached external song can start progressively before its final encoded size is known; it does not claim a guessed `Content-Length` or byte-range support. Once a complete copy is in the managed cache, both Jellyfin and Subsonic clients receive its exact length and can request byte ranges. External Jellyfin item metadata does not advertise an estimated media size or bitrate as though it were the delivered file. A client that requires random access on the very first cold play may still need a completed cache copy; this is not a guaranteed Musiver-specific fix.

In Cache storage mode, consuming a provider File or Download response also retains the complete original audio. A partial response can start one shared full-file fill through its selected source; the requested range remains available while that fill opens. A complete response supplies the fill directly. The fill can finish after the client closes its range, within the existing transfer and capacity limits. HEAD probes and unused responses do not start it. Original copies have a separate cache identity bound to their source account revision and are never replaced by playback-converted bytes. They follow the configured retention and size limits without creating a playback observation merely because a file was read. Native media-server files retain their existing path.

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

Similar-song and instant-mix suggestions can use a connected Last.fm account, then the public [ListenBrainz similar-recordings dataset](https://labs.api.listenbrainz.org/similar-recordings). ListenBrainz requires the listener’s seed to already have a MusicBrainz recording ID. Both fallbacks are enabled by default and can be disabled independently under Settings; these controls do not change scrobbling. No account or identity-discovery request is needed for ListenBrainz. The lookup has a five-second deadline, bounded response size and a fifteen-minute metadata cache. Missing identity, unavailable service or unresolved suggestions leave the existing artist fallback available. Native seeds still use the backend.

Provider mixes replace each provider song with a known, accessible library copy in the same position, retaining its complete native metadata and the recommendation or shuffle order. Native lookups run concurrently with at most four in flight. If that copy cannot be read, the provider song remains available. Verified recording aliases collapse; matching names alone never collapse songs.

Sequential streams send response headers before reading their duration-guidance prefix. They can receive duration guidance from catalog metadata or a supplied probed duration. The Apple gateway sends an immediate ID3 prelude while preparing media; the core strips it lazily. The shared core fills unknown FLAC sample totals, verified MP3 Xing/Info/VBRI frame counts and consistent single-track MP4 duration fields without changing the encoded length or sample tables. Existing values remain untouched. CRC-sensitive MP3 tags, edited or fragmented MP4 files and unsupported structures fall back to sequential playback. A FLAC ID3 prelude is removed before delivery and cache writes. Duration guidance establishes the timeline; actual seeking still depends on available bytes and client behavior. See the [FLAC STREAMINFO specification](https://datatracker.ietf.org/doc/html/rfc9639#section-8.2) and the format handling in [FFmpeg MP3](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mp3dec.c) and [MOV](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mov.c).


In Cache storage mode, the first body read starts shared read-ahead for every provider. Another authorized request for the same provider, account revision, quality and track can read flushed bytes without opening another full download. Disconnecting the first request leaves the fill running while other readers need it, or for a ten-second reattachment grace. A seek beyond the cached prefix opens an independent upstream range if the selected source supports it. Otherwise, it waits at most eight seconds for the requested offset and a verified total length before sending range headers. Seek opening and first-byte timeouts return a retryable HTTP 504; subsequent provider reads stop the connection after eight seconds without bytes. Ordinary cold playback retains its bounded sixty-second initial audio-preparation window; a byte-zero request can remain HTTP 200 while the final length is unknown. Partial responses preserve the declared range length and fail on premature EOF. A buffering failure is not an out-of-bounds 416. Unknown-length streams keep their full 200 response until their complete encoded length is verified. Native library streams keep their backend behavior.

Download conversion verifies declared file sizes and the duration of decoded input samples before encoding. A complete-looking container header cannot qualify missing audio fragments. Short or canceled transfers publish neither a stream nor a cache artifact, and ordinary pre-response failover can try the next source.

Download-backed extensions share their producer across canceled requests and concurrent ranges. If Tidal, Qobuz, Amazon or another extension requires post-processing, the final encoded file must finish conversion and validation before it can serve ranges; original offsets cannot describe the converted file. A seek joins the existing conversion and waits within the same eight-second opening deadline. The conversion keeps its original bounded producer deadline and ten-second idle grace, so a timed-out seek can retry without canceling or duplicating the conversion. Formats explicitly eligible for progressive delivery can serve flushed bytes earlier. These transport bounds do not guarantee a particular provider's startup or a client's seek performance.


External Jellyfin start, progress and stop reports include a minimal audio item with the synthetic song ID and the authorized provider's title, artists, album and duration. Progress refreshes the existing session; native item reports retain their original payloads. Client-supplied song metadata is not used for this external item. This uses Jellyfin's [`PlaybackProgressInfo.Item`](https://github.com/jellyfin/jellyfin/blob/v10.11.0/MediaBrowser.Model/Session/PlaybackProgressInfo.cs) contract and its [session metadata handling](https://github.com/jellyfin/jellyfin/blob/v10.11.0/Emby.Server.Implementations/Session/SessionManager.cs#L357). Dashboard display and session lifetime still require qualification with the deployed server and client versions.

Home reports the confirmed serving source and the observed seek behavior: verified byte access, a progressive cache reader, duration guidance, or a full stream relying on client buffering. A duration hint alone never proves random access. These observations belong to the exact serving request and account revision; a late prefix read cannot overwrite a newer stream. If a selection disappears or the serving account revision changes, continuation reauthorizes available sources and recovers without asking the client to restart playback.

Home advances the displayed position between fresh reports when the music app explicitly reports playing, and freezes the reported position when paused. It estimates at most fifteen seconds after the last position report, then holds that estimate with `~` and **Awaiting update**. Missing position or duration appears as a dash. Source checks and unrelated requests do not refresh the position clock. These observations describe app reports; they do not confirm that a lyric click or seek played correctly on the device.

## Upcoming songs

**Prepare upcoming songs** is a household setting. Off by default. Turn on only if your player waits until a song ends before requesting the next one. The count defaults to one song when enabled and can be set to two. Many players already request the next song before the current one ends.

When enabled, Allstarr prepares queued Apple songs after ten seconds of playback when track caching is allowed. A recent client request or an existing cache transfer suppresses preparation. If playback starts during preparation, it takes over the same cache transfer and stops the background preparation reader without opening another upstream download.

Authenticated administrators can read recent provider playback starts at `GET /api/admin/diagnostics/playback-starts?limit=50`. SQLite retains at most 500 requests for seven days, across restarts. Each record contains a generated request ID, available cached catalog labels, the selected or last attempted source, available account scope, cache use, response-header and first-body-write times, and bounded route, account, lease, source, verification, retry, segment and process phases. Durations use a monotonic clock; parallel and nested phases overlap and must not be added together. Missing phases or labels mean they were unavailable, not zero. No provider lookup is added to obtain labels. Headers and first byte describe the server response path, not audible playback at the device.

Opening, started, completed, failed and cancelled records distinguish unfinished work. An opening/started record left after a restart remains incomplete rather than claiming success. Diagnostics store no request URL, authentication material, device identifier, account ID, raw error or provider payload. These records do not appear in Activity. The response's `X-Allstarr-Playback-Request` header identifies its diagnostic record.
