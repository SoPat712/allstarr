# Configuration

Allstarr separates deployment bootstrap, durable application settings, and encrypted credentials. Do not move a value between these owners merely to make it editable in the WebUI.

## Deployment-owned values

`.env` exists for values required before PostgreSQL and the administrator UI are available. `.env.example` is the checked-in source of truth.

| Group | Current values |
| --- | --- |
| Backend selection | `BACKEND_TYPE` |
| PostgreSQL bootstrap | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD_FILE` |
| Encryption bootstrap | `ALLSTARR_KEYRING_FILE` |
| Image and media mounts | `ALLSTARR_IMAGE`, `DOWNLOAD_PATH`, `KEPT_PATH`, `APPLE_UPLOAD_PATH` |
| Public listeners | `PROXY_BIND_ADDRESS`, `PROXY_PORT`, `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` |
| Admin network policy | `ADMIN_BIND_ANY_IP`, `ADMIN_TRUSTED_SUBNETS` |
| Extension install policy | `EXTENSIONS_ALLOW_REMOTE_INSTALL` |
| Browser origin policy | `CORS_ALLOWED_ORIGINS`, `CORS_ALLOW_CREDENTIALS` |
| Optional Spotify lyrics bootstrap | `SPOTIFY_API_SESSION_COOKIE` |

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

## Provider accounts

Provider credentials are encrypted and persisted as provider accounts with explicit tenant, user/shared scope, capability, and access policy. Services and their configuration are managed under **Integrations > Services**. Credentials and audience policy live under **Integrations > Accounts**; capability priority lives under **Integrations > Routing**.

Extensions are package implementations, not a second account system. Their install, update, permission, rollback, and removal lifecycle lives under **Integrations > Extensions**. Once active, their Services and Accounts use the same Integrations surfaces as built-in providers.

AudioMuse-AI is a built-in Intelligence integration rather than an extension. Its self-hosted URL, optional token, and optional music-server selector live under **Intelligence > Automation**; shared health remains visible in Integrations.

A shared account is not automatically available to every user. Administrators must set its access policy explicitly.

## Backend setup

`BACKEND_TYPE` selects Jellyfin or Subsonic/OpenSubsonic before startup. Backend URL, credentials, instance identity, library selection, and user mapping are completed through onboarding and durable configuration. An imported legacy file must not switch the active backend.

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
