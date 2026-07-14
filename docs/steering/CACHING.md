# Caching Architecture

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Overview

Caching in Allstarr is a layered runtime contract:

- Valkey is the primary runtime cache in the standard deployment. The existing services use its Redis-compatible
  protocol and retain `Redis` in several type and configuration names.
- `/app/cache` holds cold-start recovery files, legacy compatibility mapping files, and a few admin artifacts.
- `CacheKeyBuilder` and `CacheExtensions` centralize cache naming and TTL policy.

The code is intentionally fail-open when Valkey is unavailable. Features should degrade, not crash. Valkey and
`/app/cache` are not the durable owner of identities, provider accounts, jobs, outbox work, health rollups, or
backup records.

## Core Components

### `RedisCacheService`

`RedisCacheService` is the entry point for cache reads and writes.

- Connects once at startup if Redis is enabled.
- Retries one `SET` after reconnect on timeout or connection failure.
- Returns `null` or `false` when Redis is disabled or unavailable.
- Serializes complex values as JSON.

Do not hardcode direct `StackExchange.Redis` usage in feature code unless there is a very strong reason.

### `CacheKeyBuilder`

All cache keys should come from `CacheKeyBuilder`.

Common families:

- Search: `search:*`
- Provider metadata: `{provider}:song:*`, `{provider}:album:*`, `{provider}:artist:*`
- Spotify playlists and stats: `spotify:playlist:*`, `spotify:matched:*`, `spotify:missing:*`
- Global Spotify mappings: `spotify:global-map:*`
- Lyrics: `lyrics:*`, `lyricsplus:*`
- Images: `playlist:image:*`, `image:{provider}:{type}:{id}`
- Genre enrichment: `genre:*`
- Odesli and MusicBrainz lookups

If a new feature needs cache, extend `CacheKeyBuilder` first.

### `CacheExtensions` and `CacheSettings`

TTL policy is centralized in `CacheSettings`, initialized once through `CacheExtensions.InitializeCacheSettings`.

Current default TTL families:

- Search results: 1 minute
- Playlist images: 168 hours
- Spotify playlist items: 168 hours
- Spotify matched tracks: 30 days
- Lyrics: 14 days
- Genres: 30 days
- External metadata: 7 days
- Odesli lookups: 60 days
- Proxy images: 14 days
- Transcoded audio cache: 60 minutes

### File-Based Warm State

`/app/cache` is not just a debug directory. It stores runtime artifacts used across restarts.

Important locations:

- `/app/cache/spotify`: playlist item snapshots and matched-track snapshots
- `/app/cache/mappings`: manual playlist mapping files
- `/app/cache/lyrics_mappings.json`: manual lyrics ID mappings
- `/app/cache/lyrics`: cached lyrics payloads
- `/app/cache/genres`: genre enrichment cache files
- `/app/cache/admin_playlists_summary.json`: short-lived admin summary cache

### Warmers and Persistence

- `CacheWarmingService` loads file-backed artifacts into Redis on startup.
- `RedisPersistenceService` currently relies mostly on Redis native persistence and only maintains a snapshot folder placeholder.

The fresh overhaul baseline does not backfill cache, favorite, environment, mapping, or version-state files into
durable storage on startup. Compatibility services may still read these formats for explicitly retained legacy
routes, but provider-neutral matching, playlists, jobs, accounts, and identities use durable storage. Cache files
are never the durable authority for those features.

## Feature-Specific Cache Contracts

### Spotify

- Playlist fetch cache uses per-playlist cron-aware expiry, not a single global refresh interval.
- Rebuild operations clear playlist-specific cache groups before fetching and matching again.
- Legacy global Spotify mappings remain until deleted. They are not the provider-neutral durable mapping model.

### Lyrics

- Manual lyrics mappings are permanent.
- Lyrics content uses normal TTL-based caching.
- The controller and orchestrator depend on stable key formats for cache hits.

### Search and Images

- Jellyfin integrated search results are cached only for pure search requests.
- External cover art and playlist art use dedicated image cache keys.

## Editing Guardrails

- Use `CacheKeyBuilder`; do not invent ad hoc keys inline.
- Use `CacheExtensions` or existing feature TTL flows instead of embedding raw `TimeSpan` values everywhere.
- Keep Valkey-disabled behavior safe. Cache misses must not break core playback or proxy flows.
- If a feature writes persistent runtime files under `/app/cache`, document the format and make sure `CacheWarmingService` either understands it or intentionally ignores it.
