# Configuration

Allstarr separates deployment bootstrap, durable application settings, and encrypted credentials. Do not move a value between these owners merely to make it editable in the WebUI.

## Deployment-owned values

`.env` exists for values required before storage and the administrator UI are available. `.env.example` is the checked-in source of truth.

| Group | Current values |
| --- | --- |
| Backend and release selection | `BACKEND_TYPE`, `ALLSTARR_RELEASE_PROFILE` |
| Image and local data mounts | `ALLSTARR_IMAGE`, `ALLSTARR_DATA_PATH`, `APPLE_UPLOAD_PATH` |
| Public listeners | `PROXY_BIND_ADDRESS`, `PROXY_PORT`, `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` |
| Admin network policy | `ADMIN_BIND_ANY_IP`, `ADMIN_TRUSTED_SUBNETS` |
| Admin URL prefix | `ADMIN_BASE_PATH` (empty by default) |
| WebUI SSO | `ADMIN_OIDC_ENABLED`, `ADMIN_OIDC_AUTHORITY`, `ADMIN_OIDC_CLIENT_ID`, `ADMIN_OIDC_CLIENT_SECRET`, `ADMIN_OIDC_PUBLIC_URL`, `ADMIN_OIDC_DISPLAY_NAME` |
| Extension install policy | `EXTENSIONS_ALLOW_REMOTE_INSTALL` |
| Browser origin policy | `CORS_ALLOWED_ORIGINS`, `CORS_ALLOW_CREDENTIALS` |
| Optional Spotify lyrics bootstrap | `SPOTIFY_API_SESSION_COOKIE` |
| Canonical metadata upstream | `MUSICBRAINZ_SOURCE_ID`, `MUSICBRAINZ_BASE_URL`, `MUSICBRAINZ_RATE_LIMIT_MS`, exceptional `MUSICBRAINZ_AUTHORIZED_USER_AGENT_OVERRIDE` |

The Compose file translates these values into ASP.NET configuration. Changing one requires recreating the affected container. Allstarr does not hot-edit its own Compose deployment.

SQLite is the durable database. Compose mounts `ALLSTARR_DATA_PATH` (default `./data` in `.env.example`) at `/app/data`. Without that variable, Compose uses the `allstarr-data` named volume. No database sidecar is required.

For a dashboard mounted under a reverse-proxy path or optional OIDC login, see [WebUI proxy and SSO setup](webui-access.md). Neither setting changes music-client authentication on port 5274.

## Protected files

First startup creates `keyring.json` with private permissions when the database has no saved secrets. Keep it with the database: losing the key ring prevents decryption of saved provider credentials. `Secrets__KeyRingPath` remains available as an explicit override; it must be writable to apply a restore. Use the verified backup and restore controls in Settings → Maintenance. See [storage operations](storage.md) for data-folder contents and offline copies.

## Durable settings

Non-secret product behavior belongs in tenant-scoped SQLite settings and is edited through the dashboard surface that owns it. General playback, cache, matching, playlist, and diagnostics policy lives under **Settings**. Provider priority lives under **Integrations > Routing**.

`DurableRuntimeSettingsService` owns validation, typing, revisions, and optimistic concurrency. Controllers must not add a second environment or JSON owner for these settings.

## Provider accounts

Provider credentials are encrypted and persisted as **Personal** or **Shared** accounts. Personal accounts have one user owner; Shared accounts have no user owner and are managed by administrators. Services and their configuration are managed under **Integrations > Services**. Credentials and audience policy live under **Integrations > Accounts**; capability priority lives under **Integrations > Routing**.

`ProviderAccounts:ListenersCanConnectOwnAccounts` defaults to `true` and is editable in Settings as **Listeners can connect their own accounts**. It gates creation of Personal accounts by listeners. Turning it off preserves use, configuration and removal of existing accounts. Administrators can manage all accounts, but cannot route playback through another user’s Personal credential. The former account management modes and global-account policy switches are no longer supported.

Routing selects the requesting user’s enabled, eligible Personal account first, then an eligible Shared account. Explicit account selection follows the same ownership checks; revoked credentials are unavailable. Accounts are independent of backend music libraries. Shared use can consume provider limits and includes the capabilities declared by that provider. See the [account workflow](../user-guide.md#integrations).

Extensions are package implementations, not a second account system. Their install, update, permission, and removal lifecycle lives under **Integrations > Extensions**. Once active, their Services and Accounts use the same Integrations surfaces as built-in providers.

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

## Music libraries

Allstarr indexes every music library returned by the backend by default. Set
`BACKEND_MUSIC_LIBRARY_IDS` to a comma-separated list of backend library identifiers
before starting Allstarr to restrict indexing and local matching to a subset.
An empty value selects all libraries. Existing `JELLYFIN_LIBRARY_ID` configuration
remains a single-library selection when the new setting is absent.

Indexed tracks retain their backend library identifier. Each listener's access is
resolved from Jellyfin `UserViews` or Subsonic `getMusicFolders`, using their current
credentials. Permission results are cached for at most 30 seconds per viewer and
authentication context and invalidated on sign-in. A failed lookup makes indexed
local copies unavailable; independently authorized provider routes can still play.
When an inaccessible match has another accessible local copy of the same recording,
Allstarr chooses it deterministically. Native backend requests continue to use the
caller's credentials.

## Listening preferences

`EXPLICIT_FILTER` seeds the household default through the existing settings importer. Supported values are `All`, `ExplicitOnly` and `CleanOnly`; the default is `All`. Unknown explicit status is included in every mode. Each signed-in listener can override this filter and independently show the `[A]` external and `[E]` explicit title markers in **Listening preferences**. Both markers default to on. Reset removes that listener's overrides and uses the latest household defaults. Native backend objects remain unchanged; routing order and deployment settings remain household controls.
