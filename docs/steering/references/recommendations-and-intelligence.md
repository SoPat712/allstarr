# Recommendations And Intelligence

Use this file for listening signals, profiles, explained recommendations, generated playlists, AudioMuse-AI,
Jellyfin InstantMix, and intelligence privacy controls. The root plan is [OVERHAUL.md](../../../OVERHAUL.md).

## Current Boundary

Intelligence is off until a user enables it for an exact tenant, user, protocol, backend instance, and library.
The policy chooses allowed signal types, enabled recommendation sources, retention days, and the explicit
Subsonic target credential when generated playlists may be written there. A user can turn the policy off and
purge retained signals, profiles, runs, candidates, generated sets, and entries for that exact scope.

Playback never waits for this work. Playback and scrobble observations enter durable, idempotent jobs after the
backend event succeeds. Eligible signals receive an expiry from the saved retention policy. Redis/Valkey is not
the source of truth, and controller `Task.Run` is not part of this path.

## Listening Profile And Seeds

`ListeningProfileService` builds a scoped profile from retained play, completion, skip, favorite, and playlist
signals. Favorites and completed plays increase habit weight; skips reduce it. Recent weighted track references
become recommendation seeds. The profile stores counts, its time window, and bounded track keys without storing
provider credentials.

An unlinked backend principal cannot write signals, read a profile, run recommendations, or see another user's
generated sets. Disabling or purging one scope does not touch another user, backend, or library.

## Recommendation Sources

Each source returns bounded candidates with a stable identity, score, and at least one explanation. The admin and
user UI reads exact-scope readiness before allowing a source to be selected.

- Jellyfin InstantMix is ready only for a linked Jellyfin scope. All six pinned InstantMix route classes remain
  covered by protocol fixtures.
- Last.fm uses the exact scoped encrypted account and `track.getSimilar` from habit-derived seeds. Missing or
  unauthorized account state is visible and cannot be selected as ready.
- ListenBrainz uses the exact scoped encrypted account and collaborative-filtering recommendations. Returned
  MusicBrainz recording IDs retain their identity through local matching.
- MusicBrainz local similarity uses MusicBrainz-enriched recording, release, artist, genre, credit, and
  relationship facts already present in the local library. MusicBrainz is metadata here, not a personalized
  recommendation service or user account.
- Local rules use retained private habits and exact local-library coverage.
- AudioMuse-AI is optional. It is selectable only after its configured URL passes the bounded health and contract
  check. Missing, unhealthy, unauthorized, and degraded states stay truthful in the UI.

Recommendation providers do not download a candidate. A generated playlist can contain only candidates that
later resolve to exact accepted local backend items.

## Runs, Explanations, And Generated Playlists

Recommendation runs are durable and idempotent. They snapshot source selection, retention policy, seeds, and the
explicit target credential reference. Candidates retain source identity and explanation signals. The UI shows
empty, loading, configured, disabled, degraded, unauthorized, and error states from current API data instead of
static capability promises.

`SmartPlaylistService` saves an ordered generated set and queues `smart-playlist.materialize`. Jellyfin and
Subsonic/Navidrome materializers reuse the shared backend playlist targets:

- Resolve only the exact tenant, user, backend, and library.
- Match by exact backend item, library track, MusicBrainz recording ID, ISRC, or verified provider identity.
- Preserve candidate order and skip unmatched or ambiguous candidates with a durable explanation.
- Reconcile idempotently under a collision-safe Allstarr playlist name.
- Never download an unmatched candidate. Download remains a separate explicit policy and job.
- Write a safe description and report backend metadata limits. No artwork is claimed when no generated artwork
  exists.
- Persist pending, running, succeeded, failed, unsupported, or cancelled state plus backend playlist ID, target
  revision, and a safe error code.

Jellyfin uses its configured backend authentication and rejects an unnecessary generated-playlist credential.
Subsonic/Navidrome requires the credential reference saved on the intelligence policy. The reference must belong
to the same tenant and remain active. It is snapshotted through the run and generated set, then opened just in
time by the existing encrypted playlist authentication resolver. The materializer never borrows a credential
from another playlist link.

## UI And Test Contract

The intelligence screen is available to administrators and linked users. It includes privacy and retention
controls, source readiness, explanation disclosure, generated playlist status, keyboard focus, live status text,
and a single-column narrow-screen layout.

Coverage includes policy scope and purge, signal expiry, habit-derived seeds, source readiness and identities,
provider failure classes, run idempotency, explanation persistence, generated-set state transfer, exact local
matching, ordered Jellyfin/Subsonic writes, explicit Subsonic credentials, retry-to-success, cancellation,
tenant isolation, admin middleware, controller DTOs, and WebUI contracts. Tests use fake accounts, fake backend
targets, fake HTTP, deterministic clocks, and temporary databases. They do not require live provider traffic.

## References

- [apis/specifications/jellyfin/openapi-12.0.0.json](../../../apis/specifications/jellyfin/openapi-12.0.0.json)
- [AudioMuse-AI](https://github.com/NeptuneHub/AudioMuse-AI)
- [AudioMuse Jellyfin plugin](https://github.com/NeptuneHub/audiomuse-ai-plugin)
- [Jellyfin plugins docs](https://jellyfin.org/docs/general/server/plugins/)
- [docs/steering/SCROBBLING.md](../SCROBBLING.md)
