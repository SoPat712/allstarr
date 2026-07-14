# Lyrics Architecture

## Components

- `JellyfinController.Lyrics.cs`: local-first controller logic and Spotify ID resolution
- `LyricsOrchestrator`: ordered fallback across non-local sources
- `SpotifyLyricsService`: sidecar-backed Spotify lyrics access
- `LyricsPlusService`: aggregator lookup
- `LrclibService`: LRCLib lookups, ID-based fetches, and manual mapping support
- `LyricsController`: admin actions for mappings and diagnostics

## Division Of Responsibility

### Controller Responsibilities

- Detect local versus external item IDs
- Ask Jellyfin for embedded lyrics for local items
- Gather `Song` metadata
- Resolve Spotify IDs from metadata, playlist state, or Odesli
- Convert final `LyricsInfo` into the Jellyfin response shape

### Orchestrator Responsibilities

- Apply source priority
- Catch and log source failures
- Return the first successful lyrics result
- Support background prefetch calls without changing controller behavior

### Service Responsibilities

- Each source service encapsulates one external API
- Each source service owns its own request details and parsing
- Shared cache keys still come from `CacheKeyBuilder`

## Adding A New Lyrics Source

When adding a source:

1. Create a dedicated service under `Services/Lyrics`
2. Keep parsing and API details inside that service
3. Register it in `Program.cs`
4. Add it to `LyricsOrchestrator` in an explicit priority position
5. Add tests for both the service and the orchestrator decision flow

Do not bolt new source logic directly into the controller.

## Current Architectural Constraints

- Spotify lyrics are sidecar-based, not fetched directly from Spotify in-process.
- `LyricsPrefetchService` exists but is currently not registered in `Program.cs`.
- The controller is allowed to do extra Spotify ID discovery because provider metadata is not guaranteed to include one.

## Edit Checklist

- If the source priority changes, update both lyrics docs.
- If cache key shapes change, update `CACHING.md` too.
- If the admin mapping format changes, update `LyricsController`, file persistence, and tests together.
