# Startup And Configuration

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## `Program.cs` Owns The Host Contract

`allstarr/Program.cs` is not just bootstrapping. It owns several core policies:

- SquidWTF endpoint discovery before the host is built
- Forwarded-header trust rules
- Kestrel listeners on `8080` and `5275`
- Conditional controller registration by backend type
- DI ordering for providers and hosted services
- Durable schema migration and warm-state initialization
- Middleware order

Changes here can affect every subsystem.

## Important Startup Flows

### Backend Selection

`Backend:Type` decides which proxy controller set is registered:

- `JellyfinController`
- `SubsonicController`

All admin controllers remain registered either way.

### Provider Registration Order

For `IMusicMetadataService` and `IDownloadService`, registration order matters.

- ASP.NET Core DI injects the last registered implementation by default.
- Deezer or Qobuz secondary-provider support for playlists relies on this ordering.
- `ParallelMetadataService` races all registered metadata providers.

### Runtime State Initialization

Before steady-state serving starts, the app:

- initializes PostgreSQL and applies checked-in schema migrations under the database advisory lock
- bootstraps the configured tenant and backend identity policy
- initializes `CacheExtensions`

These are part of boot correctness, not optional helpers. Startup does not import pre-overhaul `.env`, favorite,
Spotify mapping, Redis, cache, or version-state formats. The overhaul baseline is a fresh install. After the new
database and identity are ready, an administrator can explicitly open the WebUI migration wizard for a reviewed
subset of legacy configuration.

PostgreSQL is the only application and offline storage-command target. Legacy database files are not opened by the application.

The WebUI importer follows the exact classification, scope, redaction, conflict, and transaction contract in
[Legacy `.env` Import Contract](../operations/legacy-env-import.md). In particular, deployment keys remain in
Compose, global provider credentials become disabled encrypted accounts only with explicit acknowledgement,
and user-owned credentials and playlist links remain manual. WebUI-owned non-secret preferences use the typed,
tenant-scoped, revisioned `tenant_runtime_settings` model; deployment values and secrets are rejected by its
allowlist.

## Config Parsing And Ownership

### Settings Binding

Settings bind from configuration sections, then some are post-processed in `Program.cs`:

- `SpotifyImportSettings`
- `SpotifyApiSettings`
- `ScrobblingSettings`
- `MusicBrainzSettings`
- `Cors`

`SpotifyImportSettings` is especially custom. It supports:

- the canonical `SpotifyImport:Playlists` JSON array format

Pre-overhaul split playlist environment variables are not translated on startup.

### Provider Account Management

`ProviderAccounts:ManagementMode`, or `ALLSTARR_PROVIDER_ACCOUNT_MANAGEMENT_MODE` in the standard deployment,
controls who may use the provider-account admin API:

- `AdminManaged`: only an administrator manages provider accounts, including global, library, and user scopes.
- `UserManaged`: every signed-in user, including an administrator acting as a user, manages only their own
  user-scoped accounts.
- `Hybrid`: administrators manage every scope and linked users manage only their own user-scoped accounts.

The default is `Hybrid`. An invalid value fails startup instead of silently selecting another mode. This setting
controls account management only. Provider selection still goes through tenant, owner, library, capability, and
provider policy checks.

### Configuration Ownership

Deployment values such as listeners, backend selection, database connection, key-ring location, and mounted paths
remain in Compose, secret files, or `.env` and normally require a container recreation. WebUI-owned non-secret
preferences use the typed tenant-scoped durable runtime settings store. Provider-account credentials use encrypted
secret references.

`ConfigController` and `AdminHelperService` still read and write `.env` for explicitly retained compatibility
settings, including the legacy Spotify playlist and user-cookie flows. Do not send new provider-neutral settings,
playlist links, account secrets, or job state through that compatibility path.

### Provider Workload Bounds

Provider discovery and metadata search are bounded independently from per-provider HTTP timeouts. The
`Providers:MetadataFanoutConcurrency` setting defaults to `4` and is clamped to `1`-`16`. It is one shared gate
across built-in metadata providers and active extensions, so concurrent requests cannot each consume the full
limit. Set `Providers__MetadataFanoutConcurrency` in Compose or the environment to override it.

Endpoint health checks, startup benchmarks, and endpoint-backed parallel work use a fixed maximum of eight active
endpoint operations. Every configured endpoint remains eligible for fallback; the cap limits simultaneous network
pressure rather than truncating the endpoint list.

## Hosted Services

Startup and long-running services currently include:

- `StartupValidationOrchestrator`
- `CacheCleanupService`
- `LegacyMappingImportService`
- `SpotifyPlaylistFetcher`
- `SpotifyMissingTracksFetcher`
- `SpotifyTrackMatchingService`
- `DurableStorageInitializer`
- `IdentityBootstrapper`
- `DurableJobWorker`
- `DurableOutboxDispatcher`
- `DurableProviderHealthInitializer`
- `SidecarHealthMonitor`
- `AuditEventRetentionService`

When changing a feature that has a hosted service, inspect both the controller path and the background-service path.

### Event Log Retention

`AuditEventRetentionService` bounds the durable operator event log by both age and total row count. It removes
unreferenced records in fixed-size oldest-first batches and never deletes audit events referenced by a legacy import
record. Cleanup runs once when the worker starts and then at the configured interval; failures are logged and retried
without failing the application host.

The settings below use normal ASP.NET configuration binding, so Compose and environment variables replace `:` with
`__` (for example, `Operations__EventLog__RetentionDays`):

| Key | Default | Allowed range | Purpose |
| --- | ---: | ---: | --- |
| `Operations:EventLog:RetentionDays` | `30` | `1`-`3650` | Maximum age for unreferenced events. |
| `Operations:EventLog:MaximumRows` | `250000` | `1000`-`5000000` | Hard global row ceiling after age cleanup. |
| `Operations:EventLog:CleanupBatchSize` | `1000` | `100`-`10000` | Maximum records removed per database transaction. |
| `Operations:EventLog:CleanupIntervalMinutes` | `360` | `5`-`10080` | Delay between cleanup cycles. |

These controls bound storage; they do not turn the audit table into request tracing. High-volume producers should
still summarize or sample repetitive success events and always retain failures and state transitions.

## Editing Guardrails

- Keep `Program.cs` changes deliberate and minimal.
- Preserve legacy Spotify playlist parsing and user-scoped cookie settings until those compatibility routes are
  deliberately retired.
- Do not add an automatic pre-overhaul import path. Import remains an explicit, reviewed WebUI action conforming
  to [the legacy environment import contract](../operations/legacy-env-import.md).
- Reuse `AdminHelperService` for `.env` work.
- If a change affects startup timing, DI order, or middleware order, update steering and tests together.
