# Spotify Integration

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## What The Spotify Subsystem Does

Allstarr uses Spotify in three different ways:

- Fetch playlist contents directly from Spotify
- Match Spotify tracks to local Jellyfin items or external provider tracks
- Reuse Spotify IDs for lyrics and playlist UX

The current implementation is built around session cookies and direct playlist fetching, not the older refresh-token flow.

## Current Auth Model

Spotify access is driven by `SpotifyApiSettings` and `SpotifySessionCookieService`.

- Primary credential: Spotify `sp_dc` session cookie
- Global fallback cookie: `SpotifyApi:SessionCookie`
- User-scoped cookies: JSON maps stored in `.env`
- Lyrics sidecar URL: `SpotifyApi:LyricsApiUrl`

Non-admin users can only operate on their own cookie scope. Admin users can manage other user scopes.

### Account-Scope Guardrail

The cookie fallback is a compatibility mechanism. Provider-neutral work resolves and records the effective
`ProviderAccount` before any playlist fetch, match rebuild, or playlist write:

- A user-scoped cookie/account is selected only for its resolved Allstarr owner.
- A shared/global Spotify account may be used only when the playlist-link policy explicitly permits it; do not silently fall back from a missing user account to another user's scope.
- Persist the account scope, backend identity, library scope, source snapshot version, policy version, and correlation ID with the sync. Do not persist raw `sp_dc` values in playlist snapshots, match records, logs, or job payloads.
- If the proxy user's backend identity cannot be resolved, preserve normal backend proxy behavior but do not start a user-owned Spotify sync or favorite side effect.

## Playlist Configuration Contract

`SpotifyImportSettings.Playlists` is the canonical model. In `.env`, it is stored as `SPOTIFY_IMPORT_PLAYLISTS` and parsed from JSON arrays:

`["Name", "SpotifyId", "JellyfinId", "first|last", "cron", "UserId?"]`

Important points:

- `Name` is the logical playlist key used throughout cache and admin flows.
- `SpotifyId` is the direct Spotify playlist ID.
- `JellyfinId` links the Spotify playlist to a Jellyfin playlist.
- `LocalTracksPosition` controls whether local matches appear before or after external matches.
- `SyncSchedule` is a per-playlist cron schedule in UTC.
- `UserId` is optional and enables user-scoped playlist ownership.

Only this unified array is parsed. Pre-overhaul split playlist environment variables are not translated or
imported during startup.

## Core Services

### `SpotifySessionCookieService`

- Resolves the effective session cookie for a user scope.
- Stores user-scoped cookie maps and set dates in `.env`.
- Falls back to the global cookie when appropriate.

### `SpotifyApiClient` and `SpotifyApiClientFactory`

- Handle direct Spotify web API and GraphQL-style calls using the effective `sp_dc` cookie.
- Factory support exists so a controller or worker can create a scoped client for a non-global cookie.

### `SpotifyPlaylistFetcher`

- Fetches configured playlists directly from Spotify.
- Uses per-playlist cron schedules to decide staleness.
- Caches full playlist payloads in Redis.
- Keeps a playlist-name to playlist-ID cache after discovery.

### `SpotifyTrackMatchingService`

- Background service that rebuilds playlist matches.
- Supports startup matching, manual rebuilds, and cron-driven rebuilds.
- Clears playlist-scoped caches during rebuild before fetching fresh data.
- Prefers ISRC matching when enabled, otherwise falls back to fuzzy matching.

### Legacy `SpotifyMappingService`

Stores mappings for the pre-overhaul injected-playlist compatibility routes. It is not used by the provider-neutral
playlist orchestrator, virtual playlist reads, library index, or current match-review screen.

Current precedence rules:

- Manual mappings beat automatic mappings.
- Local mappings beat external mappings.
- Existing local mappings are not downgraded back to external mappings.

### `SpotifyMappingValidationService`

- Validates and refreshes mappings written in the current format.

The automatic legacy mapping migration service was retired for the fresh overhaul baseline. Existing cache or
mapping files are not imported into the durable model at startup.

## Provider-Neutral Playlist Lifecycle

The durable replacement is implemented for account-bound Spotify and Apple MusicKit sources. It starts from newly
configured playlist links rather than importing pre-overhaul Redis, mapping files, or environment state.

1. Resolve the backend identity, user/library scope, and effective Spotify `ProviderAccount`; load the scoped playlist link and its rule version.
2. Fetch and persist an ordered source snapshot, including page boundaries and provider revision/ETag when available. On a temporary upstream failure, serve/report the last known-good snapshot as stale instead of replacing it with an empty playlist.
3. Send each source track through `TrackIdentityService`. Preserve manual mapping precedence, expose low-confidence/unresolved tracks, and do not use a weak fuzzy match merely to make the response look complete.
4. Apply the link's explicit rules—local-over-external, ordering, dedupe, availability, and confidence threshold—and save the resulting match/rule versions for review.
5. In `virtual` mode, only shape the protocol response. In `materialized` or `hybrid` mode, enqueue idempotent backend writes keyed by source revision plus rule version; never mutate the Spotify source playlist unless a separate explicit policy authorizes it.
6. Report progress, cancellation, stale state, and retryability to the owner. A replay must reuse the job/idempotency key rather than add duplicate tracks or rewrite an unchanged playlist.

The old Spotify workers remain only for characterized compatibility routes. New review and playlist work uses
`ExternalMetadataSnapshot`, `TrackMatch`, `ManualTrackOverride`, canonical recordings, and provider identities.

## Admin Surface Ownership

- `PlaylistLinksController`: durable links, refresh/preview, run-now, schedules, match overrides, and encrypted
  Subsonic target credential references
- `TrackMatchesController`: provider-neutral match review and many-provider identity projection
- `LibraryIndexController`: durable Jellyfin or Subsonic/Navidrome library scan jobs and scan counts
- `PlaylistController`: legacy injected-playlist summary, tracks, rebuild, and refresh actions
- `JellyfinAdminController`: link and unlink Jellyfin playlists
- `SpotifyAdminController`: user playlists, session-cookie status and writes, sync, match, global mappings

Keep legacy Spotify behavior in those compatibility controllers. Do not route new provider-neutral records back
through the Redis mapping cache or scatter Spotify environment parsing into new controllers.

## Editing Guardrails

- Use `SpotifySessionCookieService` for cookie resolution instead of reading `.env` directly in new code.
- Use `SpotifyImportSettings` and `AdminHelperService` for playlist config changes.
- Preserve the mapping precedence rules in `SpotifyMappingService`.
- Preserve per-playlist cron behavior. Matching and fetch expiry are intentionally playlist-specific.
- If a change affects playlist cache keys or mapping formats, update the cache and testing docs too.
- Keep the direct Spotify compatibility flow operational until it is deliberately retired. Provider-neutral work
  must carry an explicit account and identity context through playlist fetch, match, preview, and materialized-write paths.
- Add tests for account isolation, snapshot/stale-state behavior, low-confidence match handling, and duplicate-safe retry before changing a playlist lifecycle boundary.
