# Provider Services

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Authoritative Provider Contract

`Core/Capabilities` owns the typed metadata, streaming, download, playlist, lyrics, and health contracts used by
built-ins and SDK v1 packages. `ProviderRegistry` validates descriptors and implementations. `ProviderRouter`
selects an eligible provider account by tenant, user, library, permission, policy, health, and capability.

`IMusicMetadataService` remains a legacy compatibility contract for older provider metadata services. New work
must enter through the typed capability core instead of extending last-registration-wins behavior.

Key responsibilities:

- song, album, artist, and playlist search
- exact item lookup
- ISRC lookup
- artist albums and artist tracks
- playlist metadata and playlist tracks

Provider services should return domain models, not backend-specific response objects.

`TrackParserBase` centralizes typed external ID generation and shared parse helpers. New provider parsers should reuse it.

## Legacy Registration Rules

`Program.cs` may register more than one provider at a time.

- The last registered `IMusicMetadataService` is the default injected one.
- `ParallelMetadataService` can race all registered providers.
- Deezer and Qobuz may be paired for playlist support depending on `EnableExternalPlaylists`.

If you change a compatibility provider registration, think about both default injection and multi-provider racing.
Typed routes must register an accurate descriptor and matching capability implementation atomically.

## Current Provider Notes

### Deezer

- Metadata from Deezer API
- Explicit-filter policy through `ExplicitContentFilter`
- Download decryption in `DeezerDownloadService`
- Quality tiers mapped from env and stream override settings

### Qobuz

- Uses app bundle secrets plus user auth token and user ID
- Quality format IDs map to Qobuz-specific download tiers
- `QobuzBundleService` owns app ID and secret discovery

### SquidWTF

- Optional endpoint discovery supports metadata
- Current routing permits metadata only
- Streaming, download, and playlist lanes are policy-blocked until a working endpoint and contract fixtures exist
- Search code handles query variants and endpoint failover
- Odesli conversion is used for Spotify ID enrichment

### Apple

- `apple-download` is an optional, separately deployed compatible gateway configured by URL. Standard and AIO do
  not bundle GAMDL, wrapper-v2, or the gateway stack.
- `apple-musickit` is a separate per-user MusicKit account for personal playlists and library operations.
- Never pass a Music User Token to the download gateway or treat gateway login state as MusicKit authorization.

### Spotify

- Durable provider-neutral playlist links are the current playlist path.
- The specialized session-cookie injection and Redis mapping flows remain compatibility-only.

### MusicBrainz Enrichment

MusicBrainz is optional and sits alongside providers as enrichment, not as the main playback provider. When enabled, genre enrichment plugs into provider metadata services.

## Editing Guardrails

- Keep provider names stable and lowercase in IDs and cache keys.
- Use `TrackParserBase` for external ID shapes.
- Keep provider-specific HTTP and parsing logic inside provider service classes.
- Reuse `RoundRobinFallbackHelper`, `RetryHelper`, and shared cache helpers instead of hand-rolling similar logic.
- If a provider contract changes, update provider tests and any matching or lyrics code that depends on provider metadata.
