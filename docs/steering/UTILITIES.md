# Shared Utilities

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Why The Utility Layer Exists

`allstarr/Services/Common` and `allstarr/Services/Admin` hold the repo's shared policy helpers. New feature code should lean on these before introducing one-off logic.

## Utility Families

### Cache And ID Helpers

- `CacheKeyBuilder`: canonical cache key construction
- `CacheExtensions`: centralized TTL access
- `PlaylistIdHelper`: external playlist ID parsing and formatting
- `InjectedPlaylistItemHelper`: Spotify playlist item composition helpers
- `SpotifyPlaylistCountHelper`: playlist count helpers

### Auth And Session Helpers

- `AuthHeaderHelper`: forwards and builds Jellyfin auth headers
- `AdminAuthSessionService`: in-memory admin session storage
- `AdminHelperService`: `.env` access, value masking, playlist serialization, validation helpers

### Safety Helpers

- `PathHelper`: safe file and folder names, bounded path generation
- `OutboundRequestGuard`: blocks local and private outbound HTTP targets
- `BotProbeDetector`: high-confidence scanner path detection
- `AdminNetworkBindingPolicy`: admin bind and subnet policy parsing

### Retry, Matching, And Response Helpers

- `RetryHelper`: shared backoff logic
- `FuzzyMatcher`: shared fuzzy matching
- `ExplicitContentFilter`: centralized explicit-track filtering policy
- `JellyfinItemSnapshotHelper`: preserves Jellyfin item fidelity in cached snapshots
- `ProviderIdsEnricher`: provider ID enrichment helpers
- `RoundRobinFallbackHelper`: mirror failover and endpoint rotation for SquidWTF and similar cases

### Maintenance Helpers

- `EndpointBenchmarkService`: endpoint timing support used by startup validation
- `CacheCleanupService`: bounded cleanup for transient downloaded media
- `CacheWarmingService`: current cache-file warmup for compatibility flows

Pre-overhaul environment, favorite, Spotify mapping, and version-state migration helpers were retired. Do not
reintroduce an automatic conversion path during startup. The durable overhaul uses a fresh install, and any
future import must be an explicit operator action with validation and rollback.

## Editing Guardrails

- Extend a helper before duplicating logic in controllers or provider services.
- If a helper encodes policy, add or update tests when that policy changes.
- Prefer pure, reusable helpers in `Services/Common`; keep feature orchestration in feature services.
- If a helper affects security, paths, auth forwarding, or cache keys, treat it as a shared contract and update the matching steering doc too.
