# Injected Playlist Matching Plan

Owner: this plan. Reference: [OVERHAUL.md](../../../OVERHAUL.md), [metadata-matching-and-placement.md](metadata-matching-and-placement.md), [SpotifyTrackMatchingService.cs](../../../allstarr/Services/Spotify/SpotifyTrackMatchingService.cs), [PerProviderTrackMatcher.cs](../../../allstarr/Services/Spotify/PerProviderTrackMatcher.cs).

## Status

Implemented. The Spotify injected matching loop now uses a provider-agnostic
`PerProviderTrackWalker` that walks playback providers in configured order,
stops on the first verified identity or per-provider accept, and falls back
to title-only retries on the first N providers. Apple MusicKit and any future
injected source (Deezer, Qobuz, extension) can reuse the same walker.

The walker and its scoring live in
`allstarr/Services/Spotify/PerProviderTrackMatcher.cs`. The matching service
calls it through `WalkProvidersForTrackAsync` after the local-first pass
finishes. Tests in `allstarr.Tests/PerProviderTrackWalkerTests.cs` cover the
per-provider walk, early stop, low-score advance, ISRC short-circuit,
title-only retry, and unknown provider handling.

## Problem Statement

Injected playlist matching (Spotify → backend, Apple MusicKit → backend) works, but it does not match the way allstarr-v2 did. The current pipeline is:

1. One bulk pass over Jellyfin playlist items builds a Jellyfin × Spotify candidate grid.
2. Per unmatched Spotify track, the runner issues one metadata search that fans out to every enabled provider in parallel (`SearchPlayableSongsAsync` interleaves results across providers).
3. The first acceptable score wins; provider priority only sorts candidates after the fact.

That is too coarse in two ways. It still surfaces every provider's results, even when a configured high-priority provider could supply a better match. It also does not give the per-provider fallback that v2's per-track, per-provider loop gave. The result is more bad matches, more metadata noise per track, and fewer matched tracks when one provider would have hit cleanly.

## What allstarr-v2 Did Differently

v2 iterated per Spotify track, then per provider, in priority order. For every track it asked the top-priority playable provider first; on a typed miss it stopped there and moved to the next provider. Local library was the implicit first stop and the search was only the fallback path. v2 cached per-track outcomes and used a different fuzzy strategy (title-stripped → primary-artist query, then a re-query with the full artist list on miss).

## Target Behavior

1. Local library first, always. The matching loop must confirm "no local item" before any provider request.
2. Per-track, per-provider. The matching service walks providers in `GetEnabledPlaybackProviders()` order and only advances on typed misses, never on a low score.
3. Stop early on a verified identity. If ISRC, MusicBrainz recording, or a manually pinned link returns a local or external match, persist it and move to the next source track.
4. Cache and rehydrate. A matched track should be reusable across runs; only changed snapshots or re-pinned overrides should force a re-search.
5. Be explainable. Each accepted match must report which provider, which query, which signal won, and which candidates were rejected.

## Pipeline (Implemented)

For every unmatched source track in the playlist (Spotify, Apple MusicKit, future Deezer/Qobuz playlists):

1. Local-first pass.
   - Query the local library index by normalized title + primary artist.
   - Score against the source track using the current local fuzzy strategy.
   - If the top local candidate is above the local-accept threshold, persist the match and move to the next source track.

2. Per-provider walk in configured order (`PerProviderTrackWalker.WalkAsync`).
   - For each provider in `GetEnabledPlaybackProviders()`, in order, call that provider's own concrete metadata service.
   - For the current provider, first try `FindSongByIsrcAsync(source.Isrc)` when an ISRC is present. An ISRC hit is a verified identity and stops the walk with `matchType: "isrc"`.
   - If no ISRC hit, run `SearchSongsAsync(titleStripped + " " + primaryArtist, limit)`. Score the candidates and accept the top one when it crosses `PerProviderAcceptThresholds.ProviderAcceptScore` (or the artist override / title-substring rules).
   - On typed miss, low score, or any failure, advance to the next provider. We never retry the same provider with a different query inside the walk.

3. Title-only retry.
   - If the per-provider walk produced no match, retry with a title-only query (no artist) on the first `TitleOnlyProviderCount` providers. Useful when the source artist is spelled differently from any of the provider's catalog artists.

4. Persist and continue.
   - Each accepted match is saved through the same mapping service path the current code uses, then the loop advances to the next source track.

## Per-Track State

`PerProviderMatchResult` records the accepted match and a `walked` list of
every provider step that was attempted. Each `PerProviderAttempt` records
the provider id, the query that was used, the candidate count, the top
score, the outcome (`accepted`, `miss:not-found`, `miss:low-score`,
`miss:no-service`, `miss:error`, `miss:empty-results`, `pinned-local`),
and a `ReasonCode` for the per-step reason.

The result is added to the per-track candidates list and the rest of the
existing pipeline (local-first, greedy assignment, snapshot cache, ordered
match) keeps working unchanged.

## Provider Priority Semantics

The user has the impression that "metadata priority" is not what they want. They are right that playback priority is what matters. The actionable change is:

- Keep the metadata lane for search/discovery only and keep it labeled as such.
- Match per-provider walks against the *playback* priority list, not the metadata list. The Settings → Provider priority → Streaming/Download list is what controls per-track fallback.
- The pinned "Local library" item that is now visible at the top of the Streaming and Download priority groups is also the implicit first stop in the matching loop. The walker records `pinned-local` for that step and continues.

## What Stays The Same

- Local-first pass with the current fuzzy scorer.
- ISRC preference toggle.
- Existing `MatchedTrack` cache keys, `SaveLocalMappingAsync`, and `SaveExternalMappingAsync` paths.
- Per-batch parallelism (BatchSize 11) for tracks that are still being matched. The intra-track loop is sequential per track so per-provider fallback is deterministic.
- Cache reuse. A track already accepted by a prior run is not re-searched unless the snapshot or a manual override changed.

## What Changed (Implemented)

- New `PerProviderTrackWalker` in `Services/Spotify/PerProviderTrackMatcher.cs` walks providers in order, calls each provider's own `IConcreteMetadataService`, and stops on the first acceptable match.
- `InjectedSourceTrack` is a provider-agnostic descriptor so Apple MusicKit and any future injected source can feed the same walker.
- New `PerProviderAcceptThresholds` and `PerProviderTrackScorer` so the scoring rules are centralized and can be tuned per provider class (Apple, Deezer, Qobuz, extensions) without touching the walk loop.
- `PerProviderServiceResolver` maps a provider id to a concrete metadata service using the existing `IConcreteMetadataService` registration. Apple MusicKit, SquidWTF, and other known aliases are handled.
- New debug logging in the matching service reports the actual walk (provider, query, score, outcome) for every track, plus a summary of which providers walked and which step won. This is what v2's logs gave operators.
- WebUI track mapping dialog already exposes per-track "Search local library", "Search music providers", and "Rematch automatically". The plan keeps these actions and reuses the same loop.

## Implementation Order (Done)

1. Add a per-provider search adapter that uses the existing `IConcreteMetadataService` implementations; do not change their public shape. → `PerProviderServiceResolver` + `PerProviderTrackWalker.StepProviderAsync`.
2. Refactor the unmatched track loop in `MatchPlaylistTracksWithIsrcAsync` to use the new per-provider walk. Keep the local-first pass exactly as it is. → `WalkProvidersForTrackAsync`.
3. Add per-provider accept thresholds. Start with the current 40 / 70 / 85 from `TryMatchByFuzzyAsync` and tune per provider. → `PerProviderAcceptThresholds` (defaults: `ProviderAcceptScore=40`, `ArtistOverrideScore=70`, `ArtistOverrideTitleScore=30`, `TitleSubstringScore=85`, `TitleOnlyProviderCount=2`).
4. Record the per-provider walk via `PerProviderMatchResult` and `PerProviderAttempt` so the loop is explainable. Kept as a sibling to the existing `MatchedTrack` shape, not a replacement.
5. Add tests: per-provider fallback in configured order, early stop on a verified ISRC match, low-score providers do not block later providers, local match still short-circuits the provider walk, title-only retry, no-match records every walked provider, unknown provider id records `no-service` and continues. → `allstarr.Tests/PerProviderTrackWalkerTests.cs` (7 tests).
6. Update the matching log line so it reports the actual walk, not just the final score. → added to the per-track lambda in `MatchPlaylistTracksWithIsrcAsync`.
7. Update the contract test that asserted the old shape. → `LegacyMappingReadinessContractTests.AutomaticPlaylistMatching_QueriesOnlyPlaybackCapableProviders`.
8. Document the new loop shape in this file and cross-link from `metadata-matching-and-placement.md`.

## Risks

- Per-provider search can be slower than the current parallel fan-out when many providers are enabled. The mitigation is to stop on the first accept and to keep the per-track sequential loop short.
- Operators who depended on the current "search everything then rank" behavior may see fewer candidates per track. The mitigation is the per-track "Search music providers" dialog which already exposes the full fan-out.
- Cache invalidation. If a manual override changes the source data, the existing cache must re-verify. The current cache logic already does that; the new loop must respect the same triggers.

## Out Of Scope

- New fuzzy algorithms. We keep the existing `FuzzyMatcher` and `CalculateLocalMatchScore` until tuning data shows they need to change.
- A new matching service. The existing `SpotifyTrackMatchingService` is the owner for the injected Spotify path. Apple MusicKit and other injected sources will move to the same loop in a follow-up once Spotify proves it.
- A new UI for the per-provider walk. The existing track mapping dialog and the existing per-track log are the surface until we have data that demands more.
