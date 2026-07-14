# Lyrics Integration

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Runtime Flow

Lyrics requests are owned by `JellyfinController.Lyrics.cs`.

The flow is:

1. Determine whether the item is local or external
2. For local items, ask Jellyfin for embedded lyrics first
3. Resolve track metadata and, if possible, a Spotify track ID
4. Call `LyricsOrchestrator`
5. Return Jellyfin-shaped lyrics JSON

This split is intentional. The controller owns Jellyfin-first behavior. The orchestrator owns multi-source fallback after that.

## Current Source Priority

For non-local or no-local-hit scenarios, `LyricsOrchestrator` currently tries:

1. Spotify lyrics sidecar, if a Spotify track ID is available
2. LyricsPlus
3. LRCLib

`SpotifyLyricsService` talks to the configured lyrics sidecar URL from `SpotifyApiSettings.LyricsApiUrl`.

## Spotify ID Resolution

When the item is external, the controller tries several ways to obtain a Spotify ID:

- `Song.SpotifyId` already populated by metadata services
- Cached playlist or matching state
- Odesli conversion from provider URLs or SquidWTF/Tidal IDs

Keep this resolution inside the controller or existing helper methods. The orchestrator should not own cross-provider Spotify ID discovery.

## Manual Mapping And Admin APIs

`LyricsController` owns admin-side lyrics tools:

- Save manual LRCLib mappings
- Read manual lyrics mappings
- Test Spotify lyrics by track ID
- Trigger playlist lyrics prefetch actions

Manual lyrics mappings are persistent and should not expire.

## Caching Rules

- Manual mapping keys use `lyrics:manual-map:*`
- Lyrics content uses normal lyrics TTLs from `CacheSettings`
- The controller strips decorated titles and artist names before lookups so cached lyrics are reusable across local and external presentations

## Editing Guardrails

- Keep the Jellyfin local-lyrics lookup in the controller.
- Keep the orchestrator resilient. Source failures should fall through, not fail the whole request.
- Reuse `CacheKeyBuilder` for new cache keys.
- If a new lyrics source is added, wire it through the orchestrator instead of creating another controller-specific path.
