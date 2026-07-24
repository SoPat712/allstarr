# Runtime And Compose

Use this file for Postgres, Valkey, Docker Compose, sidecar profiles, resource modes, and deployment documentation. The root plan is [OVERHAUL.md](../../../OVERHAUL.md).

## References

- [Postgres Docker image](https://hub.docker.com/_/postgres)
- [Npgsql EF Core](https://www.npgsql.org/efcore/)
- [Docker Compose profiles](https://docs.docker.com/compose/how-tos/profiles/)
- [Docker multiple compose files](https://docs.docker.com/compose/how-tos/multiple-compose-files/)
- [Docker Compose health checks](https://docs.docker.com/reference/compose-file/services/#healthcheck)
- [docs/steering/STARTUP-AND-CONFIG.md](../STARTUP-AND-CONFIG.md)
- [docs/steering/CACHING.md](../CACHING.md)

## Runtime Direction

Use:

- PostgreSQL for durable state in every supported runtime deployment.
- SQLite only as an offline source for controlled migration into PostgreSQL.
- Valkey or Redis for cache, queue acceleration, locks, probe state, and hot runtime data; it is never the only durable record of work.
- Sidecars only for provider runtimes that need them.
- Configured media roots or mounted folders for audio and other managed media. PostgreSQL stores paths, identities, checksums, metadata, and workflow state, never encoded song payloads.

For the fresh overhaul baseline, keep `.env` for deployment settings, initial secrets, and bootstrap values. Runtime settings and state created after setup belong in durable storage. The WebUI should mask secrets, avoid exposing raw API keys, and never return a stored secret after it has been saved.

Standard deployment is the default: core app, Postgres, and Valkey. Optional sidecars should be selectable, removable, and re-addable without breaking startup.

`allstarr.sh` persists an explicit `release` or `source` mode. Release mode uses reviewed images. Source mode adds
the development override and builds the checked-out commit. Both modes reuse the same saved optional profiles and
volumes. Private `.apple-provider` inputs are excluded from the core Docker context, and Apple preparation uses
owner-only staging permissions.

## Historical Phase 1 Checkpoint

This section preserves the Phase 1 exit evidence. It is historical, not the current release test inventory. The
Phase 1 durable foundation and its runtime-image/Compose exit gate completed with the following behavior:

- The application runtime accepts only `Postgres`. It owns the full EF model, migrations, readiness state,
  mutation guard, backups, and durable workers. A failed PostgreSQL connection never creates or opens SQLite.
  Offline storage commands may open an existing SQLite file for verification and export.
- Seven checked-in provider-neutral migrations create the foundation, durable health rollups, separate job
  failure/deferral policy, and the final operational fields. Startup serializes migration with a database-scoped
  PostgreSQL advisory lock. Offline SQLite verification uses an exclusive file lock.
- Native PostgreSQL 18 integration covers concurrent migrations, down-to-foundation/reapply, native `uuid` and
  `bytea` storage, idempotent durable work, verified `pg_dump`, and isolated `pg_restore`. SQLite coverage is
  limited to offline verification and one-way state transfer.
- `storage backup`, `storage restore-sqlite`, `storage restore-postgres`, `storage export`, and `storage import`
  run without starting the HTTP host or background workers. Destructive or quiesced operations require explicit
  confirmation flags, checksums are verified, output is JSON, and Postgres passwords use environment variables
  instead of command arguments.
- Provider secrets are encrypted with AES-GCM and versioned by key ID. The key-ring file is mounted separately
  and is excluded from database backup artifacts. Logs, metrics, traces, health output, and command failures do
  not return secret values, media URLs, filesystem paths, raw exceptions, or SQL text.
- Jobs, attempts, leases, retry/cancellation state, sidecar deferrals, and outbox messages are durable database
  records. Idempotency binds the canonical request and saved execution policy inside the initiating tenant/user
  scope. Valkey can accelerate current compatibility/cache paths, but it is not required to reconstruct this
  work after a restart.
- Provider health samples, 15-minute rollups, retention, and circuit state are durable and keyed by provider
  account plus capability. Liveness, readiness, optional sidecar state, aggregate health metrics, and recovery
  behavior have focused coverage. Managed probes use only the selected encrypted account credential, and
  transient health-store failures do not stop the sidecar monitor.
- The checked-in standard Compose file runs the app with pinned PostgreSQL 18 and Valkey images, mounted secret
  files, named database/app/cache volumes, and separate bind-mounted download and kept-media folders. The
  development override builds the local app image. The old Redis-to-Valkey conversion overlay is gone.

At that checkpoint, 1,002 .NET tests passed with no skips, including the native PostgreSQL run with
`ALLSTARR_TEST_POSTGRES`. The then-current Python sidecar contract suite also passed; that retired suite is not
part of the current repository gate.
Standard and development Compose render cleanly. Runtime image
`sha256:c6b659ed0028fc4347ac32ab1e5fc0505f2f742bc1ed906be17e68294b287e43` contains `pg_dump` 18.4 and
completed an isolated verified backup and restore through `20260711001832_Phase1OperationalCompletion`.
That image and migration are the preserved Phase 1 runtime checkpoint. The next recorded checkpoint migrated
through `20260711141123_Phase2TrackIdentityFoundation`; later checked-in migrations define the current schema.
Later phase and release records in `OVERHAUL.md` supersede this image as current release evidence.

## Explicit Profile And Storage Selection

The deployment profile and durable database provider are operator choices made before the first start. They must be represented in the deployment configuration, shown as non-secret runtime status, and remain stable across restarts. A resource profile changes service topology and resource limits; it must not silently change database semantics.

| Concern | Standard/AIO deployment | Manual or small deployment | Rule |
| --- | --- | --- | --- |
| Durable database | PostgreSQL on an explicit persistent volume or external managed PostgreSQL | External or local PostgreSQL | SQLite is not a runtime option. |
| Cache and acceleration | Valkey is included | Valkey may be omitted when the selected profile permits it | Cache loss may reduce performance, but must not lose committed data or jobs. |
| App state and media | Explicit app-state and media/library volumes or bind mounts | The same, sized for the installation | Do not rely on anonymous or container-layer storage for user data. |
| Sidecars | Only selected sidecar services are included | None unless deliberately selected | A sidecar is capability-scoped, not a global dependency. |

An existing SQLite deployment can move one way into PostgreSQL through the offline export/import procedure. It is not a failover mechanism.

## Fresh-Install Baseline

The overhaul release is a fresh install for deployments created on the pre-overhaul layout. Users set up the new Compose stack, database, secret keyring, provider accounts, and runtime settings again. Allstarr does not promise an in-place import of legacy JSON, Redis, cache, mapping, extension, or job state.

- The old Redis-to-Valkey conversion Compose file is retired. Fresh installs use the standard named Valkey volume and do not run an in-place cache conversion.
- Do not convert or reuse a legacy Redis volume as the new Valkey source of truth. Valkey remains disposable acceleration state.
- Keep existing music folders intact. Reattach them through documented bind mounts or volumes, then let the new library index scan them. A fresh application setup never means deleting or copying song payloads into Postgres.
- Create new durable database and app-state volumes unless a later, versioned overhaul-to-overhaul migration explicitly says otherwise.
- Keep backup, restore, schema migration, and storage-provider migration support for releases built on this new durable baseline. Fresh install removes legacy compatibility work; it does not remove future operational safety.

Setup documentation must say what users keep, what they recreate, and what is intentionally not migrated. Do not leave an obsolete conversion file or command in the repository as an implied supported path.

### No Implicit Database Failover

If PostgreSQL is unavailable, the app must not create or open SQLite. It remains unready, rejects state-changing
work, and surfaces an actionable database-health error until the same PostgreSQL service recovers.

This protects library state, account ownership, jobs, and audit history from split-brain deployments. A deliberate emergency change requires a backup/restore or import procedure and a recorded configuration change; it must never happen merely because a connection attempt failed.

## Compose Files

Checked-in deployment files:

- `docker-compose.yml`: standard core app, PostgreSQL 18, and Valkey deployment.
- `docker-compose.dev.yml`: local app build override on top of the standard deployment.
- `docker-compose.aio.yml`: verified offline first-party package bundle, without provider sidecars.

Possible later repository profiles remain separate work:

- `docker-compose.lyrics.yml`: lyrics sidecars only.
- `docker-compose.lowram.yml`: lower memory settings and fewer services.

`docker-compose.apple.yml` is an explicit optional profile. It builds the repository gateway and the locked
wrapper-v2 source only after the operator supplies hash-verified legal Apple libraries. Standard and AIO remain
complete without it, and removing the profile preserves Postgres, media, gateway state, and wrapper login state.
Terminal generic gateway jobs are written atomically beneath the gateway data volume and rehydrated after restart.
Nonterminal or malformed records are ignored rather than exposed as successful work. Multi-artifact manifests and
host ingestion are still required before broader GAMDL feature claims can be advertised.

Compose-file selection and any Compose profiles are explicit operator inputs. Deployment documentation should show the selected files/profile, database choice, persistent volume locations, and the rendered `docker compose config` output before an upgrade. Do not infer AIO or low-RAM mode from available memory, and do not hide a storage-provider change inside an override file.

Current commands:

```bash
# standard default
docker compose up -d

# development
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build

# verified first-party package bundle
docker compose -f docker-compose.yml -f docker-compose.aio.yml up -d
```

The following commands apply after their later profile files are implemented:

```bash

# selected repository sidecars
docker compose -f docker-compose.yml -f docker-compose.lyrics.yml up -d

# lower-resource deployment; choose and persist its database provider first
docker compose -f docker-compose.yml -f docker-compose.lowram.yml up -d
```

Every maintained Compose configuration should declare named volumes or documented bind mounts for:

- the selected database data;
- durable app state, including extension/package state when applicable;
- the managed media/library root and any staging area that must survive restarts; and
- sidecar session state only when the sidecar requires it.

The database volume and media volumes are different concerns. Database backup protects control-plane state. Media backup protects the actual audio files in their accessible folders. Neither backup should pretend that song bytes live in the database.

Purgeable caches should be isolated from those volumes. A Valkey persistence volume can improve warm recovery, but it is not a replacement for the durable database volume.

## External Provider Services

Compose must distinguish Allstarr services from operator-owned provider services. `docker compose up --build`
rebuilds checked-out Allstarr source; it does not install, update, or verify an external provider gateway.

For every external provider service:

- The operator owns its deployment, persistent session state, upgrade procedure, and rollback image.
- Allstarr receives an explicit URL and verifies the gateway's API version, health, authentication state, and
  capability manifest before enabling routes.
- AIO must not imply that optional providers are installed.
- An external service update should be validated before its URL is switched in Allstarr. Removing an endpoint
  degrades only that provider's capabilities.
- GAMDL and wrapper-v2 are one compatibility pair behind an Apple provider gateway. Use GAMDL 3.8.2 or newer with
  wrapper-v2 0.0.2, or the pair required by the gateway's authoritative runtime manifest. wrapper-v2 alone is not
  the gateway API Allstarr expects.

## Durable Jobs And Transactional Outbox

The durable job and transactional-outbox contract owns downloads, favorites, file placement, provider refreshes,
probes, and extension work. The selected database is the source
of truth for jobs, attempts, cancellation, progress, and terminal results. State changes that require a
follow-up action write an outbox record in the same transaction as the state change.

- Workers claim jobs with an atomic lease, use idempotency keys, record attempts, and retry with bounded backoff.
- Outbox sink invocation is at-least-once; consumers and external effects must be idempotent. The built-in
  `DiagnosticOutboxSink` acknowledges events by recording only their type and message ID. It does not publish
  externally. In the outbox table, `Delivered` means the configured sink accepted the record, not that a
  webhook, event bus, or third-party integration received it. Replace `IOutboxSink` when real external delivery
  is configured.
- Valkey may accelerate dispatch, locking, and wakeups, but a Valkey restart must be recoverable by scanning durable pending jobs and outbox records.
- A job blocked by an unavailable sidecar or provider should be deferred or paused with a visible reason, not discarded or retried in a tight loop.
- Job ownership, account scope, and cancellation must be checked again by the worker, not trusted only from the request that enqueued it.
- Failure attempts and sidecar deferrals have separate bounded budgets. Repeated lease loss consumes the failure
  budget; waiting for a declared sidecar does not.

This keeps background work correct in low-RAM deployments without Valkey and through cache/sidecar restarts.

## Migrations, Backup, And Restore

The first overhaul install creates the fresh baseline schema; it does not import pre-overhaul runtime state. For every later schema change, only one migration runner may apply it for a deployment. It must acquire the appropriate database lock, record the target version, and prevent application instances from becoming ready until the schema is compatible. Routine forward-only migrations may run as part of controlled startup; destructive, long-running, or data-rewriting migrations need an explicit maintenance procedure.

Before a migration that can alter or remove durable data:

1. Run a compatibility and free-space preflight.
2. Create and verify a recoverable database backup.
3. Record the application version, schema version, selected storage provider, and affected persistent volumes.
4. Apply the migration, then run readiness and smoke checks before admitting normal traffic.
5. Retain the rollback artifact for the documented recovery window.

Rollback is normally a restore to a compatible application version, not an untested automatic down migration. A failed migration must leave the application unready and produce an actionable operator error rather than silently starting against a partial schema.

Backups must cover PostgreSQL and the durable media/app-state volumes needed to reconstruct the library.
Use a supported logical backup or point-in-time recovery plan. Valkey and rebuildable caches are not
authoritative. Secrets require separate encrypted, access-controlled handling.

Restore procedures must be exercised against an isolated environment and verify schema compatibility, library references, queued work recovery, and application readiness before a production cutover. Keep an operator runbook for backup, restore, storage-provider migration, and profile changes.

## Secret Handling

Use `.env` only for local/deployment bootstrap values with restrictive file permissions. Prefer Docker secrets or an external secret manager for production deployments. Provider tokens and other runtime secrets that move into durable storage must be encrypted at rest, access-controlled by account/role, and protected by an encryption key that is not stored in the database backup.

- The WebUI may accept a replacement secret and show masked metadata, but never read back the raw value.
- Logs, traces, metrics, support bundles, job payloads, and health responses must redact credentials, authorization headers, cookies, session files, and secret-bearing URLs.
- Rotate secrets through a controlled replace-and-validate flow; retain only what is necessary to complete a safe transition.
- Sidecars and extensions receive only the capability-scoped secret they need. They must not receive a global configuration dump or another provider's credentials.

## Resource Profiles

Low RAM:

- Core app only.
- PostgreSQL on persistent storage; never an automatic SQLite fallback.
- Valkey may be omitted when durable jobs and locking remain correct without it.
- Around 512 MB to 1 GB target.

Standard:

- Core app.
- Postgres.
- Valkey.
- Around 1 GB to 2 GB target.

AIO:

- Core app.
- Postgres.
- Valkey.
- Verified read-only first-party package bundle.
- Nearly the same resource target as Standard. Independent external provider services have their own budgets.

External Apple download provider:

- Runs in an operator-owned stack with its own resource, session, health, update, and rollback budget.
- Connects to Standard or AIO through a configured compatible gateway URL.
- Is not a raw wrapper-v2 URL and does not change Allstarr's database or media-volume profile.

Switching profiles does not move data, replace a database, or remove persistent volumes. Profile changes should be made as a planned deployment change with a configuration review and backup where the changed services own durable state.

## Sidecar Readiness And Degradation

All sidecars should be optional.

Docker health checks are necessary but not sufficient. For each selected sidecar, the core app should probe the required endpoint and validate the capability contract, configuration, authentication state, and compatible version before declaring that capability ready. `depends_on` ordering may wait for a selected service's health check, but the core app must not fail merely because an optional sidecar was omitted.

The WebUI should distinguish at least: Not Installed, Unreachable, Needs Configuration, Unauthorized, Degraded, and Ready. It should show the affected provider capabilities and the recovery action rather than treating a sidecar container as a generic global health signal.

If a sidecar is removed or becomes unhealthy:

- The app starts.
- Provider capability becomes disabled or degraded.
- WebUI shows missing sidecar status.
- Stored settings remain.
- Re-adding the sidecar restores capability after health passes.
- Dependent jobs are paused or rescheduled with bounded retries and a visible reason.
- Existing unrelated playback, library, and protocol operations continue when their own dependencies are healthy.

Optional services should make the app better, not mandatory. Users who skip sidecars should lose only the features that depend on those sidecars.

Probe failures should use timeouts, circuit breaking, and rate-limited rechecks so a failing sidecar cannot exhaust worker capacity or flood logs. A provider explicitly marked required by deployment policy may make readiness fail, but that policy must be visible and deliberate.

## Startup Rules

- Verify the selected database, persistent storage, and schema compatibility before the app reports ready.
- Missing optional provider config should not fail startup.
- Only the selected durable backend and explicitly required provider configuration should fail readiness.
- Only one provider per required capability should be needed.
- Remove the global primary music service requirement.
- Health warnings should be visible but not fatal unless deployment policy marks a provider required.
- A Valkey outage may degrade cache, dispatch, and probe performance; durable requests and jobs must recover from the selected database.

## Operational Observability

Expose separate liveness and readiness signals. Liveness answers whether the process can run; readiness answers whether the selected durable database, schema, required storage, and any policy-required capabilities are usable. Optional sidecars should appear as per-capability status rather than making all readiness checks fail.

Use structured, redacted logs and correlated traces for startup, migrations, job/outbox processing, provider calls, and sidecar probes. Track metrics and dashboards for:

- database connectivity, schema version, migration duration/failures, and storage free space;
- job and outbox depth, age of oldest pending item, lease expiration, retry/cancellation counts, and dead-letter/terminal failures;
- Valkey availability and cache/dispatch degradation;
- provider-account-capability health, sidecar probe latency/errors, circuit state, and recovery events; and
- backup success/age and restore-test status.

Metric labels and logs must use stable non-secret identifiers; never emit provider tokens, account names where unnecessary, URLs with embedded credentials, or raw request headers. Alerts should be actionable: distinguish an optional degraded capability from a database outage that blocks writes.

## Tests

Required test areas:

- compose config validation
- fresh-install validation that no legacy Redis-to-Valkey overlay or automatic legacy-state import remains
- existing media-root reattachment and re-indexing without writing audio blobs to PostgreSQL
- selected profile and storage-provider persistence across restarts
- Postgres migration, migration lock, failed-migration readiness behavior, and backup/restore
- offline SQLite verification/export; verify that runtime startup rejects SQLite
- controlled SQLite-to-PostgreSQL migration and PostgreSQL rollback/restore runbooks
- durable job and transactional-outbox recovery after process or Valkey restart
- startup with missing optional sidecars
- startup with disabled provider
- sidecar readiness validation beyond container health, including incompatible/auth-required sidecars
- health degradation when sidecar disappears
- health recovery when sidecar returns
- bounded retry and resume behavior for jobs blocked on a sidecar
- masked secret handling, redaction in diagnostics, and secret replacement/rotation
- liveness/readiness, redacted telemetry, and actionable degradation status
