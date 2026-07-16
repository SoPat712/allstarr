# Metadata, Matching, And Placement

Use this file for MusicBrainz, matching, library indexing, favorites, storage roots, hardlinks, path templates, beets, and Picard-compatible behavior. The root plan is [OVERHAUL.md](../../../OVERHAUL.md).

## Local Code

- [allstarr/Services/MusicBrainz/MusicBrainzService.cs](../../../allstarr/Services/MusicBrainz/MusicBrainzService.cs)
- [allstarr/Services/Common/FuzzyMatcher.cs](../../../allstarr/Services/Common/FuzzyMatcher.cs)
- [allstarr/Services/Spotify/SpotifyTrackMatchingService.cs](../../../allstarr/Services/Spotify/SpotifyTrackMatchingService.cs)
- [allstarr/Controllers/JellyfinController.Spotify.cs](../../../allstarr/Controllers/JellyfinController.Spotify.cs)
- [docs/steering/DOWNLOADS.md](../DOWNLOADS.md)

## External References

- [MusicBrainz API](https://musicbrainz.org/doc/MusicBrainz_API)
- [beets path formats](https://beets.readthedocs.io/en/stable/reference/pathformat.html)
- [Picard file naming scripts](https://picard-docs.musicbrainz.org/en/latest/tutorials/naming_script.html)

MusicBrainz requires responsible API use, including a meaningful user agent and rate limiting. Keep local caching and avoid blocking playback on MusicBrainz lookups.

## Scope, Ownership, And Snapshot Context

The following is the implemented durable data contract. Provider-specific compatibility readers may remain, but new matching and playlist work uses these records.

Every library record, external snapshot, match, manual override, favorite event, and placement must carry enough scope to answer all of these questions without reading request headers again:

- Which backend instance and protocol originated the action?
- Which verified backend principal and canonical Allstarr user own it?
- Which library root or backend-library scope is visible to that user?
- Which provider instance and `ProviderAccount` supplied the data?
- Which policy, source snapshot version, and correlation/job ID produced the decision?

Use stable IDs for these fields; never persist a raw backend token, provider cookie, or secret in a snapshot, match reason, or audit payload. A manual override is scoped to its owner and library unless an administrator explicitly creates a broader policy. A candidate from another user's private library or provider account is not eligible merely because its metadata matches.

## Library Index

`LibraryIndexService` is backed by the explicitly selected durable database.

Indexed data:

- backend item ID
- protocol source
- user/library scope
- file path
- title
- artist
- album
- album artist
- duration
- ISRC
- MusicBrainz IDs
- provider IDs
- canonical recording ID and accepted identity-link decision version
- cover art reference
- date indexed
- modified timestamp

The index should make local lookup fast enough that playback and playlist rewriting do not feel slower than a normal streaming service.

## Track Identity

`TrackIdentityService` owns provider-neutral matching.

Use a provider-neutral identity graph rather than expanding the current Spotify mapping table:

- `canonical_recordings` identify recordings independently of a provider, backend, release, or file.
- `provider_track_identities` attach typed external IDs to a canonical recording. One recording can have many identities across Spotify, Apple, Deezer, Qobuz, MusicBrainz, backend catalogs, and future providers.
- `library_tracks` represent indexed local renditions. More than one local file or backend item can match the same canonical recording without being collapsed into one file.
- `track_matches` retain accepted, rejected, suggested, and manually pinned links plus their evidence and decision version.

A typed provider identity includes provider ID, resource kind, catalog or namespace when relevant, and the opaque source ID. Account access does not become part of a global catalog ID, but an account-scoped personal-library ID keeps the owner and account scope needed to prevent cross-user reuse. A provider identity may resolve to one accepted canonical recording for a decision version. Conflicting candidates stay unresolved until policy or a person settles them.

Do not add a new `spotify_mappings` source of truth. During migration, old Spotify-specific code may read a compatibility projection over these records. All new matching, playlist, stream, and download decisions use the provider-neutral model.

Inputs:

- identity-resolution context: Allstarr user, backend instance/principal, visible library scope, selected provider account, and policy version
- local indexed tracks
- external provider track
- playlist context
- MusicBrainz lookup
- ISRC lookup
- manual overrides

Signals:

- exact backend ID
- exact external provider ID
- ISRC
- MusicBrainz recording ID
- MusicBrainz release ID
- MusicBrainz artist ID
- title normalized match
- artist normalized match
- album normalized match
- album artist normalized match
- duration similarity
- explicit flag
- release year
- playlist context
- existing user mapping

Output:

- selected local track
- confidence score
- candidate list
- reason list
- warning list
- manual override status
- source snapshot versions and the scope/policy that made the result eligible

### Match Lifecycle

`TrackIdentityService` is a decision service, not a downloader or playlist writer. Its lifecycle is:

1. Capture an immutable external-track snapshot and its provider/account context.
2. Limit local candidates to the caller's visible library scope.
3. Apply scoped manual overrides first, then deterministic identity signals, then fuzzy signals.
4. Return an explainable result with a confidence threshold and unresolved/ambiguous state; do not silently choose a weak match just to fill a playlist position.
5. Persist the decision, inputs/snapshot versions, and reason codes so it can be reviewed or recomputed when metadata, policy, or an override changes.
6. Let the caller decide whether to stream an external fallback, enqueue a download, or leave the item unresolved. Playback and playlist reads must not wait on enrichment or a remote retry.

An accepted identity link is translation evidence, not provider permission or availability. When a caller requests a stream or download, the router intersects the recording's verified provider identities with the providers and accounts selected for that capability, then applies scope, health, quality, and fallback policy. It never uses a disabled or unauthorized provider merely because the recording has an ID there.

Example explanation:

```json
{
  "confidence": 0.94,
  "selectedBackendItemId": "abc123",
  "reasons": [
    "isrc matched",
    "title normalized match",
    "artist normalized match",
    "duration within 2 seconds"
  ],
  "warnings": []
}
```

## Metadata Merge

`MetadataMergeService` owns explainable merge planning.

Default merge order:

1. Local user-edited metadata
2. MusicBrainz identity, credits, genres, dates
3. Provider metadata from user-preferred providers
4. Fallback provider metadata

Rules:

- Retain raw provider snapshots.
- Store merge decisions.
- Allow remerge when provider snapshots change.
- Do not block streaming on enrichment.
- Cover art source should be configurable.

## Favorites And Downloads

`FavoriteActionPipeline` owns durable favorite side effects.

Supported actions:

- record favorite event
- preserve normal Jellyfin favorite behavior
- match track to local library
- download missing track using selected download provider
- enrich metadata
- tag file
- place file into target root
- refresh backend library
- add to liked-songs playlist

Defaults:

- Auto-download off.
- Add to virtual liked list on.
- No original library deletion.
- Managed file removal only after explicit user action.
- Extra actions require opt-in per user, backend, or admin policy.

### Favorite Lifecycle

The favorite/star mutation and the optional Allstarr workflow are separate operations:

1. The protocol adapter first preserves or proxies the backend's normal favorite result. A backend failure must not be reported as a successful Allstarr favorite.
2. After a successful mutation, write a durable, idempotent `favorite_event` and outbox entry keyed by backend instance, canonical user, operation, item, and source revision/correlation ID.
3. Resolve the user's visible library and effective provider account under the saved policy. The event must record whether it used a user, library, or global account.
4. Enqueue opted-in work: match, download if missing, enrich/tag a safe output, place, refresh the backend, and update the virtual liked list or selected playlist.
5. Each step records status, retryability, idempotency key, and a safe compensation action. Replaying an event must not create duplicate downloads, placements, backend playlist entries, or audit events.
6. A failure, timeout, or cancellation leaves the favorite state intact and exposes a recoverable job result. It does not delete a prior good file or silently change the provider account.

An unfavorite/unstar removes the favorite or virtual-liked state and may cancel work that has not started. It does **not** remove a file. “Remove managed copy” is a separately confirmed managed-file action with ownership, reference-count, and audit checks.

Jellyfin and Subsonic favorite mutations preserve the backend result first, then enqueue this durable, scoped lifecycle. Compatibility playlist reads do not bypass the favorite pipeline or broaden its side effects.

## Playlist Virtualization Lifecycle

Provider-neutral playlists use the same identity, account, and durable-work rules as favorites. Spotify's existing injection flow is the migration prototype, not the final shared contract.

Persist `playlist_links`, immutable `playlist_source_snapshots`, `playlist_sync_runs`, ordered per-entry results, sync-owned target membership, and a reference to `job_schedules` when scheduled. These records keep the source snapshot separate from the backend target and let an operator explain what was matched, skipped, reused, added, reordered, or rejected on each run.

1. Resolve the caller and effective `ProviderAccount`, then load the scoped playlist link and its policy/rule version.
2. Fetch pages into a source snapshot with provider revision/ETag when available, source-account ID, name, description, artwork reference, order, and retrieval time. Preserve the last known-good snapshot on a temporary provider failure.
3. Match each source track through `TrackIdentityService` using the playlist, library, and account context. Keep unresolved and low-confidence tracks visible in a preview/diagnostic result instead of hiding the decision.
4. Apply ordered rules such as local-over-external, dedupe, unavailable hiding, and confidence thresholds. Record the applied rule version and match decisions.
5. For `virtual` mode, shape the protocol response without writing the backend playlist. Return accepted local backend items on the fly. Use an external stream fallback only when the link policy and provider router allow it.
6. For `materialized` or `hybrid` mode, enqueue idempotent writes to the selected Jellyfin or Subsonic/OpenSubsonic-compatible backend, including Navidrome. The materialized playlist contains accepted local backend items only. Never mutate a source-provider playlist unless a distinct policy explicitly authorizes it.
7. Run materialization immediately after preview or from a saved durable schedule. A schedule records timezone, next run, overlap policy, misfire policy, retry policy, and whether this link uses reconcile or recreate behavior.
8. Sync source name, description, and artwork when the target adapter supports them. Store the source values and a clear unsupported result when it does not, so a later backend capability upgrade can apply them.
9. Surface progress, stale-snapshot state, skipped tracks, failures, conflicts, and cancellation to the owner. Retrying a sync must reuse the same run idempotency keys rather than append duplicates.

Materialization modes:

- `reconcile` is the default. Reuse an existing target playlist, reuse each desired local item already present, add only missing matched items, and reorder the sync-owned entries to match the immutable source snapshot. Do not remove and re-add an item just to move it, and never rewrite or retag its audio file.
- `recreate` is an explicit per-link option. Build a fresh target result on every manual or scheduled run. Prefer a staged replacement when the backend supports it; otherwise use the adapter's documented recoverable rebuild flow. A retry continues the same run instead of creating another playlist.

Unmatched, ambiguous, and below-threshold tracks are omitted from materialized membership and listed in the run result. Matching alone never downloads them. A separate opt-in download policy may enqueue downloads, but those files can join the playlist only after placement, backend refresh, and a verified local backend item match.

Conflict and idempotency rules:

- Snapshot the source before planning writes. A provider change during the run becomes a later source revision, not a moving target.
- Key a reconcile run by link, target backend, source revision, rule version, and run generation. Retries reuse the generation. A later scheduled recreate uses a new generation even when the source revision did not change.
- Record the target revision or an equivalent fingerprint before writing. If a person or another job changes it, stop with a visible conflict unless the saved policy allows Allstarr to reconcile its own sync-managed entries.
- Track which target entries belong to the link. Preserve unrelated manual entries by default. Removal of stale sync-owned entries is a separate mirror rule; it never removes an audio file.
- Apply dedupe policy before writing and preserve the remaining source order exactly. Repeated delivery of the same job cannot duplicate an item or playlist.
- A partial failure retains the last known-good target where the backend permits staged replacement. Otherwise persist enough completed operations and target state for a safe retry or explicit repair.

## File Placement

`FilePlacementService` owns managed placement.

Behavior:

- Validate target root.
- Generate path from template.
- Avoid path traversal.
- Keep hardlinks disabled until source and destination immutability are represented by a durable lease. A runtime boolean is not an ownership guarantee.
- Try native reflink/copy-on-write on Linux and macOS where the target filesystem supports it.
- Fall back to copy.
- Record placement method.
- Avoid overwriting existing files unless configured and safe.

### Managed-File Safety Contract

`FilePlacementService` owns this contract. Older provider download paths can still stage their artifacts, but a file does not become a managed library output until placement records ownership and a durable reference.

- Record every managed output with root ID, canonical path, content fingerprint, filesystem device/file identity where supported, placement method, source/download job, owner/scope, managed-state flag, and durable references before it can be deleted or reused.
- Build and validate the target path against the configured root before writing, then validate resolved parents/final path again without allowing symlink escapes. Do not trust a string-prefix check or raw provider metadata as containment proof.
- Download or transform into a uniquely named staging file under controlled storage, validate it, then atomically finalize within the target filesystem. A crash, cancellation, or retry may clean only its own staging file and must leave a completed managed file untouched.
- Placement writes a durable, root-local operation journal before finalization. If the process stops between the atomic filesystem rename and the database ownership commit, a retry with the same durable reference verifies the tenant, root, scope, target, length, and SHA-256 before adopting the exact finalized file. A mismatch is left untouched and reported for operator review. Completed ownership removes the journal.
- Resolve collisions by content fingerprint and managed-record compatibility. Reuse/increment a compatible managed record, otherwise choose a deterministic safe suffix; never overwrite an unrelated file.
- The target design may hardlink only an immutable, eligible managed file on the same filesystem. Current production placement deliberately does not hardlink because immutability is not yet a durable lease. It uses reflink or copy instead. A hardlink shares an inode, so any output that may be tagged, transcoded, or rewritten must remain independent.
- Give each consumer a stable durable reference key. Retrying that consumer is idempotent. A different consumer adds a reference, and explicit release marks only that reference released and decrements the protected count once.
- Delete only an explicitly selected Allstarr-managed output after ownership and reference-count checks. Removal releases the final active reference in the same durable update. Never delete an existing backend-library source, infer deletion from a playlist change, or remove a shared file because one user unfavorited it.

Managed favorite placement renders the configured path template from the resolved local/provider metadata before
the file enters the library. Enrichment then creates a versioned, explainable merge plan. Local values win,
MusicBrainz identity and release data fill safe gaps, and provider snapshots fill only what remains. Tag writes use
a same-directory staging file and atomic replacement, update the managed ownership checksum and revision, and map
MusicBrainz recording, release, release-group, and artist IDs to TagLib's Picard-compatible native fields. A retry
recognizes an already completed atomic swap instead of applying the same tags twice. Path-plan values are retained
for audit and future template changes; enrichment does not rename a file that a backend may already have indexed.

Templates:

```text
{albumArtist}/{album}/{track:00} - {title}
{artist} - {title}
{genre}/{artist}/{album}/{title}
{year}/{albumArtist}/{album}/{track:00} - {title}
```

Roots:

```text
/media/Music
/media/Music-Genre1
/media/Music-Genre2
/media/Users/{user}/Music
```

## Add-Only Safety

Allstarr should only add to original libraries by default.

Allowed:

- create managed downloads
- hardlink/copy into configured targets
- clean Allstarr cache/temp/transcode files
- delete Allstarr-managed downloads after explicit user action

Not allowed by default:

- delete existing backend library files
- rewrite existing source files in place
- move existing source files
- infer deletion from playlist removal
- tag or transcode an existing source-library inode through a hardlink

An unfavorite changes the logical favorite state only. A user must explicitly request managed-file removal, and that request must prove the file is Allstarr-managed and no remaining placement/reference protects it.

## Tests

Required test areas:

- path template rendering
- invalid path rejection
- hardlink fallback to copy
- native reflink independence or clean fallback without a partial destination
- local library indexing
- ISRC match
- MusicBrainz ID match
- fuzzy title/artist/duration match
- manual override priority
- metadata merge precedence
- atomic managed tag staging, completed-swap recovery, and Picard-compatible MusicBrainz fields
- favorite event without auto-download
- favorite event with fake download provider
- identity and candidate isolation across users, libraries, and provider accounts
- source snapshot/version persistence and scoped manual-override precedence
- ambiguous match remains unresolved below its policy threshold
- favorite-event idempotency, retry, cancellation, and a failed action that preserves favorite state
- playlist virtual/materialized lifecycle, account selection, stale snapshot handling, and duplicate-safe retry
- one canonical recording linked to several provider IDs and several local renditions without cross-account leakage
- capability routing that considers only selected, authorized provider identities for stream and download
- Spotify compatibility projection backed by provider-neutral identity records rather than a Spotify-only source of truth
- manual and scheduled playlist materialization into fake Jellyfin and fake Subsonic/Navidrome targets
- reconcile mode reuses existing items, adds missing local matches, preserves exact order, and reports skipped tracks
- recreate mode replaces once per run and remains duplicate-safe after interruption or retry
- playlist name, description, and artwork sync, including an explicit unsupported target capability result
- target-revision conflicts, manual-entry preservation, stale-entry mirror policy, and immutable source snapshots
- atomic placement finalization and safe cleanup after interrupted work
- symlink/path-containment rejection, collision handling, and reference-counted managed-file removal
- tagging after hardlink is rejected or uses an independent output, proving the source inode is unchanged
- stable filesystem identity capture, idempotent reference acquisition, and one-time explicit reference release
