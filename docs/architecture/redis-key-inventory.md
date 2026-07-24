# Redis and Valkey key inventory

This inventory describes every key family emitted through `RedisCacheService`. It is the
baseline for replacing mandatory Valkey with a cache backend while keeping PostgreSQL as
the durable source of truth.

Rate and payload columns are qualitative design estimates from call sites, not production
measurements. The benchmark release gate must replace them with observed values.

## Classification rules

- **Durable state** must move to PostgreSQL before Valkey can become disposable.
- **Derived cache** may be deleted at any time and rebuilt from PostgreSQL, a provider, or
  the media backend.
- **Compatibility snapshot** is derived, but currently has a file-based warm path used by
  legacy playlist code.
- **Coordination** would require atomic/locking semantics. No such Redis key exists today.
- **Telemetry** would be disposable counters or logs. No such Redis key exists today.

## Key families

| Builder or literal | Key shape | Classification | Current TTL | Typical payload / rate | Current fallback |
| --- | --- | --- | --- | --- | --- |
| `BuildSearchKey` | `search:{query}:{filters...}` | Derived cache | 1 minute | JSON search result; high read, medium write | Re-query Jellyfin and providers |
| `BuildAlbumKey` | `{provider}:album:{id}` | Derived cache | 7 days where written | Album metadata; low/medium read | Re-query provider |
| `BuildArtistKey` | `{provider}:artist:{id}` | Derived cache | 7 days where written | Artist metadata; low/medium read | Re-query provider |
| `BuildSongKey` | `{provider}:song:{id}` | Derived cache | Provider-specific | Track metadata; medium read | Re-query provider |
| `BuildSpotifyPlaylistKey` | `spotify:playlist:{name}` | Compatibility snapshot | Schedule-derived expiry | Playlist plus tracks; medium read/write | Spotify fetch; legacy file snapshots may warm related data |
| `BuildSpotifyPlaylistItemsKey` | `spotify:playlist:items:{name}` | Compatibility snapshot | 7 days | Serialized Jellyfin response rows; high read after sync | Rebuild from canonical routes/Jellyfin; legacy file warm path |
| `BuildSpotifyPlaylistOrderedKey` | `spotify:playlist:ordered:{name}` | Compatibility snapshot | Call-site dependent | Ordered tracks; medium read/write | Rebuild from playlist source and mappings |
| `BuildSpotifyMatchedTracksKey` | `spotify:matched:ordered:{name}` | Compatibility snapshot | 30 days | Ordered `MatchedTrack` list; high read | Rebuild from canonical mappings; legacy file warm path |
| `BuildSpotifyLegacyMatchedTracksKey` | `spotify:matched:{name}` | Legacy compatibility snapshot | Call-site dependent | Legacy song list; medium read | Rebuild/migrate, then remove this family |
| `BuildSpotifyPlaylistStatsKey` | `spotify:playlist:stats:{name}` | Derived cache | 30 minutes | Small count dictionary; high read | Recalculate from canonical playlist/mappings |
| `BuildSpotifyPlaylistStatsPattern` | `spotify:playlist:stats:*` | Derived-cache invalidation pattern | Not stored | Pattern scan on mapping changes; low write-path frequency | Delete/recalculate individual PostgreSQL cache rows by namespace |
| `BuildSpotifyPlaylistLastSuccessfulSyncKey` | `spotify:playlist:last-successful-sync:{name}` | Operational state | No TTL | Timestamp string; low read/write | Must move to durable playlist-run state |
| `BuildSpotifyMissingTracksKey` | `spotify:missing:{name}` | Compatibility snapshot | 365 days | Missing-track list; medium read/write | File snapshot or regenerate from current playlist |
| `BuildSpotifyManualMappingKey` | `spotify:manual-map:{playlist}:{track}` | Durable state | No TTL at several call sites | Local target ID; high lookup, low write | Legacy mapping files may warm it; PostgreSQL must become authoritative |
| `BuildSpotifyExternalMappingKey` | `spotify:external-map:{playlist}:{track}` | Durable state | No TTL at several call sites | Provider and external ID; high lookup, low write | Legacy mapping files may warm it; PostgreSQL must become authoritative |
| `BuildSpotifyGlobalMappingKey` | `spotify:global-map:{track}` | Durable state | No TTL | Full one-to-many mapping; high lookup, medium write during rematch | None in `SpotifyMappingService`; migrate to canonical PostgreSQL repository |
| `BuildSpotifyGlobalMappingsIndexKey` | `spotify:global-map:all-ids` | Durable index | No TTL | Potentially large JSON ID list; frequent full read/rewrite | None; replace with indexed PostgreSQL query |
| `BuildLyricsKey` | `lyrics:{artist}:{title}:{album}:{duration}` | Derived cache | 14 days when explicit; some writes use backend default | Lyrics JSON/text; medium read | Provider lookup and optional lyrics files |
| `BuildLyricsPlusKey` | `lyricsplus:{artist}:{title}:{album}:{duration}` | Derived cache | 14 days | Lyrics JSON; medium read | Re-query LyricsPlus |
| `BuildLyricsManualMappingKey` | `lyrics:manual-map:{artist}:{title}` | Durable state | No TTL at current write sites | Lyrics ID; high lookup, rare write | Legacy `/app/cache/lyrics_mappings.json`; move to PostgreSQL |
| `BuildLyricsByIdKey` | `lyrics:id:{id}` | Derived cache | 14 days where written | Lyrics JSON/text; low read | Re-query LRCLib |
| `BuildPlaylistImageKey` | `playlist:image:{id}` | Derived cache | 7 days | Image bytes; high read, low write | Re-fetch backend artwork |
| `BuildExternalImageKey` | `image:{provider}:{type}:{id}` | Derived cache | 14 days | Image bytes; high read | Re-fetch provider artwork |
| direct proxy image key | `image:{item}:{type}:{width}:{height}:{tag}` | Derived cache | 14 days | Resized image as base64/content type; high read | Re-fetch Jellyfin image |
| `BuildGenreEnrichmentKey` | `genre:{track identity}` | Derived cache | 30 days | Short genre string; medium read | File cache, then enrichment provider |
| `BuildGenreKey` | `genre:{genre}` | Derived cache | Call-site dependent | Genre metadata; low read | Recompute/re-query |
| `BuildMusicBrainzIsrcKey` | `musicbrainz:isrc:{isrc}` | Derived cache | 30 days currently | Recording JSON; medium read | Re-query MusicBrainz |
| `BuildMusicBrainzSearchKey` | `musicbrainz:search:{title}:{artist}:{limit}` | Derived cache | 30 days currently | Recording list; medium read | Re-query MusicBrainz |
| `BuildMusicBrainzMbidKey` | `musicbrainz:mbid:{mbid}` | Derived cache | 30 days currently | Recording JSON; low read | Re-query MusicBrainz |
| `BuildOdesliTidalToSpotifyKey` | `odesli:tidal-to-spotify:{id}` | Derived cache | 60 days | Target ID string; medium read | Re-query Odesli |
| `BuildOdesliUrlToSpotifyKey` | `odesli:url-to-spotify:{url}` | Derived cache | 60 days | Target ID string; medium read | Re-query Odesli |
| direct Odesli translation key | `odesli:translate:{sourceUrl}:{target}` | Derived cache | 60 days | Target ID string; medium read | Re-query Odesli |
| direct Jellyfin signature key | `spotify:playlist:jellyfin-signature:{name}` | Derived cache | 7 days | Small signature string; high read | Recompute from Jellyfin playlist |

## Non-Redis caches

- Transcoded media uses files under the downloads tree and a 60-minute cleanup TTL.
- Genre, missing-track, playlist-item, matched-track, manual-mapping, and lyrics data have
  legacy file warm paths under `/app/cache`.
- `RedisPersistenceService` does not serialize keys itself. It relies on Redis-native
  persistence and only cleans historical placeholder snapshot filenames.

## Migration consequences

1. Move global, playlist-specific, and lyrics manual mappings plus successful-sync state
   to PostgreSQL before making cache loss an expected event.
2. Replace the global JSON mapping-ID index with an indexed PostgreSQL query.
3. Treat every remaining family as reconstructable, namespaced cache data with bounded
   payloads and explicit expiry.
4. Remove duplicate literal key construction as consumers move behind the cache contract.
5. Add metrics before selecting the beta default; current rate and size labels are only
   code-derived estimates.
