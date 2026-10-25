# Configuration

Allstarr separates deployment bootstrap, durable application settings, and encrypted credentials. Do not move a value between these owners merely to make it editable in the WebUI.

## Deployment-owned values

`.env` is optional. It holds values needed before storage opens: image and release profile, ports, bind address, trusted subnets and the local data path. `.env.example` lists the supported deployment defaults.

| Group | Values |
| --- | --- |
| Image and local data | `ALLSTARR_IMAGE`, `ALLSTARR_RELEASE_PROFILE`, `ALLSTARR_DATA_PATH` |
| Public listeners | `PROXY_BIND_ADDRESS`, `PROXY_PORT`, `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` |
| Admin network policy | `ADMIN_BIND_ANY_IP`, `ADMIN_TRUSTED_SUBNETS`, `ADMIN_BASE_PATH` |
| Legacy or temporary backend selection | `BACKEND_TYPE`, `JELLYFIN_URL`, `SUBSONIC_URL` |
| Optional session verifier | `SESSION_VERIFIER_URL` (Compose), `Extensions__SessionVerification__BaseUrl` (Allstarr) |

The default Compose file passes these bootstrap values to Allstarr. It does not mount `.env` into the container or import product settings from it. Changing a deployment value requires recreating the container. Existing v2 deployments can keep their mounted `.env` for the explicit upgrade review.

Compose uses `./data` at `/app/data` unless `ALLSTARR_DATA_PATH` selects another local path. SQLite, the key ring and managed media share that folder. No database sidecar is required. A fresh core starts with `docker compose up -d`; choose and verify the backend in the dashboard. Its saved connection takes precedence over legacy startup defaults. See [deployment](deployment-profiles.md).

For a dashboard mounted under a reverse-proxy path or optional OIDC login, see [WebUI proxy and SSO setup](webui-access.md). Neither setting changes music-client authentication on port 5274.

Saved Jellyfin connections require encrypted service access for playlist sync and library scans. If it is missing or unreadable, Home and Settings link to **Settings → Access → Media-server access**. When a legacy `JELLYFIN_API_KEY` is still mounted or present in the process environment, this page and the import review offer **Verify and restore access** separately from a settings import. Confirmation verifies administrator permissions before encryption and restart; the plain key is never a fallback for a saved connection. Rematching preserves existing local matches while the library index or access is unavailable and retries later.

## Protected files

First startup creates `keyring.json` with private permissions when the database has no saved secrets. Keep it with the database: losing the key ring prevents decryption of saved provider credentials. `Secrets__KeyRingPath` remains available as an explicit override; it must be writable to apply a restore. Use the verified backup and restore controls in Settings → Backups. See [storage operations](storage.md) for data-folder contents and offline copies.

## Durable settings

Non-secret product behavior belongs in household SQLite settings and is edited through the dashboard surface that owns it. Operator, cache and diagnostics policy lives under **Settings**. Source order, playback quality and matching preference live under **Integrations > Routing**. Playlist behavior stays with each playlist. Each user's explicit-content filter and external/explicit title labels can override the household defaults under **Preferences**; clearing an override restores the current household value.

`DurableRuntimeSettingsService` owns validation, typing, revisions, optimistic concurrency, and live option updates after a successful commit. Controllers must not add a second environment or JSON owner for these settings.

## Music-client address

Set **Settings → Network → Music-client address** to the Allstarr gateway URL listeners enter in their music apps, for example `https://music.example.com`. Include the port and any gateway path prefix when needed. This optional household setting updates **Getting started** immediately. It does not change listener ports, reverse-proxy routing or the backend connection. An unset value leaves the guide's address empty.

The durable key is `Gateway:PublicListenerUrl`; `PUBLIC_LISTENER_URL` is its environment/import alias. Use an absolute HTTP or HTTPS URL without credentials, a query or a fragment. Keep the backend server's private address out of this field. The guide uses the signed-in listener's verified backend type and username and offers only compatible apps. See [connecting a music app](../user-guide.md#connect-your-music-app).

Problem reports are stored in SQLite and included in database backups. Only administrators can list reports, inspect their captured diagnostics or change their status. Signed-in listeners can preview and submit reports for their accessible songs and observed playback. Capture reads existing local metadata, match authority and source-check results; it does not start a provider request, playback probe or download. Submitted context is bounded and redacts credential assignments and URLs. Reports are not sent to an external service.

## Provider accounts

Provider credentials are encrypted and persisted as **Personal** or **Shared** accounts. Personal accounts have one user owner; Shared accounts have no user owner and are managed by administrators. Services and their configuration are managed under **Integrations > Services**. Credentials and audience policy live under **Integrations > Accounts**; capability priority lives under **Integrations > Routing**.

`ProviderAccounts:ListenersCanConnectOwnAccounts` defaults to `true` and is editable in Settings as **Listeners can connect their own accounts**. It gates creation of Personal accounts by listeners. Turning it off preserves use, configuration and removal of existing accounts. Administrators can manage all accounts, but cannot route playback through another user’s Personal credential. The former account management modes and global-account policy switches are no longer supported.

Routing selects the requesting user’s enabled, eligible Personal account first, then an eligible Shared account. Explicit account selection follows the same ownership checks; revoked credentials are unavailable. Accounts are independent of backend music libraries. Shared use can consume provider limits and includes the capabilities declared by that provider. See the [account workflow](../user-guide.md#integrations).

Extensions are package implementations, not a second account system. Their install, update, permission, and removal lifecycle lives under **Integrations > Extensions**. Once active, their Services and Accounts use the same Integrations surfaces as built-in providers.

Shared accounts are eligible for household use only while enabled and authorized for the requested capability. They do not grant access to any backend library or another user's private playlist management.

## Spotify playlist compatibility

Public Spotify playlist reads use an anonymous web session with no account or cookies. Private playlists and Liked Songs use only the selected encrypted account. Import requests accept a playlist ID, `spotify:playlist:` URI, or an `https://open.spotify.com/playlist/…` link; tracking parameters and fragments are discarded before persistence. The logical source ID `liked-songs` reads that account's Liked Songs through the same paged playlist pipeline and preserves Spotify's returned order.

Administrators can edit compatibility fields under **Integrations → Services → Spotify** without restarting: Pathfinder endpoint, library/playlist/Liked Songs query hashes, token endpoint, and the public TOTP secrets URL. The shipped v1 endpoint and existing hashes remain the defaults. Selecting Spotify's v2 endpoint sends the corresponding POST query envelope. Query hashes require 64 hexadecimal characters; token-bearing endpoints are restricted to the supported Spotify URLs, and the public secrets URL requires HTTPS without embedded credentials. Credentials remain in Accounts.

A persisted-query failure reports a provider contract change. Check the compatibility fields against a trusted current source before changing them; token failures remain account errors, and an incompatible response does not publish a partial playlist snapshot.

## Backend setup

A fresh persistent installation starts with health and guided backend setup available before a server has been selected. `GET /api/admin/auth/setup` returns the shared first setup step and an antiforgery token. `POST` to that endpoint verifies an active media-server administrator before saving the backend type and base address in SQLite. The response completes before Allstarr restarts to compose the selected Jellyfin or Subsonic/OpenSubsonic surface. A configured installation refuses anonymous replacement of that connection.

The optional library-scanning choice saves the verified Jellyfin service token through the encrypted secret store, bound to the exact backend address and credential purpose. Subsonic playlist credentials remain listener-specific consent. Passwords are used for verification, and the setup response contains no credentials. A later startup applies a staged restore before reading the saved connection, so the restored database selects the protocol. Saved household settings take precedence over deployment defaults; personal overrides remain scoped to their user.

In a version 2 layout, an explicit backend address/type remains available for native proxying while the upgrade is pending. The confirmed legacy import saves that same verified connection once and preserves existing encrypted service access. Imported values never switch the active backend. Product environment values are accepted through the explicit import boundary rather than reread as live settings.

`Identity:BackendInstanceId` defaults to `primary` and identifies the selected backend instance. Users are created on verified sign-in and are distinct by backend type, instance, and backend principal. Their administrator role is read from the backend. Old multi-user modes and provider account management modes are recognized only as unsupported legacy input; they do not change ownership. Indexing includes all music libraries unless the administrator selects a subset, and each request still checks the listener's backend library permissions.

`ALLSTARR_RELEASE_PROFILE` defaults to `core`. The core profile omits unreleased Intelligence controllers, recommendation providers, imports, background handlers, and direct WebUI access while preserving their database records. `development` explicitly composes those surfaces for continued work. Unknown profile names stop startup instead of silently widening the release.

## Background cross-provider links

Enable **Public cross-provider links** in Integrations → Services to collect optional cross-provider evidence after a match
has been saved. It defaults to false. A durable job queries public track links,
verifies the exact source ID, and stores new catalog aliases through the recording
identity owner. Existing conflicting or manual associations are left unchanged.
New links enqueue an owner-scoped rematch; lookups never run inside the original
match decision, search, or playback request. Existing lyrics URL translation uses
the same bounded client independently of this background-work toggle.

The cross-provider request interval defaults to 6000 ms and accepts 1000–60000 ms.
Successful responses use the existing Odesli cache duration; empty responses are
cached for six hours. Rate limits and server failures honor `Retry-After`, bounded
to one hour, across all requests. Jobs make at most three attempts; a later match
can request unresolved evidence again the following week. Disabling enrichment
leaves previously accepted aliases intact.

## Canonical metadata upstream

MusicBrainz recording enrichment is optional and disabled by default. Enable MusicBrainz under Integrations → Services to discover recordings with ISRCs but no MBID in
hourly background passes. Each lookup runs as a low-priority durable job, with
three attempts; unresolved or exhausted work can be queued again the following
week. It never runs inside a search, playback, library scan or match decision.
The MusicBrainz request interval defaults to 1000 ms and accepts 1000–60000 ms.
Rate limits and temporary failures use cancellable requests and durable retries;
`Retry-After` is bounded to one hour. Exact, unambiguous evidence can add ISRCs
and merge recordings, retaining the earliest public ID and all old redirects.
Ambiguous results or conflicting MBIDs leave the existing identity unchanged.

Allstarr's catalog client speaks the read-only MusicBrainz `/ws/2` contract. The public MusicBrainz service remains the bootstrap default. After the BrainzMash operator approves Allstarr's user agent, select the pool without changing matching or catalog ownership:

In Integrations → Services → MusicBrainz, set the metadata source identifier to `brainzmash`, the service URL to `https://api.brainzmash.cc/ws/2`, and the request interval to `1000` ms, then save and restart.

This is the MusicBrainz-compatible front door used by DroppedNeedle. Do not append `/ws/2` to `https://lidarrapi.brainzmash.cc`: that is the separate Lidarr/Aurral-shaped API with `/artist`, `/album`, and `/search/*` resources, and Allstarr deliberately does not implement that second dialect. Requests identify themselves with the version derived from `AppVersion.Version` and the Allstarr repository URL. Source IDs isolate cache entries and provenance, so changing the upstream cannot reuse another source's response. A 401 or 403 is an access/configuration failure, not a missing recording; do not retry it as catalog churn. Playback uses persisted routes and remains available when the metadata upstream is unavailable.

The authorized user agent override is an exceptional, temporary operator-approved compatibility setting. Leave it blank for normal operation so Allstarr derives its identity from `AppVersion.Version`. Never copy another application's identity without explicit permission from the upstream operator; remove the override as soon as Allstarr's own identity is admitted.

## Optional services

Use `allstarr.sh` rather than editing Compose fragments:

```bash
./allstarr.sh enable apple x86_64
```

The optional [session verifier](../../sidecars/session-verifier/README.md) uses a deployment-owned service address. `./allstarr.sh enable sessions` configures it and recreates Allstarr; the address is read at startup and does not require a legacy settings import.

See [deployment profiles](deployment-profiles.md), [Spotify lyrics](spotify-lyrics.md), and [Apple download](apple-download-provider.md).

## Legacy import

A legacy `.env` is imported explicitly after the new deployment is running. Startup never scans it automatically. See [the legacy import contract](legacy-env-import.md).

## Music libraries

Allstarr indexes every music library returned by the backend by default. Select backend music libraries in Settings to restrict indexing and local matching to a subset. An empty selection includes all libraries. Legacy library selections can be reviewed during the explicit settings import.

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

## Service and retention settings

Settings owns browser origins/methods/headers/credential policy, remote extension installation, request logging, activity and job-history retention, automatic backup retention, and the selected backend music libraries. These values are saved in SQLite. Library selection preserves opaque identifiers and never grants a listener additional backend permissions. Browser credentials require explicit origins; wildcard credential policies are refused in both import preview and apply.

Integrations → Services → MusicBrainz owns the metadata source identifier, service URL, request interval and optional authorized user agent override. The public metadata service needs no username or password. Changes to these service and operator settings are marked as requiring a restart. Save commits first and requests restart after the response completes; a container restart policy brings Allstarr back. Source changes isolate cache/provenance by identifier. Existing product environment values are imported once through the reviewed legacy import and cannot override saved settings on later startups.

## Administrator access settings

Use **Settings → Network → Single sign-on** for OIDC and **Settings → General → Background library access** for unattended Jellyfin scans. Saves restart Allstarr after confirmation; the dashboard waits for it to return.

The authenticated `GET`/`PUT /api/admin/access/oidc` API owns single sign-on policy and its encrypted client secret. GET returns non-secret settings, an opaque revision and an antiforgery token; PUT requires both. Changing the identity service or client ID requires a new client secret. A save requests restart after the response completes. Missing, revoked or incorrectly scoped encryption references leave single sign-on disabled for review while media-server login remains available. Old SSO environment values are reported for explicit review, never automatically enabled.

`GET`/`PUT /api/admin/access/backend` manages optional Jellyfin scanning access after the connection is saved. Enable verifies the same active media-server administrator outside the database writer, then rechecks user, connection and setting revisions before encrypting the token. Disable revokes only that exact backend-purpose reference. Passwords are never saved. Subsonic playlist grants remain personal and separate. These operations require an active administrator, antiforgery protection and a current revision; native proxying does not require scanning access.
