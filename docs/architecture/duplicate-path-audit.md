# Duplicate playback, matching, cache, and job paths

This audit identifies compatibility implementations that overlap the v3 core. It assigns
the canonical owner and the evidence required before removing each legacy path. See also
[`background-operation-inventory.md`](background-operation-inventory.md),
[`redis-key-inventory.md`](redis-key-inventory.md), and
[`performance-hot-path-audit.md`](performance-hot-path-audit.md).

## Playback

| Concern | Overlapping paths | Canonical owner | Removal prerequisite |
| --- | --- | --- | --- |
| Virtual playlist reads | Redis-backed reads in `JellyfinController.Spotify` and helpers; durable reads in `PlaylistVirtualizationService` through `JellyfinVirtualPlaylistProtocolAdapter` and `SubsonicVirtualPlaylistProtocolAdapter` | `IPlaylistVirtualizationService` | Protocol parity for order, local/provider fallback, artwork, duration, and unavailable tracks |
| Playback-track identity | Ad-hoc provider/title parsing in `DownloadActivityController`; resolver chain in `PlaybackTrackResolver` | `IPlaybackTrackResolver` | Activity and event details resolve the same canonical/provider IDs through the shared resolver |
| Playback event delivery | Jellyfin and Subsonic protocol capture plus controller-local dedupe; durable `PlaybackSignalPipeline` and `PlaybackSignalJobHandler` | Protocol adapters capture only; `IPlaybackSignalPipeline` owns persistence and delivery | Restart, duplicate-event, multi-user, Last.fm, ListenBrainz, and cancellation parity |
| Playback metadata | `JellyfinPlaybackMetadataResolver`, `ExternalPlaybackMetadataResolver`, and controller fallbacks | Ordered `IPlaybackMetadataResolver` chain | Every event surface uses resolver output and exposes a structured unresolved state |

## Matching and classification

| Concern | Overlapping paths | Canonical owner | Removal prerequisite |
| --- | --- | --- | --- |
| Candidate generation and decisions | `SpotifyTrackMatchingService` scoring/search branches; `TrackMatchDecisionEngine` in `PlaylistOrchestrationService` and dry-run preview | Provider-neutral candidate gateway plus `TrackMatchDecisionEngine` | Decision parity fixtures for local-first and every enabled provider, including rejection/fallthrough reasons |
| Durable mappings | Redis-only `SpotifyMappingService`; PostgreSQL `TrackMatchPersistenceService`; one-way `LegacySpotifyMappingProjector` | `ITrackMatchPersistenceService` backed by PostgreSQL | Bidirectional migration/rollback test and restart parity with Redis unavailable |
| Summary/detail classification | Repeated branches in `PlaylistController`; helper logic in `PlaylistTrackStatusResolver`; canonical match state in durable playlist records | One shared track-classification service reading canonical decisions/routes | Summary, modal detail, playback, mapping review, and event counts agree for the same snapshot |
| Manual review | Compatibility mapping endpoints plus durable overrides in `TrackMatchesController` | Durable scoped override repository and canonical decision engine | Existing valid manual mappings migrate with owner, scope, revision, and evidence |

## Cache

| Concern | Overlapping paths | Canonical owner | Removal prerequisite |
| --- | --- | --- | --- |
| Playlist snapshots and matched rows | Multiple Spotify `CacheKeyBuilder` families read directly by controllers, warming, matching, and lyrics code | PostgreSQL source snapshots and canonical match records; cache is reconstructable only | Cold restart produces identical playlist order and match decisions without Redis |
| Global/manual/external mappings | No-expiry Redis keys and indexes plus durable identity graph | PostgreSQL canonical mappings and overrides | Migration preserves all usable routes and unresolved evidence |
| Cache lifecycle | `CacheWarmingService`, `CacheCleanupService`, and `RedisPersistenceService` | Pluggable bounded cache lifecycle; no durable state in cache | Backend parity, expiry, memory, and cache-loss tests |

## Jobs and scheduling

The complete owner/idempotency/progress/retry table is maintained in
`background-operation-inventory.md`. The primary duplicate is the legacy Spotify
fetch/match polling stack running beside durable playlist jobs. Controllers must enqueue
canonical jobs and never invoke long-running implementations directly.

## Removal order

1. Establish shared classification and provider-neutral candidate contracts.
2. Migrate reusable Redis mappings and playlist state into PostgreSQL.
3. Route summary, detail, playback, review, and event surfaces through canonical stores.
4. Route controller, schedule, and extension entry points through durable jobs.
5. Run parity fixtures with Redis unavailable and after restart.
6. Remove legacy reads first, then writers, hosted loops, compatibility handlers, and
   obsolete cache keys.

No legacy path is removed merely because the replacement returns a non-empty result.
Parity must cover identity, order, scope, provider route, outcome, and user-visible
counts.

