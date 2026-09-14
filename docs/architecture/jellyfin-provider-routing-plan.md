# Jellyfin and provider routing plan

Status: accepted first-use-case plan; working-tree behavior still requires live qualification.
Decision date: 2026-09-14.

## Release target

The first qualified Allstarr deployment is deliberately narrow:

- Jellyfin is the native library and authentication backend.
- Imported provider playlists retain every source row and source order.
- Each source track may resolve to one accepted local Jellyfin item and several accepted external provider identities.
- Playback prefers an accepted local Jellyfin item. If no local item is selected, external playback follows the streaming-provider order from Settings.
- The actual serving provider is recorded after the stream opens and is exposed in diagnostics and song details.

BrainzMash, SkyHook, a MusicBrainz mirror, a Lidarr metadata service, and a merged global artist/discography catalog are not part of this target. The existing bounded MusicBrainz lookup may remain supporting evidence for a recording, but it is never required for search, matching, routing, or playback.

## One deterministic decision

Matching and routing are separate decisions.

### 1. Establish recording identity

1. Apply an authoritative manual pin or rejection first.
2. Score local and external candidates using stable IDs when available, then title, full artist credits, album context, duration, explicitness, and semantic version tags.
3. Reject clean/explicit, live/studio, remix/original, instrumental/vocal, or materially different-duration conflicts even when the names resemble each other.
4. A candidate is playable automatically only after it independently meets the acceptance threshold and artist-evidence requirement.
5. For each provider, retain the closest accepted candidate for the source recording. Lower-scoring candidates remain review evidence, not fallback routes.

Confidence establishes whether a candidate is the same recording. Provider preference never adds confidence or turns a tentative candidate into a playable route.

### 2. Order accepted routes

1. An accepted local Jellyfin candidate wins.
2. Otherwise, order accepted external identities exactly by `Providers:StreamingOrder`.
3. Try the first eligible, healthy, authorized route.
4. Before returning any audio, advance to the next accepted route after not-found, unavailable, rate-limit, incompatible-media, or bounded transient failure.
5. Stop on authorization, forbidden, cancellation, policy, or permanent failures.
6. Once bytes have been committed, never splice another provider or encoding into that response. A new playback request may select another route.

The source catalog provider does not own playback. For example, a Spotify playlist entry may play from Jellyfin, Apple Music, Deezer, or Qobuz without changing its playlist position or stable Allstarr identity.

## Audio quality rule

Provider order is the outer decision; audio quality is resolved inside the selected provider.

- `Audio:Quality` supplies one shared target/ceiling.
- A client bandwidth or codec request may lower that target but may not silently raise it.
- Each provider adapter requests the closest representation it supports at or below the effective target.
- A provider is skipped only when it cannot supply an allowed representation or its opened response is incompatible.
- A later provider offering a higher bitrate does not jump ahead of an earlier configured provider.

For the currently qualified configuration, `CdLossless` means Apple Music requests 16-bit/44.1-kHz ALAC, Deezer requests FLAC, and Qobuz requests 16-bit FLAC. The configured external order is Apple Music, Deezer, then Qobuz. Local Jellyfin remains first when its match is accepted.

## Ownership

| Concern | Existing owner |
| --- | --- |
| Candidate scoring, version safety, and manual authority | `Core/Matching/TrackMatchDecisionEngine` and `TrackMatchCommandService` |
| Provider/account eligibility and configured order | `Core/Routing/ProviderRouter` |
| Pre-response stream failover and serving-provider observation | `Core/Protocols/ProtocolProviderGateway` |
| Per-provider quality translation | existing provider streaming/download capability adapters |
| Source playlist order and local/external projection | `Core/Playlists/` |
| Runtime order and quality settings | durable runtime settings and the existing Settings/Integrations UI |

Do not add another router, matching service, catalog database, or provider-specific playlist coordinator.

## Required qualification

The use case is complete only after the same build passes all of these against the real deployment:

1. Sign in through Allstarr with a Jellyfin user and preserve that user's account scope.
2. Browse native Jellyfin artists, albums, tracks, artwork, and playlists unchanged.
3. Import a provider playlist and retain every entry, including unresolved entries.
4. Prove exact, fuzzy, featured-artist, album-edition, clean/explicit, live/remix, and duration-conflict fixtures.
5. Prove that two accepted candidates select local Jellyfin even when an external candidate scores higher.
6. Prove that multiple accepted external candidates select the closest candidate within Apple Music, then route Apple Music before Deezer before Qobuz.
7. Disable or fail each route in turn and verify bounded pre-response fallthrough without losing the stable track/playlist identity.
8. Request original, data-saver, lossy, and lossless playback and verify the same effective quality reaches every fallback adapter.
9. Verify range start, seek continuation, cancellation, empty body, HTML/error body, expired lease, retry, and the client's three-second startup budget.
10. Verify the serving provider appears in the response header, playback observation, activity detail, and song information without provider text in the canonical title.
11. Verify private/global account authorization, disabled/revoked accounts, cache ownership, and two-user isolation.
12. Rematch automatically created decisions with the current algorithm while preserving manual pins and rejections.

Focused unit and protocol tests run before deployment. Live qualification records the exact source commit, image digest, Jellyfin version, provider order, quality target, client/version, timings, and failures. A missing credential or unavailable provider is reported as not qualified, not counted as a pass.

## Deferred deliberately

- BrainzMash or another pooled MusicBrainz service.
- A bundled or required MusicBrainz mirror.
- Provider-neutral merged artist and global discography pages.
- A second SkyHook/Lidarr Metadata API dialect.
- Mandatory Lidarr or AudioMuse.
- Searching or fuzzy matching after the listener presses Play.

These may be reconsidered only after the Jellyfin-plus-provider route is stable and measured. The [reference ledger](reference-projects.md) remains research evidence, not a dependency list.
