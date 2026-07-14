# Deezer API Steering

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Scope

This document captures the Deezer Simple API details that matter for Allstarr provider work.

- Public catalogue reads use ordinary HTTP `GET` requests.
- Base URL: `https://api.deezer.com`.
- Request and response text is UTF-8.
- The documented quota is `50` requests per `5` seconds.
- Deezer docs describe public discovery access without identification for catalogue reads. OAuth and permissions apply when a request interacts with user data or mutates library state.

Keep Deezer API knowledge here when it affects provider search, metadata parsing, matching, playlist ordering, explicit-content handling, or rate limiting.

## Request Shape

The documented method shape is:

```text
https://api.deezer.com/version/service/id/method/?parameters
```

Common catalogue examples:

```text
https://api.deezer.com/album/302127
https://api.deezer.com/artist/27
https://api.deezer.com/playlist/908622995
https://api.deezer.com/search?q=eminem
```

Allstarr normally uses the current unversioned public paths under `https://api.deezer.com`.

## Pagination

List responses should be treated as paginated. Deezer documents these global parameters:

| Parameter | Meaning |
| --- | --- |
| `index` | Offset of the first object to return |
| `limit` | Maximum number of objects to return |

Examples:

```text
https://api.deezer.com/playlist/4341978/tracks?index=0&limit=10
https://api.deezer.com/playlist/4341978/tracks?index=3&limit=7
https://api.deezer.com/playlist/4341978/tracks?limit=2
```

Implementation guidance:

- Do not assume embedded `tracks` lists inside album or playlist detail responses are complete.
- Prefer dedicated tracklist endpoints when the detail object advertises more tracks than were embedded.
- Preserve Deezer page order for albums, playlists, and search results unless a caller explicitly requests different sorting.
- Follow Deezer pagination carefully and keep requests under the Deezer quota.

## Errors

Deezer documents API errors with an error type and numeric code.

| Constant | Type | Code |
| --- | --- | --- |
| `QUOTA` | `Exception` | `4` |
| `ITEMS_LIMIT_EXCEEDED` | `Exception` | `100` |
| `PERMISSION` | `OAuthException` | `200` |
| `TOKEN_INVALID` | `OAuthException` | `300` |
| `PARAMETER` | `ParameterException` | `500` |
| `PARAMETER_MISSING` | `MissingParameterException` | `501` |
| `QUERY_INVALID` | `InvalidQueryException` | `600` |
| `SERVICE_BUSY` | `Exception` | `700` |
| `DATA_NOT_FOUND` | `DataException` | `800` |
| `INDIVIDUAL_ACCOUNT_NOT_ALLOWED` | `IndividualAccountChangedNotAllowedException` | `901` |

Provider code should inspect API error payloads in addition to HTTP status codes.

## Search

Track search example:

```text
https://api.deezer.com/search?q=eminem
```

Optional search parameters documented for search methods:

| Parameter | Meaning |
| --- | --- |
| `strict=on` | Disable fuzzy mode |
| `order` | Result ordering |

Documented `order` values:

```text
RANKING
TRACK_ASC
TRACK_DESC
ARTIST_ASC
ARTIST_DESC
ALBUM_ASC
ALBUM_DESC
RATING_ASC
RATING_DESC
DURATION_ASC
DURATION_DESC
```

Search track fields that matter to Allstarr:

- `id`
- `readable`
- `title`, `title_short`, `title_version`
- `isrc`
- `duration`
- `rank`
- `explicit_lyrics`
- `preview`
- `artist`
- `album`

The search `artist` object can include image fields. The search `album` object can include cover fields.

### Advanced Search

Deezer documents advanced search fields inside the `q` parameter:

| Field | Meaning |
| --- | --- |
| `artist` | Artist name |
| `album` | Album title |
| `track` | Track title |
| `label` | Label name |
| `dur_min` | Minimum duration in seconds |
| `dur_max` | Maximum duration in seconds |
| `bpm_min` | Minimum BPM |
| `bpm_max` | Maximum BPM |

Examples:

```text
https://api.deezer.com/search?q=artist:"aloe blacc"
https://api.deezer.com/search?q=track:"i need a dollar"
https://api.deezer.com/search?q=artist:"aloe blacc" track:"i need a dollar"
https://api.deezer.com/search?q=bpm_min:120 dur_min:300
```

Search implementation guidance:

- Preserve Deezer ranking order by default.
- Use advanced fields when a search path needs more precision than plain text.
- `strict=on` is a precision tradeoff because it disables Deezer fuzzy mode.
- Track matching may use ISRC lookup before fuzzy title and artist search.

## Track

Track object example:

```text
https://api.deezer.com/track/3135556
```

Track fields relevant to Allstarr:

| Field group | Fields |
| --- | --- |
| Identity | `id`, `isrc`, `link`, `share` |
| Names | `title`, `title_short`, `title_version` |
| Playback and timing | `readable`, `duration`, `track_position`, `disk_number`, `preview` |
| Release and scoring | `release_date`, `rank`, `bpm`, `gain` |
| Availability | `available_countries`, `alternative` |
| Explicit metadata | `explicit_lyrics`, `explicit_content_lyrics`, `explicit_content_cover` |
| Related metadata | `contributors`, `artist`, `album`, `md5_image` |
| Media integration | `track_token` |

The full track `artist` object may include pictures, fan and album counts, radio support, tracklist, and contributor role. The full track `album` object may include cover sizes and release date.

### Explicit Content Values

For track explicit-content fields documented here:

| Value | Meaning |
| --- | --- |
| `0` | Not explicit |
| `1` | Explicit |
| `2` | Unknown |
| `3` | Edited |
| `6` | No advice available |

### Matching Notes

- Track-level ISRC is a first-class Deezer field.
- Playlist and search track payloads may also carry `isrc`.
- Playlist matching should keep Deezer `isrc` when present and should prefer Deezer exact ISRC lookup before fuzzy matching.

## Album

Album object example:

```text
https://api.deezer.com/album/302127
```

Album fields relevant to Allstarr:

| Field group | Fields |
| --- | --- |
| Identity | `id`, `upc`, `link`, `share` |
| Names and release | `title`, `label`, `provider`, `release_date`, `record_type` |
| Counts | `nb_tracks`, `duration`, `fans` |
| Artwork | `cover`, `cover_small`, `cover_medium`, `cover_big`, `cover_xl`, `md5_image` |
| Availability | `available`, `alternative`, `fallback` |
| Explicit metadata | `explicit_lyrics`, `explicit_content_lyrics`, `explicit_content_cover` |
| Related metadata | `genre_id`, `genres`, `contributors`, `artist`, `tracklist`, `tracks` |

Album `tracklist` is the API link to the album tracklist. Album detail responses can include a `tracks` list with track fields such as:

- `id`
- `readable`
- `title`, `title_short`, `title_version`
- `duration`
- `rank`
- `explicit_lyrics`
- `preview`
- `artist`
- `album`

For album-level explicit-content fields, Deezer also documents partial album states:

| Value | Meaning |
| --- | --- |
| `4` | Partially explicit for album lyrics |
| `5` | Partially unknown for album lyrics |
| `7` | Partially no advice available for album lyrics |

Use `nb_tracks` and `tracklist` when deciding whether an embedded album `tracks` list is incomplete.

## Artist

Artist object example:

```text
https://api.deezer.com/artist/27
```

Artist fields relevant to Allstarr:

- `id`
- `name`
- `link`
- `share`
- `picture`, `picture_small`, `picture_medium`, `picture_big`, `picture_xl`
- `nb_album`
- `nb_fan`
- `radio`
- `tracklist`

The artist `tracklist` points at the artist top tracks. Artist album methods return album lists and should be treated as paginated.

## Playlist

Playlist object example:

```text
https://api.deezer.com/playlist/908622995
```

Playlist fields relevant to Allstarr:

| Field group | Fields |
| --- | --- |
| Identity | `id`, `link`, `share`, `checksum` |
| Display | `title`, `description`, artwork fields |
| State | `public`, `is_loved_track`, `collaborative` |
| Counts | `duration`, `nb_tracks`, `unseen_track_count`, `fans` |
| Related metadata | `creator`, `tracks` |

Playlist artwork fields:

- `picture`
- `picture_small`
- `picture_medium`
- `picture_big`
- `picture_xl`

Playlist track fields relevant to Allstarr:

- `id`
- `readable`
- `title`, `title_short`, `title_version`
- `isrc`
- `duration`
- `rank`
- `explicit_lyrics`
- `preview`
- `time_add`
- `artist`
- `album`

The playlist track album object may include `upc` and cover sizes.

Playlist implementation guidance:

- Keep the order returned by Deezer for playlist tracks.
- Use `nb_tracks` to detect incomplete embedded `tracks` data.
- Keep playlist track `isrc` because Spotify playlist matching can use it.
- Keep `time_add` available if a future playlist flow needs Deezer add dates.

## Chart

Chart example:

```text
https://api.deezer.com/chart
```

Chart objects can return ranked lists of:

- `tracks`
- `albums`
- `artists`
- `playlists`
- `podcasts`

Chart entries add `position` for chart order. Chart playlist entries use a `user` object rather than the full playlist creator shape. Chart support is not a core Allstarr provider path today, but chart objects reuse many track, album, artist, playlist, and podcast field shapes.

## OAuth And Permissions

Deezer uses OAuth 2.0 when authentication and authorization are required.

Permissions documented in the pasted reference:

| Permission | Use |
| --- | --- |
| `basic_access` | Basic user information |
| `email` | User email |
| `offline_access` | Access user data when the user is not connected |
| `manage_library` | Add or rename playlists and add or order songs |
| `manage_community` | Follow and unfollow community relationships |
| `delete_library` | Delete library items |
| `listening_history` | Access listening history |

Do not add OAuth, access-token, or user-library mutation code to catalogue metadata flows unless the product behavior actually needs it.

## Allstarr Editing Guardrails

- Keep Deezer provider names and external IDs stable: provider key `deezer`, typed item IDs from `TrackParserBase`.
- Honor Deezer quota when adding request fan-out, search variants, or pagination.
- Treat explicit-content values as structured provider data and route filtering through shared explicit-content policy.
- Preserve Deezer result order for search and playlist retrieval unless a caller explicitly requests a documented `order`.
- Use ISRC where Deezer exposes it for matching. Do not reduce exact match behavior to plain-text search.
- Prefer dedicated Deezer tracklist endpoints when album or playlist detail embeds only part of the track list.
- Add provider regression tests when a new Deezer field changes matching, ordering, labels, or download metadata.
