# Allstarr - Architecture

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Runtime Overview

Allstarr is a single ASP.NET Core host with two listener surfaces:

- Port `8080`: the proxy surface for Jellyfin or Subsonic clients.
- Port `5275`: the admin UI and admin API surface.

At startup, `Program.cs` chooses one backend controller set with `BackendControllerFeatureProvider`:

- All admin controllers are always registered.
- Exactly one of `JellyfinController` or `SubsonicController` is registered.

This is critical because both backend controllers own catch-all routes.

`HostCompositionTests` boots both choices with fake upstream HTTP, asserts that exactly one protocol
controller is registered, and activates every registered controller. The one-protocol-per-deployment rule
is therefore a checked current invariant, not only a roadmap preference.

## Compatibility And Observability Seams

The compatibility layer preserves older routes while current durable and typed subsystems own new work:

- `JellyfinAuthFilter` permits only login/public bootstrap routes without client credentials. Before any
  other Jellyfin controller action it calls backend `Users/Me` using the client's credentials, preserves
  an upstream auth failure's status/body, stores the verified backend principal ID in request state, and
  resolves an existing canonical Allstarr identity when one is linked.
- `SubsonicAuthFilter` runs as a resource filter before model binding, verifies exactly one native password,
  salted-token, or API-key mechanism with the backend, preserves protocol failure status/body, and stores a
  backend username in request state. It also resolves an existing canonical identity when one is linked.
- `ProtocolExecutionContextFilter` runs after either protocol authentication filter. It projects the verified
  backend identity, optional canonical actor, backend instance, correlation ID, request cancellation, and a
  bounded deadline into one secret-free request context. An unresolved identity can keep transparent proxy
  behavior, but cannot authorize user-owned provider accounts or side effects.
- `SubsonicRequestParameters` and `SubsonicProxyService` retain method, query/form source, repeated ordering,
  body, content type, and upstream status for the characterized relay surface.
- `ProxyResponseResultFactory` preserves upstream status codes for JSON and empty proxy responses rather
  than turning an upstream JSON error into HTTP `200`.
- `IPlaybackActivitySource` and `IPlaybackMetadataResolver` decouple the admin download-activity screen
  from Jellyfin-only session and metadata services. Jellyfin supplies the current adapters; Subsonic mode
  starts without those adapters.
- `CurrentProviderSupportCatalog` is the visible compatibility and coverage overlay. Typed built-ins and packages
  meet at `ProviderRegistry`; lanes still marked `ConfiguredOnly`, partial, or unavailable remain compatibility
  paths until a typed adapter and focused contract tests exist. `ProviderStatusManager` observations are in-memory
  compatibility projections scoped by provider, capability, and account key.
- Provider configuration and health are distinct. Untested capabilities are `Unknown`, explicit tests pass
  through `Testing`, and only a successful observation is `Healthy`/ready. The compatibility router may
  still attempt eligible `Unknown` capabilities on legacy protocol paths. `ProviderRouter` is the policy
  boundary for typed capability adapters and authenticated protocol work.
- Remote extension installation is default-deny. SDK v1 packages pass checksum, manifest, permission, staging,
  lifecycle, and typed capability checks. JavaScript still runs as trusted code in constrained in-process Jint,
  not an operating-system sandbox.
- External unfavorite is logical and preserves managed files. Favorite-triggered optional work now enters the
  durable, tenant-scoped `FavoriteActionPipeline`; it no longer relies on controller `Task.Run` work or a Redis
  kept-file record. The backend favorite result stays separate from optional Allstarr action failures.

## Durable Foundation

The app has a verified durable control plane beside the legacy provider and protocol services. Its migration,
runtime-image, backup/restore, and Compose exit gates are complete.

- `AllstarrDbContext` is the source of truth for tenants, users, backend identities, provider accounts,
  secret references and versions, jobs, attempts, outbox messages, provider health, circuits, and backup
  records. Audio stays in configured media folders. The database stores paths and control-plane state, not
  song payloads.
- `DurableStorageInitializer` initializes PostgreSQL, applies checked-in migrations under a database advisory
  lock, and reports durable readiness. `DurableStorageRuntimeProbe` checks connectivity and exact schema on a
  bounded cadence after startup. PostgreSQL is the only runtime and offline storage-command target.
- `DurableBackupService`, `DurableRestoreTargetVerifier`, `DurableStateTransferService`, and the offline
  `storage` command provide verified backup, isolated restore, export, and empty-target import operations.
  Backup manifests are strict inputs, not advisory notes. Restore accepts only an artifact and manifest that
  agree with this build's current schema, then verifies the restored target before reporting success.
- `BackendIdentityResolver` maps the verified backend principal into a durable platform identity.
  `ProviderAccountResolver` applies tenant, global, user, and library scope before returning an account.
  Provider-account and job APIs enforce the same ownership rules for non-admin users. Provider-account
  management can be `AdminManaged`, `UserManaged`, or `Hybrid` without changing provider routing policy.
- `EncryptedSecretStore` stores only AES-GCM ciphertext, nonce, tag, version metadata, and key ID in the
  database. Encryption key material stays in a separate key-ring file. Secrets can be replaced, rotated,
  and revoked without returning raw values through the API. The offline `storage rotate-secrets` command
  re-encrypts active references under the current key while normal writers are stopped.
- `DurableJobQueue` and `DurableOutbox` persist idempotency, attempts, leases, retries, cancellation, and
  terminal results. Job idempotency is scoped to the initiating tenant and user. Sidecar unavailability uses a
  separate bounded deferral budget so waiting does not consume normal provider-failure retries.
- `DurableProviderHealthStore` records provider-account/capability observations, 15-minute rollups, retention,
  and circuit state. `PlatformReadinessService`, `SidecarHealthMonitor`, `OperationalMetricsService`, correlation
  middleware, and the redacting console logger expose useful state without credentials, account names, media
  URLs, filesystem paths, or exception bodies. Exact managed-account probes never borrow deployment-global
  credentials, and transient sidecar health persistence failures do not terminate the monitor or host.
- The SquidWTF browser helper returns a same-origin route instead of an upstream URL. Its bounded JSON transport
  disables redirects and proxies, resolves every hostname at connection time, rejects any non-public address,
  and connects to the exact validated IP to prevent DNS rebinding.
- The standard Compose file runs the core app with pinned PostgreSQL 18 and Valkey images, mounted secret files,
  persistent database/app volumes, and separate accessible download and kept-media mounts. The legacy
  Redis-to-Valkey conversion overlay has been removed. Pre-overhaul application state is not imported.

## Capability Core And Track Identity

The typed provider lane is shared by built-ins and SDK v1 packages while compatibility controllers preserve
older routes.

- `Core/Capabilities` defines immutable external IDs, actor/account/library execution context, quality and
  fallback policy, provider outcomes, capability contracts, descriptors, and the validated provider registry.
  Metadata, streaming, download, playlist, lyrics, and health are separate interfaces. An operational descriptor
  must bind exactly one implementation of the declared interface.
- `Core/Routing/ProviderRouter` builds an ordered plan instead of calling the first configured service. It filters
  candidates by provider policy, enabled capability state, authoritative account scope and revision, health and
  circuit state, sidecar readiness, quality, managed-download permission, download idempotency, and deadline.
  Its decision record contains provider/account IDs and stable reason codes, never external track IDs, provider
  bodies, credentials, or URLs.
- Cross-provider fallback is failure-classified and identity-safe. Only explicitly allowed typed failures can
  advance to the next candidate. A different provider receives a track only after `TrackIdentityService` returns
  an exact verified or pinned translation. Text similarity is not a routing identity.
- `Core/Matching/TrackIdentityService` owns normalized exact keys and conflict behavior. Catalog links are
  tenant-scoped, account links require an authorized current provider account, an accepted exact ID cannot be
  silently remapped, and multiple target IDs in one scope are reported as ambiguous.
- `canonical_recordings` and `provider_track_identities` are durable control-plane records. They contain IDs,
  scope, verification, and decision version, not audio; audit events capture link creation and conflicts. The
  migration enforces same-tenant
  canonical relationships, separate catalog/account uniqueness, track-only identities, accepted verification
  states, positive decision versions, and normalized hash length.
- `Core/Providers` contains the current typed built-in adapters. Provider support is capability-specific and
  reported by `CurrentProviderSupportCatalog`; a provider may be supported for playlists or metadata while a
  different lane remains partial, blocked, or unavailable. `apple-download` and `apple-musickit` are separate.

## Protocol Adapters And Compatibility

The protocol layer keeps one selected surface while placing client-visible shaping behind tested adapters.

- `ProtocolExecutionContextFilter` runs after native backend authentication and provides one secret-free request
  context. Linked identities can authorize optional user work. Unresolved identities retain transparent relay
  behavior but cannot authorize provider-account, favorite, playlist, or scrobble side effects from route IDs.
- Jellyfin adapters own merged search paging, external item and conditional image responses, lyrics,
  favorite/InstantMix shaping, streaming/range headers, and raw catch-all relay behavior. The catch-all preserves
  allowed GET, POST, PUT, PATCH, DELETE, and HEAD requests without assuming JSON.
- Subsonic adapters own independent `search3` windows, structured OpenSubsonic lyrics, relay status/header/body
  shaping, and shared streaming semantics. GET and form POST parameter source, repetition, order, and exact
  `.view` routes remain intact.
- Real-host fixtures cover every support-matrix row. The matrix test rejects missing or invalid fixture files,
  so a named coverage claim cannot remain a placeholder.

Provider-neutral playlist materialization and durable favorite work now build on those authenticated boundaries.
Durable playback/scrobble work and scoped recommendation policy now build on those authenticated boundaries.

## Library Index, Matching, And Playlists

- `Core/Playlists` owns provider-neutral links, ordered source snapshots, schedules, runs, conflicts, target
  membership, and metadata/artwork references.
- `Core/Matching` and the library index connect one canonical recording to many provider identities and local
  copies. Decisions are scoped, versioned, explainable, and reviewable; manual overrides are durable.
- Spotify and Apple MusicKit sources use the selected encrypted provider account. Jellyfin and
  Subsonic/Navidrome targets support virtual reads, reconcile, and explicit recreate behavior.
- Materialization reuses exact local matches, preserves order, and reports unmatched entries. It does not
  download missing songs unless a separate opt-in policy starts a download workflow.

## Extension SDK v1 And First-Party Packages

- SDK v1 exposes typed metadata, streaming, download, playlist, lyrics, and health hooks through the same
  registry and router used by built-ins.
- Package installation verifies source and content hashes, archive bounds, manifests, compatibility, permissions,
  account scope, staged activation, disable/update, and rollback.
- The checksum-locked first-party bundle is mounted by AIO. Blocked packages are not activated merely because
  their archives are present.
- The current Jint runtime is a constrained trusted-code boundary. Stronger process isolation remains future work.

## Favorites, Managed Files, And Enrichment

- Favorite actions are opt-in at an exact tenant, user, protocol, backend, and optional library scope. Admins can
  define a tenant/backend policy. Users can save their own override in Hybrid or UserManaged mode. User values
  inherit only from the matching tenant policy and configured defaults.
- A successful backend favorite writes an idempotent event, policy snapshot, ordered action records, durable job,
  and visible failure state. The implemented chain covers virtual liked state, local matching, provider download
  artifacts, managed placement, MusicBrainz/provider enrichment plans, and Jellyfin or Subsonic library refresh.
- Provider download artifacts and managed-file records hold checksums, lengths, scope, lifecycle, and job lineage.
  Audio bytes stay in accessible filesystem roots. PostgreSQL stores control data and paths only.
- `FilePlacementService` stages and verifies an owned output, rejects traversal and symlink escapes, and uses a
  native reflink or verified-copy decision. Hardlinks remain disabled until immutability is a durable lease. Managed outputs retain filesystem identity where the OS
  supports it, and each consumer owns an idempotent durable reference with explicit release semantics. Metadata
  plans can be applied only to an Allstarr-managed artifact, never to a source-library file or a hardlink that would
  rewrite its inode. Managed tag changes stage beside the file and atomically replace it, then advance the ownership
  checksum and revision. MusicBrainz IDs use Picard-compatible native tag fields; path templates are rendered before
  backend refresh, and enrichment never silently renames an already indexed file.
- Unfavorite updates logical favorite state and may cancel work that has not started. It does not remove source
  media or a managed copy. Managed-file removal is a separate confirmed action with ownership and reference checks.
- Jellyfin refresh uses the configured server API key. Subsonic/Navidrome refresh resolves an encrypted,
  tenant-scoped credential just in time and keeps it out of job payloads, state-transfer archives, and logs.

## Intelligence And Durable Playback Signals

- `Core/Intelligence` owns exact-scope opt-in policy, retention, listening profiles, recommendation providers,
  durable runs, explanations, generated sets, and backend materialization. Turning a scope off stops new signals;
  the purge action removes its retained intelligence data.
- Playback and scrobble observations use durable, idempotent jobs rather than controller background tasks. They
  preserve backend response semantics and never let a route or payload user ID select another user's history.
- Habit-derived seeds feed Jellyfin InstantMix, exact-account Last.fm `track.getSimilar`, ListenBrainz
  collaborative filtering, local rules, MusicBrainz-enriched local relationships, and optional healthy
  AudioMuse-AI. Readiness is scoped and visible before selection.
- Generated sets reconcile exact local matches into Jellyfin or Subsonic/Navidrome through the shared playlist targets.
  Unmatched entries stay explained and are not downloaded. Subsonic credentials are explicitly saved on policy,
  snapshotted through run/set lineage, resolved just in time, and never borrowed from another user or playlist link.
- Durable recommendation schedules link to the exact intelligence policy and derive each occurrence from current
  retained habits. The generated-set job keeps schedule lineage and reconciles the same backend playlist on later
  occurrences. Policy disable or purge disables future occurrences while completed runs keep valid provenance.

## First-Party Package Boundaries

First-party Deezer, Spotify, and Apple MusicKit package sources live under `first-party` with deterministic
archives and source locks. A package replaces a built-in only after parity, permission, activation, and rollback
gates pass. The bundle lock keeps incomplete switchovers blocked.

## Project Map

### Host and Routing

- `allstarr/Program.cs`: Kestrel listeners, forwarded headers, conditional controller registration, DI, hosted services, middleware order.
- `allstarr/Middleware`: proxy websocket support, admin surface isolation, request logging, bot-probe blocking, global exception handling.
- `allstarr/Filters`: admin port gating and backend-verified Jellyfin controller authentication.
- `allstarr/Core/Storage`: mandatory PostgreSQL runtime, EF migrations, migration locking, backup/restore,
  PostgreSQL state transfer, readiness, and offline storage commands.
- `allstarr/Core/Identity`: backend identity bootstrap/resolution and provider-account scope policy.
- `allstarr/Core/Secrets`: AES-GCM secret versions and the external key-ring provider.
- `allstarr/Core/Jobs`: durable queue, worker, leases, attempts, outbox dispatch, retry, cancellation, and sidecar deferral policy.
- `allstarr/Core/Health`: durable provider-account/capability samples, rollups, retention, and circuits.
- `allstarr/Core/Operations`: liveness/readiness, correlation, sidecar status, redacted metrics, diagnostics, and logging.
- `allstarr/Core/Capabilities`: typed provider contracts, descriptors, outcomes, and the atomic provider registry.
- `allstarr/Core/Routing`: route plans, policy/account/health/sidecar gates, and typed fallback classification.
- `allstarr/Core/Matching`: canonical recording creation, exact provider identity links, and safe translation.
- `allstarr/Core/Providers`: built-in descriptors and typed adapters over current provider implementations.
- `allstarr/Core/Protocols`: the verified protocol execution context plus Jellyfin and Subsonic response-shaping adapters.
- `allstarr/Core/Playback`: durable, scoped, idempotent playback observations and optional scrobble delivery.
- `allstarr/Core/Intelligence`: opt-in policy, retained signals, habit profiles, recommendation sources and runs,
  explanations, generated sets, and Jellyfin or Subsonic materialization.

### Controllers

- `JellyfinController*.cs`: Jellyfin-compatible proxy, search merge, external streaming, lyrics, playback reporting, provider-neutral playlist reads and writes, and catch-all relay.
- `SubSonicController.cs`: Subsonic-compatible proxy, search merge, external streaming, playlist reads and writes, playback observations, and catch-all relay.
- `AdminAuthController.cs`: Jellyfin-backed login for the admin Web UI.
- `ConfigController.cs`: `.env`-backed configuration read and write.
- `DiagnosticsController.cs`: status, cache, sessions, memory, endpoint diagnostics.
- `PlaylistController.cs`: playlist summary, track views, rebuilds, mappings, external search.
- `JellyfinAdminController.cs`: Jellyfin users, libraries, playlists, link and unlink actions.
- `SpotifyAdminController.cs`: Spotify user playlists, session cookies, sync, match, global mappings.
- `LyricsController.cs`: manual lyrics mappings and lyrics diagnostics.
- `ScrobblingAdminController.cs`: Last.fm and ListenBrainz configuration and tests.
- `DownloadsController.cs` and `DownloadActivityController.cs`: kept-file management and live download activity.
- `ProxyResponseResultFactory.cs`: shared upstream JSON/empty response status preservation.
- `ProviderAccountsController.cs`: tenant-scoped provider-account creation, replacement, listing, and deletion.
- `JobsController.cs`: owner-filtered durable job inspection and cancellation, with an admin-wide view.
- `StorageController.cs`: authenticated backup creation and storage status for the admin UI.

### Services

- `Services/Jellyfin` and `Services/Subsonic`: backend-specific proxy, mapping, response shaping, and session logic.
- `Services/Spotify`: direct Spotify playlist fetch, cookie resolution, playlist matching, mapping persistence, validation, and admin helpers.
- `Services/Lyrics`: Spotify lyrics sidecar, LyricsPlus, LRCLib, orchestrator, optional prefetch.
- `Services/Scrobbling`: orchestrator plus Last.fm and ListenBrainz implementations.
- `Services/Deezer`, `Services/Qobuz`, `Services/SquidWTF`: provider metadata and download services.
- `Services/Common`: cache, paths, retries, ID helpers, outbound request safety, fuzzy matching, provider enrichment, the current provider support/runtime-status projections, backend-neutral playback activity contracts, extension containment, and maintenance tasks.
- `Services/Admin`: `.env` helpers, admin session storage, playlist status helpers.
- `Services/Validation`: startup validators and startup validation orchestration.

### Models

- `Models/Domain`: provider-neutral `Song`, `Album`, and `Artist`.
- `Models/Settings`: runtime configuration objects loaded from config and `.env`.
- `Models/Spotify`: playlist tracks, missing tracks, global track mappings.
- `Models/Scrobbling`: playback sessions and scrobble payloads.
- `Models/Admin`: admin request and response shapes.
- `Models/Subsonic`, `Models/Lyrics`, `Models/Download`, `Models/Search`: backend or feature-specific payloads.
- `Core/Storage/DurableEntities.cs`: provider-neutral durable records for identity, accounts, secrets,
  jobs, health, circuits, backups, and audit state.

### Frontend and Tests

- `wwwroot`: static Lit admin UI. Application behavior is consolidated in `wwwroot/js/webui.js`; vendored Lit
  remains separate.
- `allstarr.Tests`: xUnit regression coverage for helpers, middleware, controllers, provider services,
  real-host composition, protocol source locks/fixtures, JavaScript syntax, responsive/UI contracts, path
  safety, redaction, and policy logic.

## Request Pipeline Order

`Program.cs` wires the pipeline in a deliberate order:

1. `UseForwardedHeaders`
2. `BotProbeBlockMiddleware`
3. `RequestLoggingMiddleware`
4. `UseExceptionHandler`
5. `CorrelationMiddleware`
6. `DurableMutationGuardMiddleware`
7. `UseResponseCompression`
8. `UseWebSockets`
9. `WebSocketProxyMiddleware`
10. `UseHttpsRedirection`
11. `AdminNetworkAllowlistMiddleware`
12. `AdminStaticFilesMiddleware`
13. `AdminAuthenticationMiddleware`
14. `UseAuthorization`
15. `UseCors`
16. `MapControllers`

Do not reorder the admin middleware or conditional backend registration casually. Those are structural constraints, not style choices.

## Background Services

The host also runs several important maintenance and precomputation services:

- `StartupValidationOrchestrator`
- `CacheCleanupService`
- `LegacyMappingImportService`
- `SpotifyPlaylistFetcher`
- `SpotifyMissingTracksFetcher`
- `SpotifyTrackMatchingService`

These services are part of the current steady-state architecture. Changes to playlist flow, cache contracts,
or startup behavior usually require looking at both controller code and a hosted service. Pre-overhaul
environment, favorite, mapping, and version-state migration services are not registered. The overhaul starts
from a fresh durable baseline instead of importing those formats during startup.

The durable host registers `DurableStorageInitializer`, `DurableStorageRuntimeMonitor`, `IdentityBootstrapper`, `DurableJobWorker`,
`DurableOutboxDispatcher`, `DurableProviderHealthInitializer`, and `SidecarHealthMonitor`. Storage initialization
runs before durable mutations can be admitted. Job and outbox workers recover pending records from the selected
database, not from Valkey. The runtime monitor pauses mutations and workers if the selected database disappears
or its schema changes, then restores readiness after the exact database returns. Optional sidecar failures are
recorded per capability and can defer dependent jobs without stopping unrelated workers or the host.

Backend startup validation remains registered for the selected backend. Optional Deezer, Qobuz,
SquidWTF, and lyrics startup probes are registered only when
`StartupValidation:ProbeOptionalProviders=true`; the default is `false`. The `Testing` environment does
not run the validation orchestrator or live SquidWTF discovery. Automated tests must use fake HTTP and
temporary state, not real provider accounts.

Enabled durable provider accounts are different from deployment-global compatibility settings. After the
host becomes available, a background warmup probes every testable configured capability for each enabled
managed account and records the account-scoped result. It never delays startup, never borrows another
account's secret, and is not registered in the `Testing` environment. Saving a credential in the WebUI also
runs its primary account probe immediately and refreshes the visible status.

## Shared Data Contracts

### External IDs

Typed external IDs are the normal format:

- Songs: `ext-{provider}-song-{id}`
- Albums: `ext-{provider}-album-{id}`
- Artists: `ext-{provider}-artist-{id}`

Legacy `ext-{provider}-{id}` IDs are still accepted and treated as songs for backwards compatibility.

### Playlist Configuration

New playlist links are provider-neutral durable records. A link selects one explicit provider account and source
playlist, one owner and library scope, and either the Jellyfin or Subsonic/Navidrome backend family. It also stores
virtual, materialized, or hybrid mode; reconcile or recreate behavior; metadata controls; and an optional durable
schedule. Source snapshots, matches, target membership, conflicts, and per-entry outcomes remain reviewable.

`SPOTIFY_IMPORT_PLAYLISTS` remains a legacy compatibility input for the old injected-playlist routes only. The
fresh-install baseline does not import it into durable links, and no new playlist or matching action treats it as
the source of truth.

### Runtime State

Current state is split by ownership:

- PostgreSQL stores control-plane state: tenants, users, backend identities,
  provider accounts, encrypted secret versions, durable jobs/outbox, provider health/circuits, canonical
  recordings, exact provider track identities, library-track paths and metadata, immutable provider snapshots,
  match decisions and overrides, playlist links/runs/membership, audit records, and backup metadata.
  It also stores favorite policies/events/actions/state, provider download artifact facts, managed-file ownership,
  enrichment plans/applications, and their durable job lineage.
- The secret key-ring file stays outside the database and its backups. Losing it makes encrypted provider
  secrets unrecoverable even when the database is intact.
- `.env` still holds deployment and current compatibility settings. New provider-account secrets are saved by
  reference in durable storage instead of being returned through configuration APIs. Startup does not scan
  old environment, mapping, favorite, or version-state files and convert them into the new baseline.
- Valkey/Redis and `/app/cache` remain cache and compatibility inputs. Neither is a durable record of a
  job, outbox action, account, or health rollup.
- `downloads/*`, kept media, and configured library mounts contain the actual files. PostgreSQL never
  contain encoded song bytes.

## Architectural Guardrails

- Keep admin routes under `/api/admin`.
- Keep backend-specific catch-all behavior in the backend controllers, not in generic middleware.
- Keep exactly one protocol controller surface per deployment until a separately approved host design exists.
- Keep provider response shaping out of protocol adapters and protocol response shaping out of providers as seams are extracted.
- Keep configuration state separate from observed provider health; do not report `Unknown` as ready.
- Keep request, proxy-error, sidecar, and diagnostics logging redacted. Do not add an unsafe logging opt-out.
- Keep the selected database explicit and fail readiness instead of substituting another provider.
- Keep secret key material outside database rows, backup artifacts, metrics, and logs.
- Keep job failure retries separate from bounded sidecar deferrals, and keep both states operator-visible.
- Keep remote extension install disabled by default. Enabling staging never bypasses checksum, permission,
  content, lifecycle, or account-scope checks. Do not describe in-process Jint as an operating-system sandbox.
- Never make unfavorite/unstar an implicit file-deletion operation.
- Do not let feature docs drift from code. Update steering when architecture changes.
