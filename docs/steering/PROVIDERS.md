# Provider Services

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Shared Provider Contract

`IMusicMetadataService` is the contract all provider metadata services implement.

Key responsibilities:

- song, album, artist, and playlist search
- exact item lookup
- ISRC lookup
- artist albums and artist tracks
- playlist metadata and playlist tracks

Provider services should return domain models, not backend-specific response objects.

`TrackParserBase` centralizes typed external ID generation and shared parse helpers. New provider parsers should reuse it.

## Registration Rules

`Program.cs` may register more than one provider at a time.

- The last registered `IMusicMetadataService` is the default injected one.
- `ParallelMetadataService` can race all registered providers.
- Deezer and Qobuz may be paired for playlist support depending on `EnableExternalPlaylists`.

If you change provider registration, think about both default injection and multi-provider racing.

## Current Providers

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

- Endpoint discovery happens at startup
- Metadata and downloads use `RoundRobinFallbackHelper`
- Search code handles query variants and endpoint failover
- Downloads fall back across quality tiers and mirrors
- Odesli conversion is used for Spotify ID enrichment

### MusicBrainz Enrichment

MusicBrainz is optional and sits alongside providers as enrichment, not as the main playback provider. When enabled, genre enrichment plugs into provider metadata services.

## Editing Guardrails

- Keep provider names stable and lowercase in IDs and cache keys.
- Use `TrackParserBase` for external ID shapes.
- Keep provider-specific HTTP and parsing logic inside provider service classes.
- Reuse `RoundRobinFallbackHelper`, `RetryHelper`, and shared cache helpers instead of hand-rolling similar logic.
- If a provider contract changes, update provider tests and any matching or lyrics code that depends on provider metadata.
