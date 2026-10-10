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

Non-secret product behavior belongs in household SQLite settings and is edited through the dashboard surface that owns it. General playback, cache, matching, playlist, and diagnostics policy lives under **Settings**. Provider priority lives under **Integrations > Routing**. Each user's explicit-content filter and external/explicit title labels can override the household defaults under **Preferences**; clearing an override restores the current household value.

`DurableRuntimeSettingsService` owns validation, typing, revisions, optimistic concurrency, and live option updates after a successful commit. Controllers must not add a second environment or JSON owner for these settings.

## Provider accounts

Provider credentials are encrypted and persisted as **Personal** or **Shared** accounts. Personal accounts have one user owner; Shared accounts have no user owner and are managed by administrators. Services and their configuration are managed under **Integrations > Services**. Credentials and audience policy live under **Integrations > Accounts**; capability priority lives under **Integrations > Routing**.

`ProviderAccounts:ListenersCanConnectOwnAccounts` defaults to `true` and is editable in Settings as **Listeners can connect their own accounts**. It gates creation of Personal accounts by listeners. Turning it off preserves use, configuration and removal of existing accounts. Administrators can manage all accounts, but cannot route playback through another user’s Personal credential. The former account management modes and global-account policy switches are no longer supported.

Routing selects the requesting user’s enabled, eligible Personal account first, then an eligible Shared account. Explicit account selection follows the same ownership checks; revoked credentials are unavailable. Accounts are independent of backend music libraries. Shared use can consume provider limits and includes the capabilities declared by that provider. See the [account workflow](../user-guide.md#integrations).

Extensions are package implementations, not a second account system. Their install, update, permission, and removal lifecycle lives under **Integrations > Extensions**. Once active, their Services and Accounts use the same Integrations surfaces as built-in providers.

AudioMuse-AI is a built-in Intelligence integration rather than an extension. It is composed only when `ALLSTARR_RELEASE_PROFILE=development`; its persisted configuration and data remain intact while the core profile is active.

Shared accounts are eligible for household use only while enabled and authorized for the requested capability. They do not grant access to any backend library or another user's private playlist management.

## Backend setup

`BACKEND_TYPE` selects Jellyfin or Subsonic/OpenSubsonic before startup. Backend URL, credentials, instance identity, library selection, and user mapping are completed through onboarding and durable configuration. An imported legacy file must not switch the active backend.

`Identity:BackendInstanceId` defaults to `primary` and identifies the selected backend instance. Users are created on verified sign-in and are distinct by backend type, instance, and backend principal. Their administrator role is read from the backend. Old multi-user modes and provider account management modes are recognized only as unsupported legacy input; they do not change ownership. Indexing includes all music libraries unless the administrator selects a subset, and each request still checks the listener's backend library permissions.

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

`EXPLICIT_FILTER` seeds the household default through the existing settings importer. Supported values are `All`, `ExplicitOnly` and `CleanOnly`; the default is `All`. `ExplicitOnly` retains its v2 meaning: hide only clean/edited versions (Deezer value 3), while retaining naturally clean (0), explicit (1), unknown/no-advice (2, 6, 7), and missing values. The WebUI labels it **Hide clean/edited versions**. Unknown explicit status is included in every mode. Each signed-in listener can override this filter and independently show the `[A]` external and `[E]` explicit title markers in **Listening preferences**. Both markers default to on. Reset removes that listener's overrides and uses the latest household defaults. Native backend objects remain unchanged; routing order and deployment settings remain household controls.


## Provider connection status

Status is kept in memory for each provider, account revision and capability. Restarting starts account status at Unknown; normal requests rebuild it. Changing credentials cannot reuse a previous revision's health. By default three failures open a 60-second cooldown; credential rejection opens it immediately, and rate limits can request a longer wait. After cooldown, normal requests can try again. Historical health samples and rollups are not stored. Explicit stream diagnostic results remain in the audit history.

Sidecar readiness uses cheap bounded requests, starts immediately, and refreshes within a minute. `Sidecars:ProbeIntervalSeconds` defaults to 30 (allowed 5–30), and `ProbeTimeoutSeconds` defaults to 5 (allowed 1–5); values outside these ranges, including the earlier 900-second default, are clamped with a startup warning. Up to 256 targets run in batches of at most 64 concurrent requests. The previous jitter and per-cycle target settings are no longer used. Optional sidecar failures affect only their capabilities. No background task logs into managed accounts or downloads a media sample.

An administrator's account Test first checks each capability's connection. For a streaming account whose checks pass, it then plays a short real sample: at most 64 KiB within 30 seconds (five minutes for extensions that download before streaming), and reports the observed playback support: byte ranges, provider seeking, or sequential playback. The sample needs a known track from saved provider metadata; until one exists the Test reports the connection result and asks for one (refresh playlist metadata first). Playlist and scrobbling tests check their own capabilities. Streaming lease creation alone does not count as successful playback.

The health-table migration preserves users, accounts, encrypted credentials, settings and playlist data. Back up the database and matching encryption key ring before upgrading. A rollback requires the matching older database/key-ring pair and application image.
