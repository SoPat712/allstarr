# Testing Expectations

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Test Suite Shape

`allstarr.Tests` is broad and feature-oriented. Important areas include:

- proxy helpers and backend response builders
- real-host composition for both selected protocol modes
- protocol source locks, support-matrix validation, and route/auth fixtures
- provider metadata and download services
- current provider support claims, configuration/health separation, and explicit probes
- Spotify mapping and validation flows
- lyrics and cache helpers
- admin auth, admin allowlist, and admin static file boundaries
- path safety, extension containment/default denial, managed-file safety, and outbound request safety
- request/proxy/sidecar secret redaction and upstream status preservation
- backend-neutral playback activity and bounded admin artwork resolution
- scrobbling orchestration
- JavaScript module syntax, responsive CSS, support-state, and keyboard-contract rules
- explicit Postgres/SQLite storage, migration locks, native PostgreSQL schema types, backup/restore, state
  transfer, and the offline storage command
- tenant identity, provider-account scope, AES-GCM secret versions, key rotation/revocation, and API redaction
- durable job/outbox idempotency, concurrent claims, lease recovery, retry, cancellation, owner visibility, and
  bounded sidecar deferrals
- durable provider health samples, 15-minute rollups, retention, circuits, readiness, sidecar degradation,
  structured log redaction, and aggregate metrics
- standard Compose topology, pinned service images, secret-file mounts, persistent volumes, and fresh-install
  retirement of the Redis-to-Valkey conversion overlay

## Historical Phase 0 Characterization Baseline

This section records the Phase 0 characterization baseline before the durable foundation and capability core
landed. The fixtures remain useful, but the old phase boundary is historical. A few bullets name the current
replacement tests so maintainers do not mistake compatibility coverage for current ownership. Current coverage
continues below.

- `HostCompositionTests` uses `WebApplicationFactory<Program>` to boot Jellyfin and Subsonic modes,
  verifies exactly one protocol controller, and activates every registered controller.
- `ProtocolSupportMatrixTests` validates the endpoint-level support inventory, pins the Jellyfin OpenAPI
  version/content hash, and asserts all six InstantMix paths.
- `ProtocolRouteFixtureTests` runs real host routes against fake upstream HTTP. Current Jellyfin fixtures
  include external item response shaping, placeholder image bytes, ETag/304 behavior, and local-first lyrics
  fallback and not-found behavior. Favorite backend results, unresolved-identity side-effect suppression,
  playback capabilities/progress responses, and all six pinned InstantMix route classes are also covered
  alongside the existing authentication and search coverage. Current fixtures
  cover login status/body preservation, the non-public `Users/Me` auth boundary, zero provider calls on
  failed auth, and forwarding only client credentials to verification. Subsonic fixtures cover missing
  and failed password/token/API-key authentication, successful API-key principal resolution, zero provider
  calls on failed auth, GET/form POST preservation, repeated ordered parameters, empty-search relay, and
  upstream status/body/content-type preservation.
- `ProxyResponseResultFactoryTests` and `JellyfinProxyServiceTests` cover upstream status preservation and
  sanitized failure logging. `RequestLoggingMiddlewareTests` guards unconditional query/header redaction
  while preserving repeated-key structure.
- `CurrentProviderSupportCatalogTests`, `ProviderStatusManagerTests`, and
  `WebUiResponsiveContractTests` guard truthful advertised capabilities, disabled lanes, SquidWTF
  quarantine, configuration-versus-observation state, account/capability probe isolation, visible support
  tokens, narrow layout, and keyboard-operable mobile navigation.
- At the Phase 0 checkpoint, `ExtensionManagerSecurityTests` covered default-deny remote installation without a network request,
  explicit trusted opt-in, safe local discovery, contained staging, and unsafe ID/lifecycle rejection. It
  did not provide checksum, permission, malicious-archive, or runtime-isolation coverage for SDK v1. Current SDK
  lifecycle tests cover the implemented package boundary; Jint still is not an operating-system sandbox.
- `FavoriteFileSafetyTests` prevents reintroduction of the implicit pending-deletion processor.
  `FavoriteActionPipelineTests`, `FavoriteActionPolicyTests`, `ProviderDownloadArtifactResolverTests`,
  `FilePlacementServiceTests`, and `MetadataEnrichmentTests` cover the durable replacement.
- `AppleMusicControllerTests`, `ScrobblingAdminControllerTests`, `DownloadActivityControllerTests`, and
  `JellyfinPlaybackMetadataResolverTests` cover sanitized Apple status/2FA/login responses, actionable
  scrobbling failures, backend-neutral activity, and bounded/cached Jellyfin artwork metadata.

Protocol fixtures live under `allstarr.Tests/Fixtures/Protocols` and are copied to test output. The
`protocol-source-lock.json` file owns the pinned Jellyfin OpenAPI hash/version and local reference commits;
`protocol-support-matrix.json` owns each row's current status, target, auth boundary, fixture name, test
location, and known gap. A row may name a fixture gap deliberately; do not turn that name into a claim of
coverage until the fixture and executable test exist.

The `Testing` environment is a no-live-traffic contract: it skips live SquidWTF discovery, does not run
the startup-validation orchestrator, uses temporary data-protection/admin/cache/extension paths in host
fixtures, removes hosted services where a route test does not need them, and replaces upstream HTTP with a
deterministic fake. Optional provider startup probes default off in normal configuration as well.

## Historical Phase 1 Durable Foundation Checkpoint

At the Phase 1 exit checkpoint, 1,002 .NET tests passed with no skips. The native PostgreSQL tests were
run with `ALLSTARR_TEST_POSTGRES` against PostgreSQL 18 and matching libpq 18 tools. They are guarded by that
environment variable so an ordinary developer run does not require a local PostgreSQL server. When the variable
is present, the tests exercise the real database and command-line backup tools rather than substituting SQLite.

The Phase 1 checkpoint coverage included:

- `DurableStorageTests` for explicit provider validation, SQLite migrations, concurrent SQLite migration
  locking, one-shot SQLite bootstrap consumption, missing-file protection, bounded runtime connectivity/schema
  checks and recovery, pending-schema readiness, additive rollback/reapply, generated native Postgres SQL, and
  the rule that unavailable Postgres never creates a SQLite fallback.
- `PostgresStorageIntegrationTests` for concurrent advisory-locked migrations, down-to-foundation/reapply,
  native `uuid`/`bytea` types, database-backed idempotent enqueue, custom-format `pg_dump` verification, and
  isolated `pg_restore` recovery.
- `DurableBackupServiceTests`, `DurableStateTransferServiceTests`, `StorageOperatorCommandTests`, and
  `StorageOperationsRunbookTests` for standalone SQLite backups, secret-safe Postgres process invocation,
  checksums, strict backup-manifest parsing, exact schema compatibility, restored-target verification,
  provider-neutral export/import, confirmation flags, bulk secret rotation, and checked-in operator commands.
- `PlatformIdentityTests`, `ProviderAccountManagementOptionsTests`, `ProviderAccountsControllerTests`,
  `JobsControllerTests`, and admin middleware tests for backend principal resolution, tenant boundaries,
  `AdminManaged`/`UserManaged`/`Hybrid` management, global/user/library account policy, orphan-secret cleanup,
  owner-filtered jobs, and admin-only cross-tenant views.
- `EncryptedSecretStoreTests` for AES-GCM storage, masked metadata, tenant access, replace, rotate, revoke, and
  key-ring failure behavior. Request, proxy, admin-session, diagnostics, structured logger, and metrics tests
  check that raw credentials, URLs, filesystem paths, exception text, and sensitive labels are not emitted.

- `DurableJobQueueTests` for same-transaction outbox creation, concurrent idempotency, payload secret rejection,
  canonical request fingerprints, independent same-tenant user scopes, one-worker leasing, expired-lease recovery,
  bounded failure retries, cancellation, outbox retry, and a separate bounded sidecar-deferral budget.
- `DurableProviderHealthStoreTests`, `OperationalObservabilityTests`, and `SidecarReadinessTests` for durable
  account/capability observations, rollups, retention, circuits, readiness, optional versus required sidecars,
  managed-account credential isolation, health-write failure recovery, and redacted aggregate telemetry.
- `DiagnosticsControllerTests` and `OutboundRequestGuardTests` for same-origin diagnostics, response bounds,
  redirect denial, private and special-use addresses, connect-time DNS validation, and DNS-rebinding rejection.
- `ComposeContractTests` for the standard Postgres and Valkey topology, secret files, durable/media volume
  separation, pinned images, development overlay, and the absence of the retired conversion file.

The then-current Python sidecar contract suite also passed with its pinned environment. That retired GAMDL lane
is not part of the current repository gate. Standard and development
Compose validation, the final runtime-image build, PostgreSQL 18.4 client check, and an isolated image backup and
restore also passed. This checkpoint completes the durable foundation. The later phase sections and current test
inventory describe matching, playlists, extensions, and favorite/placement work added after this checkpoint.

## Historical Phase 2 Capability And Track Identity Checkpoint

At the Phase 2 exit checkpoint, 1,089 .NET tests passed with no skips. The native PostgreSQL run covered the
`20260711141123_Phase2TrackIdentityFoundation` migration, rollback/reapply, native types, backup, restore, and
state transfer. The then-current Python sidecar suite and JavaScript syntax checks also passed. The warning-free Phase 2
gate image is `sha256:0c6186174461faa899f590737ee32f928382e4c2846b6f5579fa35d9856a2a61`; it contains
`pg_dump` 18.4, and both standard and development Compose configurations render cleanly.

The Phase 2 checkpoint coverage included:

- `ProviderExecutionContextTests`, `ProviderCapabilityContractTests`, and `ProviderRegistryTests` for immutable
  actor/account/library context, typed outcomes, protected stream leases, provider/resource provenance, required
  hooks, playlist account requirements, permissions, and atomic descriptor/implementation registration.
- `ProviderRouterTests` for provider allowlists, priority, enabled state, authoritative account scope and revision,
  health and open circuits, declared sidecars, quality, deadlines, managed-download permission and idempotency,
  exact verified fallback identity, allowed and denied failure classes, and redacted decision records.
- `TrackIdentityServiceTests` for many-provider links, exact translation, account-over-catalog precedence,
  idempotent relinking, conflict refusal, tenant isolation, same-tenant database constraints, normalized exact
  signals and hashes, collision refusal, ambiguous target state, storage outages, and manual pin protection.
- `DurableStateTransferServiceTests`, `DurableStorageTests`, and `PostgresStorageIntegrationTests` for exact new
  archive entries, FK-safe import, semantic tamper rejection, two-tenant roundtrip, provider-neutral migration
  parity, and native PostgreSQL identity columns.
- `DeezerMetadataCapabilityAdapterTests`, `BuiltInProviderDescriptorCatalogTests`, and `HostCompositionTests` for
  the first real built-in routed through the core, safe legacy result mapping, host registration, and truthful
  metadata, streaming, download, playlist, and health descriptors for current Deezer lanes. Other built-ins stay
  non-operational until they cross the typed boundary.

## What Should Get Tests

Add or update tests for changes that affect:

- route behavior or proxy compatibility
- admin auth or admin network isolation
- path handling or file deletion rules
- cache key or persistence formats
- Spotify mapping precedence
- lyrics source ordering
- scrobbling thresholds or dedupe behavior
- JavaScript module boundaries

Pure refactors do not always need new tests, but any behavior change, bug fix, contract change, migration rule, or newly documented requirement does. Start by identifying the current behavior test or fixture, update it for the intended outcome, and add a focused regression test when fixing a defect.

Tests are part of the human-debuggable design: name them for observable behavior, keep setup narrow, use deterministic fakes/fixtures, and make a failure point to the boundary that changed rather than an incidental implementation detail.

## Protocol, Identity, And Lifecycle Coverage

For capability-core migration work, test the current controller behavior first, then run the same core action through Jellyfin and Subsonic/OpenSubsonic fixtures where both protocols support it. The parity inventory in [references/protocols.md](references/protocols.md) is the checklist; do not mark a row complete from a unit test of only one adapter.

Required coverage for new or migrated behavior:

- Jellyfin route/status/response compatibility and Subsonic GET/POST plus XML/JSON compatibility, including repeated query parameters and pagination/order preservation.
- A resolved backend identity can use only its own user/library/provider-account scope; spoofed route/query user IDs, display names, or opaque tokens cannot select another account.
- An unresolved backend identity remains a transparent proxy request and cannot start user-owned favorite, placement, or playlist work.
- `TrackIdentityService` respects scoped manual overrides, records snapshot/reason versions, isolates private candidates, and leaves ambiguous matches unresolved below the policy threshold.
- Favorite/star events are idempotent across retries, preserve the original backend result, record account/policy context, and leave favorite state intact after a failed optional action or cancellation.
- Playlist snapshots preserve order and last-known-good stale state; virtual reads do not write the backend; materialized retries do not duplicate tracks and use source-revision/rule-version keys.
- Placement rejects traversal and symlink escapes, finalizes atomically, handles collision/durable references, captures filesystem identity where supported, and proves that a source-library inode is never shared with a mutable managed output. Native reflink tests accept a clean copy fallback on unsupported filesystems but never a partial clone.
- Download and placement jobs recover from interrupted staging work without deleting a completed file or relying on Redis as the only durable state.

Use fake providers, deterministic clock/queue fixtures, temporary roots, mocked HTTP, and protocol fixtures. No automated test should require a real provider account, a real backend library, or live provider traffic.

The bullets above are migration gates. Phase 0 through Phase 8 now have current coverage.
`ProtocolSupportMatrixTests` requires every claimed fixture to exist and parse, while `ProtocolRouteFixtureTests`
boots both selected hosts against fake upstream HTTP. Phase 4 adds fake account-bound Spotify and Apple MusicKit
sources, Jellyfin and Subsonic/Navidrome catalog scanners and targets, durable schedule/job translation, matching
and override persistence, virtual reads on both protocols, reconcile/recreate orchestration, artwork degradation,
tenant isolation, idempotency, conflict recording, and provider-neutral WebUI contracts. Phase 6 adds durable
favorite events/actions/policies, exact admin and user policy scope, provider download artifacts, restart-safe
artifact reuse, atomic managed placement, traversal/symlink/cross-volume coverage, source-inode protection,
MusicBrainz/provider merge plans, managed-only metadata application, Jellyfin and Subsonic/Navidrome refresh
fakes, failed refresh handling, explicit managed removal, and strict state-transfer round trips and tamper checks.

The Phase 6 tests also prove that audio remains on the filesystem, Redis is not the only record of a kept output,
unstar does not delete media, unlinked principals cannot start user work, a user override cannot infer another
user's policy, and Subsonic credentials do not enter durable payloads.

Phase 7 adds exact-scope intelligence policy, opt-in, retention, purge, listening profiles, weighted habit seeds,
provider readiness, durable recommendation runs, explanations, and controller/WebUI state coverage. Source tests
separate Jellyfin InstantMix, Last.fm `track.getSimilar`, ListenBrainz collaborative filtering, local rules,
MusicBrainz-enriched local relationships, and optional AudioMuse readiness instead of treating them as equivalent
personalization services.

Playback pipeline tests cover authenticated actor scope, durable and idempotent observations, duplicate delivery,
retry and cancellation, native protocol-result preservation, and the absence of controller fire-and-forget work.
Generated-set tests cover exact local matching, stable order, existing-member reuse, unmatched explanations,
Jellyfin and Subsonic target behavior, explicit credential selection, safe metadata degradation, recreation,
retry, cancellation, and the rule that playlist materialization does not download missing songs.

Phase 8 adds deterministic first-party package builds, content and archive hash verification, strict source locks,
permission-pending bootstrap, ordinary built-in collision rejection, checksum-approved replacement, built-in
fallback restoration, rollback locking, tamper rejection, and core/AIO bundle-plan coverage. Packages marked
blocked in the lock are never bootstrapped or presented as active.

## Current Test Conventions

- Most tests are focused xUnit unit tests with mocks or disabled Redis
- Security-sensitive helpers and middleware usually have direct regression coverage
- JavaScript syntax tests use `node --check`, so Node must be installed for that part of the suite
- Host/route fixtures use `Microsoft.AspNetCore.Mvc.Testing` with fake upstream HTTP and temporary state
- Durable storage tests use temporary SQLite databases by default. Native Postgres tests use
  `ALLSTARR_TEST_POSTGRES` and require matching `pg_dump`/`pg_restore` tools for backup coverage.
- Storage command tests assert machine-readable JSON output and confirmation gates without printing connection
  strings or passwords.
- Static WebUI contract tests are regression guards, not a substitute for functional browser, focus,
  screen-reader, responsive, and accessibility validation before release

## Useful Commands

```bash
dotnet test allstarr.sln
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter JavaScriptSyntaxTests
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter Spotify
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "HostCompositionTests|ProtocolRouteFixtureTests|ProtocolSupportMatrixTests"
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "ProviderStatusManagerTests|CurrentProviderSupportCatalogTests|ExtensionManagerSecurityTests"
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "DurableStorageTests|DurableBackupServiceTests|DurableStateTransferServiceTests|StorageOperatorCommandTests"
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "PlatformIdentityTests|ProviderAccountsControllerTests|EncryptedSecretStoreTests|JobsControllerTests"
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "DurableJobQueueTests|DurableProviderHealthStoreTests|OperationalObservabilityTests|SidecarReadinessTests"
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "PlaylistOrchestrationIntegrationTests|BackendPlaylistTargetTests|DurableScheduleEngineTests|VirtualPlaylistProtocolAdapterTests|BackendLibraryIndexingTests"
dotnet test allstarr.Tests/allstarr.Tests.csproj --filter "IntelligenceCoreTests|IntelligenceControllerTests|RecommendationProviderSourceTests|GeneratedSetMaterializerTests|PlaybackSignalPipelineTests"
ALLSTARR_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Database=allstarr_test;Username=allstarr;Password=...' \
  dotnet test allstarr.Tests/allstarr.Tests.csproj --filter PostgresStorageIntegrationTests
```

## Editing Guardrails

- If a helper or policy already has a test file, extend it instead of creating a scattered duplicate.
- Keep tests focused on behavior and contract, not internal implementation details.
- When you change a steering rule that is already enforced by tests, update the test too.
- When a roadmap or steering change introduces a new behavior requirement, add or update its automated coverage and the relevant protocol/provider fixture in the same implementation change.
- Name new tests after the observable boundary (protocol action, account scope, match decision, job outcome, or file-safety property) so future adapter migrations can reuse them as parity fixtures.
