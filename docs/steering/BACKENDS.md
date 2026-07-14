# Backend Proxy Surfaces

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Registration Model

Allstarr supports two backend surfaces:

- Jellyfin
- Subsonic/Navidrome

Only one backend controller is registered at runtime. This is required because both backend controllers own catch-all routes.

The protocol support inventory and future parity requirements live in [references/protocols.md](references/protocols.md). Treat that matrix as the migration checklist: this document records the current controller boundaries, while the matrix distinguishes existing behavior from target capability-core work.

## Jellyfin Surface

`JellyfinController` is split across partial files by concern:

- `JellyfinController.cs`: items, artists, images, favorites, similar items, catch-all relay
- `JellyfinController.Authentication.cs`: transparent login forwarding plus session capability bootstrap
- `JellyfinController.Search.cs`: integrated browse and search routing
- `JellyfinController.Audio.cs`: local proxy streams plus external download-and-stream behavior
- `JellyfinController.Lyrics.cs`: local-first lyrics, Spotify ID resolution, orchestrator handoff
- `JellyfinController.PlaybackSessions.cs`: capabilities, playback start/progress/stop, ghost item reporting, scrobbling hooks
- `JellyfinController.PlaylistHandler.cs` and `JellyfinController.Spotify.cs`: playlist injection and Spotify-related proxy behavior

### Jellyfin Guardrails

External item response shaping, conditional image responses, and lyrics serialization now pass through
focused adapters under `Core/Protocols/Jellyfin`. The controller still owns request dispatch and lookup order,
while fixtures protect the client-visible status, body, content type, and local-first behavior.

- Preserve route ordering, especially around catch-all proxy routes.
- Preserve exact client query strings when proxying. `JellyfinProxyService` already handles repeated-key cases carefully.
- The generic relay accepts GET, POST, PUT, PATCH, DELETE, and HEAD. It preserves raw bodies, safe end-to-end request and response headers, status, media type, and binary or text bodies while removing hop-by-hop transport headers.
- Local items should proxy back to Jellyfin whenever possible.
- External items should use metadata and download services, then be shaped back into Jellyfin-compatible responses.

## Search Routing Priority

`JellyfinController.Search.cs` already encodes a priority order:

1. external artist filters
2. external album filters
3. `ParentId` handling, including external parents and external playlists
4. library artist filters
5. integrated search
6. plain proxy browse

Do not collapse this into a single generic handler without understanding the compatibility cases it covers.

## Playback And Session Rules

Jellyfin playback handling is more than request forwarding:

- `JellyfinSessionManager` keeps sessions alive and owns server-side websocket maintenance
- External tracks may create ghost playback items in Jellyfin so Now Playing still works
- Playback signals feed scrobbling
- Deduplication windows exist to avoid duplicate start or stop events

These behaviors are coupled. If you change one, inspect the others.

Playback and favorite routes now consult the shared `ProtocolExecutionContext` before starting optional
user-owned work. A verified but unlinked backend principal keeps transparent Jellyfin behavior, but a route,
query, or payload user ID cannot authorize scrobbling, synthetic played signals, kept-file work, or an external
InstantMix provider lookup. Capabilities and favorite response shaping use the focused Jellyfin interaction
adapter. All six pinned InstantMix route classes are explicit. Scoped recommendation policy now uses habit-derived
seeds, provider readiness, durable runs, and visible explanations.

## Subsonic Surface

`SubSonicController.cs` owns:

- `search3`
- `stream`
- `getSong`
- `getArtist`
- `getAlbum`
- `getCoverArt`
- `star`
- `getLyricsBySongId`
- catch-all relay

It uses `SubsonicRequestParser`, `SubsonicResponseBuilder`, `SubsonicModelMapper`, and `SubsonicProxyService` to stay protocol-compatible while still injecting external content.

### Subsonic Guardrails

- Preserve the ability to accept both GET and POST for Subsonic endpoints.
- Preserve XML and JSON response compatibility.
- Keep external playlist behavior behind the existing provider and settings checks.
- `SubsonicController` handles search, streaming, item metadata, cover art, `star`/`unstar`, provider-neutral playlist reads, OpenSubsonic structured lyrics, and authenticated scrobble observations. Linked favorite and playback actions use durable shared pipelines without changing native XML or JSON responses.

## Shared Backend Contracts

- Typed external IDs are the normal format.
- Legacy external song IDs still parse.
- Backend proxy code should not own provider-specific API details beyond orchestration.
- Backend controllers should delegate provider calls to `IMusicMetadataService`, `IDownloadService`, lyrics services, and scrobbling services instead of duplicating those concerns.
- Generated sets write exact local matches to Jellyfin or a Subsonic-compatible target through the shared playlist target. They preserve order, reuse existing members, explain unmatched entries, and never download a missing song. Subsonic and Navidrome targets require the explicitly selected encrypted credential reference.

### Target Identity And Mutation Boundary

The verified request context boundary applies to every current user-owned action without changing transparent proxy behavior for an unlinked principal:

- A protocol adapter must receive a `ProtocolExecutionContext` created from a verified backend principal. Query/path `userId` values may identify a backend request shape but cannot authorize Allstarr user-owned accounts, libraries, or side effects by themselves.
- `BackendIdentityResolver` maps `(protocol, backend instance, backend principal)` to the canonical Allstarr user before a user-scoped provider account, favorite action, placement root, or playlist link is selected. If there is no mapping, keep proxy behavior transparent and skip user-owned side effects.
- Pass the resolved user, library scope, selected provider account, policy version, correlation ID, and cancellation/deadline through core operations. Do not let a provider silently substitute another user's account or a global default without explicit policy.
- Preserve the backend's favorite/star/unfavorite response first. Optional Allstarr work is a durable event/job, not an untracked controller task; an action failure must be observable without breaking the protocol response.
- Playlist reads and writes must use the same context. Virtual responses never mutate the backend; materialized writes need a source-snapshot/rule-version idempotency key and preserve backend-specific ordering/error semantics.
- Favorite side effects stay behind `FavoriteActionPipeline`, provider download artifacts, and `FilePlacementService`. The backend mutation completes first. Optional failures remain visible in durable event/action state and do not rewrite the protocol response.
- Favorite policy is exact to tenant, user, protocol, backend instance, and optional library. Admin tenant policies and permitted user overrides never authorize another user's account, artifact, or managed root.
- Jellyfin library refresh sends the configured API key only to the configured Jellyfin origin. Subsonic/Navidrome refresh resolves the user's encrypted credential reference just in time, posts it to `startScan`, and never persists the credential in the job payload or refresh audit.
- Unfavorite/unstar is logical. It can cancel pending optional work, but it cannot delete source media or a managed file. Explicit managed removal requires confirmation, matching ownership scope, and safe reference count.
