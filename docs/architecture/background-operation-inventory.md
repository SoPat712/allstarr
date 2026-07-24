# Background operation ownership

This inventory identifies every background execution mechanism and defines the single
owner each user-visible operation must converge on. Controllers enqueue work; they do
not start parallel implementations. Provider and extension code supplies adapters; it
does not own scheduling, retries, progress, or durable decisions.

## Shared execution contract

| Concern | Required owner |
| --- | --- |
| Queue and claim | `DurableJobQueue` and `DurableJobWorker` |
| Schedules | `DurableScheduleWorker` / `DurableScheduleEngine` |
| Idempotency | A stable tenant-scoped key supplied at enqueue time |
| Retry | The durable job record and its handler-specific retry classification |
| Progress | Durable job progress plus the outbox/activity event stream |
| Cancellation | Durable job cancellation token and persisted cancellation state |
| Extension work | A core handler calling `ExtensionRuntimeCoordinator`; never an extension-owned loop |

## User-visible operations

| Operation | Current paths | Canonical owner | Idempotency | Progress and retry | Disposition |
| --- | --- | --- | --- | --- | --- |
| Playlist source refresh | `SpotifyPlaylistFetcher`, controller refresh actions, `PlaylistOrchestrationService.RefreshAsync` | Provider-neutral playlist refresh handler in core orchestration | tenant + playlist link + source revision | Durable job progress; retry transient provider failures only | Migrate the Spotify loop and controller execution into the core handler |
| Playlist matching/rematching | `SpotifyTrackMatchingService`, `LegacyPlaylistMatchAllJobHandler`, `PlaylistOrchestrationService.MatchAndLoadAsync` | Provider-neutral matching handler using the canonical decision engine | tenant + playlist snapshot version + matching policy version | Durable per-playlist progress and match events; retry transient candidate-source failures | Retire the Spotify loop and compatibility handler after parity migration |
| Legacy missing-track scrape | `SpotifyMissingTracksFetcher` | None; source snapshots and canonical match decisions replace it | Not applicable | Not applicable | Remove after compatibility reads are gone; the hosted loop is already dormant |
| Playlist target materialization | `PlaylistMaterializationJobHandler` (`playlist.materialize`) | Existing handler | tenant + playlist link + source snapshot version | Durable job/outbox events; existing retry policy | Keep |
| Library indexing | `LibraryIndexMaintenanceService`, `LibraryIndexJobHandler` (`library.index`), controller enqueue | Existing handler; maintenance service only enqueues stale scopes | tenant + backend instance + library scope + scan generation | Durable job/outbox events; transient backend retries | Keep |
| Backend library refresh | `BackendLibraryRefreshOrchestrator`, `BackendLibraryRefreshJobHandler` (`library.refresh`) | Existing handler | tenant + backend instance + requested refresh scope | Durable job/outbox events; transient backend retries | Keep |
| Playback signals | `PlaybackSignalPipeline`, `PlaybackSignalJobHandler` (`playback.signal.process`) | Existing handler | tenant + playback signal identity | Durable event/outbox stream; bounded retry | Keep |
| Favorite actions | `FavoriteActionPipeline`, `FavoriteActionJobHandler` (`favorite.process`) | Existing handler | tenant + user + favorite action identity | Durable job/outbox events; provider-aware retry | Keep |
| Recommendations | `RecommendationRunJobHandler` (`recommendation.generate`) | Existing handler | tenant + run identity + input revision | Durable job/outbox events; bounded retry | Keep |
| Generated-set materialization | `GeneratedSetMaterializationJobHandler` (`smart-playlist.materialize`) | Existing handler | tenant + generated set + recommendation revision | Durable job/outbox events; bounded retry | Keep |

## Infrastructure and lifecycle services

These services do not duplicate user operations and remain hosted lifecycle owners:

- `IdentityBootstrapper`
- `DurableStorageInitializer`
- `DurableStorageRuntimeMonitor`
- `DefaultTenantRuntimeSettingsProjector`
- `StartupValidationOrchestrator`
- `FirstPartyExtensionBootstrapper`
- `ExtensionRuntimeCoordinator`
- `DurableProviderHealthInitializer`
- `ManagedProviderAccountHealthWarmupService`
- `ProviderCtsWarmupService`
- `CacheCleanupService`
- `LegacyMappingImportService`
- `AuditEventRetentionService`
- `PlatformTraceCollector`
- `SidecarHealthMonitor`
- `DurableOutboxDispatcher`

`LyricsPrefetchService` is currently not registered. If restored, it must enqueue a
durable lyrics-prefetch job instead of owning an independent polling loop.

## Fire-and-forget provider work

Provider-local `Task.Run` calls in `BaseDownloadService`, Deezer, Qobuz, SquidWTF, and
Jellyfin session maintenance are not alternate job systems. Only short-lived,
reconstructable cache refreshes or connection maintenance may remain fire-and-forget.
Downloads, matching, library mutation, playlist mutation, and any work requiring user
progress or retry must enter `DurableJobQueue`.

## Migration order

1. Add provider-neutral playlist refresh and matching job types.
2. Route controller, schedule, and extension entry points through those jobs.
3. Preserve existing idempotency keys and emit one progress/event stream.
4. Compare canonical decisions and materialized playlists against compatibility paths.
5. Disable and remove `SpotifyPlaylistFetcher`, `SpotifyTrackMatchingService`,
   `LegacyPlaylistMatchAllJobHandler`, and `SpotifyMissingTracksFetcher`.
6. Remove compatibility cache keys only after restart and migration tests prove parity.
