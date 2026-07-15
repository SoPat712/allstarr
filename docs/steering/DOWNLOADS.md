# Downloads And Local File State

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Core Download Contract

`IDownloadService` and `BaseDownloadService` own the common download lifecycle:

- active download tracking
- concurrency limits
- request throttling
- cache-versus-permanent storage handling
- metadata embedding
- stream-on-demand support
- background Spotify ID enrichment through Odesli where relevant

Provider download services should implement provider-specific auth, manifest handling, and file transfer, not re-implement the common lifecycle.

## Durable Download And Favorite Boundary

Legacy playback services still track active transfers and cache state in memory, but favorite-triggered mutation
uses durable jobs and artifact records. Redis/Valkey may accelerate a cache or lock. It is not the record of a
favorite, download artifact, placement, enrichment application, refresh, or playlist write.

- Persist the requested action, canonical user/backend/library scope, selected provider account scope, source snapshot/policy version, idempotency key, and correlation ID before dispatching a background worker.
- A retry must reuse the same job/action key and detect an existing successful output or placement before transferring again. Job progress and failures must be visible without leaking provider credentials or URLs containing secrets.
- Cancellation stops uncommitted network/file work where possible, cleans only the caller's staging artifacts, and leaves completed cache/permanent/managed outputs intact.
- A failed provider attempt records a typed, retryable/non-retryable result. It must not switch to a different user's account or silently turn a failed favorite action into a successful placement.
- Stream-on-demand remains a latency-sensitive lane. Do not make playback wait for unrelated favorite or playlist jobs; when a stream shares a download artifact, coordinate through an idempotent artifact record rather than duplicate writers.

`FavoriteActionPipeline` saves the backend result separately from its optional Allstarr actions. Effective policy
is resolved for the exact tenant, user, protocol, backend, and optional library. Admins can set the tenant/backend
policy; users can override their own values where management mode permits. Download, placement, enrichment, and
refresh are off until the effective policy enables them. Placement requires download, and enrichment requires
placement, so a partial override cannot create an unsafe action chain.

`ProviderDownloadArtifactEntity` records the selected provider/account reference, staged and final paths,
checksum, length, lifecycle, and durable job lineage. The provider credential stays behind the routed capability
boundary and is not copied into the job or artifact record.

## Storage Layout

Compatibility playback still uses several file roots:

- `downloads/permanent`
- `downloads/cache`
- `downloads/kept`
- `downloads/transcoded`

`downloads/transcoded` holds quality-override files and is TTL-cleaned separately from the main cache.
Managed favorite outputs use configured media roots instead of a database blob. A normal Jellyfin, Navidrome,
operator, or backup process can access those files through the deployment's mounts and permissions.

## Path Rules

Always use `PathHelper` for generated file paths.

- `BuildTrackPath` sanitizes artist, album, title, provider, and external ID
- `ResolveUniquePath` avoids collisions
- Provider and external ID suffixes are intentionally embedded in filenames to reduce ambiguity

Do not join raw provider or metadata strings into file paths manually.

For managed-library placement, `PathHelper` remains the first sanitization layer, not the final safety boundary.
`FilePlacementService` validates canonical containment, rejects traversal and symlink escapes, stages and verifies
before atomic finalization, resolves collisions by managed-record/content identity, and records ownership and
durable references. Production hardlinks are currently disabled until immutability has a durable lease instead of a
caller assertion. Placement tries native copy-on-write on Linux/macOS and falls back to a verified copy. See
[references/metadata-matching-and-placement.md](references/metadata-matching-and-placement.md) for the complete add-only and tagging contract.
The final filesystem rename currently precedes the database ownership commit. An interruption in that narrow gap
leaves an unowned output that Allstarr will not automatically adopt, overwrite, or delete without a future verified
placement journal/reconciler.

## Stream Behavior

External playback intentionally downloads to disk before or while streaming so that:

- metadata can be embedded
- cached playback is possible
- later seeks and replays are safe

Once the server commits to an external download for playback, it may continue server-side even if the client disconnects. That behavior is deliberate.

## Quality Override Rules

Quality override requests are capped by the configured provider ceiling.

- Clients can request equal or lower quality than the configured default
- Quality-override files go to `downloads/transcoded`
- `CacheCleanupService` cleans that directory using the transcode TTL

Do not let client parameters silently raise provider quality above the configured maximum.

## Local Mapping State

`LocalLibraryService` owns:

- parsing external item IDs
- `.mappings.json` file state
- external-provider ID to local file-path lookups
- Subsonic scan triggers and status checks

If a feature changes file placement or external ID rules, update `LocalLibraryService` together with the provider download service.

During the overhaul, do not let `.mappings.json` or a cache entry become the only proof that a file is safe to reuse or remove. Final placement state needs a durable managed-file record; a mapping can point to it but cannot authorize deletion by itself.

## Download Activity

`DownloadActivityController` aggregates `IEnumerable<IDownloadService>` and combines download state with
backend-neutral `IPlaybackActivitySource` results. Backend-specific resolvers supply safe metadata and artwork;
Subsonic mode does not depend on Jellyfin session services.

If you change `DownloadInfo` semantics, update both the download services and the activity endpoint contract.

## Editing Guardrails

- Keep provider-specific download logic in the provider service, and common workflow in `BaseDownloadService`.
- Preserve path sanitization and unique-path behavior.
- Preserve the distinction between permanent, cache, kept, and transcoded outputs.
- Add tests for path safety, download-mode changes, or quality-override behavior.
- Do not tag, transcode, or rewrite an existing source-library inode through a hardlink. Produce a staged owned output first, or use metadata stored outside the audio file.
- Do not remove a kept/managed file as an implicit consequence of an unfavorite, playlist removal, job retry, or cleanup race. Removal requires an explicit managed-file action and ownership/reference-count verification.
- Keep favorite-triggered download, placement, enrichment, refresh, and playlist work in durable job/action records. Do not reintroduce fire-and-forget controller tasks.
