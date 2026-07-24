# Caching Architecture

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Overview

Caching in Allstarr is a fail-open layered runtime contract:

- `BoundedHotApplicationCache` keeps at most 16 MiB of recent small writes in process.
- `DatabaseApplicationCache` stores bounded disposable string/JSON entries in PostgreSQL.
- `FileMediaApplicationCache` stores bounded artwork and media payloads under `/app/cache/media`.
- `CacheKeyBuilder` and `CacheExtensions` centralize cache naming and TTL policy.

No cache tier owns identities, accounts, mappings, playlist order, jobs, events, or user decisions.

## Core Components

### `IApplicationCache`

Feature code depends only on `IApplicationCache`. The production facade routes eligible metadata through the
bounded RAM and PostgreSQL tiers and routes binary media families to bounded disk. Cache failures return a miss
rather than changing durable application behavior.

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
- `/app/cache/media`: bounded binary artwork and media payloads

### Warmers and Persistence

- `CacheWarmingService` imports only explicitly retained compatibility playlist and manual-mapping files.
- Reconstructable genre and fetched-lyrics entries use `IApplicationCache` directly and are not warmed from files.
- The admin playlist summary is a five-minute `IApplicationCache` entry, not a private file.
- Jellyfin and external playback metadata use shared short-lived metadata keys; Jellyfin
  playback artwork routes through the bounded media tier rather than process dictionaries.
- Jellyfin endpoint-policy item classification uses a five-minute shared entry; cache loss
  re-queries Jellyfin and never bypasses the music-only policy.
- Playback callback deduplication uses hashed eight-second shared-cache keys. Durable
  playback recording remains the correctness boundary; cache loss may duplicate intake
  work but cannot lose an accepted event.
- Spotify Pathfinder playlist artwork descriptors are shared for 30 minutes by stable
  playlist identity and source revision, so Add playlist discovery does not repeat
  account-bound GraphQL artwork resolution.

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
- Keep cache-loss behavior safe. Cache misses must not break core playback or proxy flows.
- If a feature writes persistent runtime files under `/app/cache`, document the format and make sure `CacheWarmingService` either understands it or intentionally ignores it.
