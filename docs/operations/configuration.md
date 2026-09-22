# Configuration

Allstarr separates deployment bootstrap, durable application settings, and encrypted credentials. Do not move a value between these owners merely to make it editable in the WebUI.

## Deployment-owned values

`.env` exists for values required before PostgreSQL and the administrator UI are available. `.env.example` is the checked-in source of truth.

| Group | Current values |
| --- | --- |
| Backend and release selection | `BACKEND_TYPE`, `ALLSTARR_RELEASE_PROFILE` |
| PostgreSQL bootstrap | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD_FILE` |
| Encryption bootstrap | `ALLSTARR_KEYRING_FILE` |
| Image and media mounts | `ALLSTARR_IMAGE`, `DOWNLOAD_PATH`, `KEPT_PATH`, `APPLE_UPLOAD_PATH` |
| Public listeners | `PROXY_BIND_ADDRESS`, `PROXY_PORT`, `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` |
| Admin network policy | `ADMIN_BIND_ANY_IP`, `ADMIN_TRUSTED_SUBNETS` |
| Extension install policy | `EXTENSIONS_ALLOW_REMOTE_INSTALL` |
| Browser origin policy | `CORS_ALLOWED_ORIGINS`, `CORS_ALLOW_CREDENTIALS` |
| Optional Spotify lyrics bootstrap | `SPOTIFY_API_SESSION_COOKIE` |
| Canonical metadata upstream | `MUSICBRAINZ_SOURCE_ID`, `MUSICBRAINZ_BASE_URL`, `MUSICBRAINZ_RATE_LIMIT_MS`, exceptional `MUSICBRAINZ_AUTHORIZED_USER_AGENT_OVERRIDE` |

The Compose file translates these values into ASP.NET configuration. Changing one requires recreating the affected container. Allstarr does not hot-edit its own Compose deployment.

PostgreSQL is mandatory. There is no SQLite, Redis, or Valkey runtime option.

## Protected files

`allstarr.sh init` creates the PostgreSQL password file and Allstarr key ring with private permissions. Back them up separately from the database.

- Losing the PostgreSQL password prevents database access.
- Losing the key ring prevents decryption of stored provider credentials.
- Rotating or replacing either file is an operator action, not a normal settings change.

## Durable settings

Non-secret product behavior belongs in tenant-scoped PostgreSQL settings and is edited through the dashboard surface that owns it. General playback, cache, matching, playlist, and diagnostics policy lives under **Settings**. Provider priority lives under **Integrations > Routing**.

`DurableRuntimeSettingsService` owns validation, typing, revisions, and optimistic concurrency. Controllers must not add a second environment or JSON owner for these settings.

Administrators can inspect the resolved, secret-free policy at `GET /api/admin/config/effective-provider-policy`. The response contains the tenant's capability orders, disabled providers, audio-quality target, local-preference window, account-scope counts, and selection rules. It deliberately omits account IDs, credentials, tokens, and provider payloads.

## Provider accounts

Provider credentials are encrypted and persisted as provider accounts with explicit tenant, user/shared scope, capability, and access policy. Services and their configuration are managed under **Integrations > Services**. Credentials and audience policy live under **Integrations > Accounts**; capability priority lives under **Integrations > Routing**.

`ProviderAccounts:ManagementMode` defaults to `Hybrid`: listeners manage their own private or self-shared global connections, and administrators may manage all connections. `UserManaged` removes cross-user administrator management; `AdminManaged` disables listener account mutations. In self-service modes, sharing never transfers control to other listeners. User/library reassignment remains an administrator operation. See the [account workflow](../user-guide.md#integrations).

`ProviderPolicy:AllowGlobalAccounts` controls whether routing may use global accounts (default `true`). `ProviderPolicy:AllowGlobalPersonalAccounts` defaults to `false`: sharing an account does not implicitly share its personal playlists, favorites, personal library, or scrobbling identity. Its creator retains personal access; administrators can explicitly select a global personal account under the existing policy. These are ASP.NET configuration keys, not new WebUI toggles or automatically added Compose variables. A permitted audience is not a guarantee of a provider's concurrent-use allowance.

Extensions are package implementations, not a second account system. Their install, update, permission, rollback, and removal lifecycle lives under **Integrations > Extensions**. Once active, their Services and Accounts use the same Integrations surfaces as built-in providers.

AudioMuse-AI is a built-in Intelligence integration rather than an extension. It is composed only when `ALLSTARR_RELEASE_PROFILE=development`; its persisted configuration and data remain intact while the core profile is active.

A shared account is not automatically available to every user. Administrators must set its access policy explicitly.

## Backend setup

`BACKEND_TYPE` selects Jellyfin or Subsonic/OpenSubsonic before startup. Backend URL, credentials, instance identity, library selection, and user mapping are completed through onboarding and durable configuration. An imported legacy file must not switch the active backend.

`ALLSTARR_RELEASE_PROFILE` defaults to `core`. The core profile omits unreleased Intelligence controllers, recommendation providers, imports, background handlers, and direct WebUI access while preserving their database records. `development` explicitly composes those surfaces for continued work. Unknown profile names stop startup instead of silently widening the release.

## Canonical metadata upstream

Allstarr's catalog client speaks the read-only MusicBrainz `/ws/2` contract. The public MusicBrainz service remains the bootstrap default. After the BrainzMash operator approves Allstarr's user agent, select the pool without changing matching or catalog ownership:

```dotenv
MUSICBRAINZ_SOURCE_ID=brainzmash
MUSICBRAINZ_BASE_URL=https://api.brainzmash.cc/ws/2
MUSICBRAINZ_RATE_LIMIT_MS=1000
```

This is the MusicBrainz-compatible front door used by DroppedNeedle. Do not append `/ws/2` to `https://lidarrapi.brainzmash.cc`: that is the separate Lidarr/Aurral-shaped API with `/artist`, `/album`, and `/search/*` resources, and Allstarr deliberately does not implement that second dialect. Requests identify themselves with the version derived from `AppVersion.Version` and the Allstarr repository URL. Source IDs isolate cache entries and provenance, so changing the upstream cannot reuse another source's response. A 401 or 403 is an access/configuration failure, not a missing recording; do not retry it as catalog churn. Playback uses persisted routes and remains available when the metadata upstream is unavailable.

`MUSICBRAINZ_AUTHORIZED_USER_AGENT_OVERRIDE` is an exceptional, temporary operator-approved compatibility setting. Leave it unset for normal operation so Allstarr derives its identity from `AppVersion.Version`. Never copy another application's identity without explicit permission from the upstream operator; remove the override as soon as Allstarr's own identity is admitted.

## Optional services

Use `allstarr.sh` rather than editing Compose fragments:

```bash
./allstarr.sh enable spotify-lyrics
./allstarr.sh install-apple x86_64
./allstarr.sh up
```

See [deployment profiles](deployment-profiles.md), [Spotify lyrics](spotify-lyrics-sidecar.md), and [Apple download](apple-download-provider.md).

## Legacy import

A legacy `.env` is imported explicitly after the new deployment is running. Startup never scans it automatically. See [the legacy import contract](legacy-env-import.md).
