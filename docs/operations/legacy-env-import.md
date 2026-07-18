# Legacy `.env` Import Contract

This document defines the boundary for the pre-overhaul `.env` migration wizard. It does not turn startup into a migration path. Version 3 still starts from a fresh Compose deployment and a fresh durable database, then an administrator can bring forward the safe parts of a 2.x configuration through the WebUI.

The wizard is an explicit, one-shot administrator action. It accepts an uploaded file or pasted contents, produces a short-lived administrator preview, requires confirmation, writes through durable services, and leaves the source file unchanged. Values are visible to the authenticated administrator by default so the import can be verified. Turn on **Redact for sharing** in the sidebar before taking screenshots; operators can make that the initial browser default with `ADMIN_REDACT_SENSITIVE_VALUES=true`. Startup and normal schema migrations never scan or import a legacy `.env`. The old wholesale `/api/admin/import-env` replacement endpoint is retired.

## Upgrade Procedure

Use a separate version 3 deployment, database, key ring, and Allstarr-managed download and kept roots. Stop the
version 2 stack before version 3 can write media. The existing backend music library may be attached as a read-only
matching source, but version 2 and version 3 must never write the same download, kept, cache, or managed-library
root at the same time. Keep the stopped version 2 deployment and its data for rollback until version 3 is verified.

1. Back up the version 2 `.env`, application data, media roots, and backend-specific playlist data.
2. Create the fresh version 3 Compose deployment. Configure its database, key ring, backend selection, mounts,
   listener policy, and other deployment-owned values before startup.
3. Start version 3 and wait for readiness. Sign in through the administrator WebUI with an administrator whose
   session is linked to the new Allstarr tenant.
4. Open **Settings**, choose **Migrate legacy `.env`**, then upload the original file or paste its contents. The
   first administrator login also offers a neutral **Upgrading from Allstarr 2.x?** shortcut. Dismissing that prompt
   does not remove the Settings workflow.
5. Review every preview category: durable settings ready to add, disabled shared accounts, deployment checklist,
   personal accounts for the signed-in administrator, conflicts, unknown or deprecated keys, and playlist ownership handoffs. Enable sharing redaction before sharing the screen or taking screenshots. Fix any blocking input error and create a new preview. A preview expires after 15 minutes and belongs
   to the administrator session that created it.
6. Check the confirmation box and apply. The wizard adds only absent allowlisted settings and absent eligible
   shared accounts. It does not replace existing durable settings or credentials.
7. Copy reviewed deployment-checklist values into the new Compose or `.env` configuration and recreate the
   affected containers if needed. Review each imported shared account, test its health and permissions, then enable
   it explicitly. The importer creates encrypted, user-scoped Last.fm and ListenBrainz accounts only for the signed-in administrator when those legacy values are present. Other users connect their own accounts, and Spotify still requires an explicit reconnect.
8. Recreate each playlist handoff through the provider-neutral playlist workflow. Select its owner, user-scoped
   provider account, backend, library, target playlist, reconcile or recreate mode, and schedule.
9. Run the readiness and client smoke checks. Take a verified Postgres backup, back up the key ring separately, and
   back up media roots separately. Only then cut clients over to version 3. Never use `docker compose down -v` as
   part of this procedure.

## Classification Rules

Every recognized key has exactly one disposition:

| Class | Meaning |
| --- | --- |
| Deployment only | The operator reviews and copies the value into the new Compose/bootstrap environment. It is never written into a durable setting row by the importer. |
| Durable setting | A non-secret application preference may be written into the typed, tenant-scoped durable runtime settings store. |
| Encrypted global provider secret | A legacy shared credential may create one disabled-by-default global provider account and encrypted secret reference after explicit acknowledgement. |
| Per-user account | Last.fm and ListenBrainz credentials may be imported into an encrypted account owned by the signed-in administrator. Ambiguous multi-user data is never guessed. |
| Ignored or deprecated | The value has no target in the new baseline. It is reported and left behind. |

An empty value is `absent`, not an instruction to clear a target. Empty JSON arrays are valid only for keys whose documented value is an array. Secret values may appear only in the authenticated, short-lived preview unless sharing redaction is enabled. They must never appear in durable reports, logs, job payloads, audit metadata, command history, or exception text.

## Exact Key Matrix

The names below cover the checked-in legacy configuration surface plus compatibility keys still read by the current configuration controller. A key not listed here is unknown and must be reported as `unknown`; it must not be copied using a prefix or best-effort rule.

### Deployment Only: Retain In Compose Or Bootstrap Configuration

| Legacy keys | New owner | Import behavior |
| --- | --- | --- |
| `BACKEND_TYPE`, `SUBSONIC_URL`, `JELLYFIN_URL`, `JELLYFIN_USER_ID`, `JELLYFIN_LIBRARY_ID` | Selected protocol/backend deployment | Report the proposed values for operator review. Never change the active protocol or backend from an import. |
| `JELLYFIN_API_KEY` | Backend bootstrap secret | Do not place it in a durable provider account. Keep it in protected deployment configuration until a separate backend-credential reference is implemented. Always redact it. |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD_FILE`, `ALLSTARR_KEYRING_FILE`, `STORAGE_AUTO_MIGRATE` | Durable storage deployment | Never import. These select and unlock the target that receives an import. |
| `ALLSTARR_IMAGE`, `PROXY_BIND_ADDRESS`, `PROXY_PORT`, `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` | Compose/runtime deployment | Review and copy manually. An application import cannot change its own image or listener bindings. |
| `VALKEY_MAX_MEMORY`, `REDIS_ENABLED` | Valkey/cache deployment | `REDIS_ENABLED` remains the compatibility name for enabling the Valkey-backed cache. Do not migrate cache contents. |
| `ALLSTARR_MULTI_USER_MODE`, `ALLSTARR_BACKEND_INSTANCE_ID`, `ALLSTARR_PROVIDER_ACCOUNT_MANAGEMENT_MODE` | Identity/bootstrap policy | Select before import. Never let legacy input replace identity mode, backend instance identity, or account-management policy. |
| `ADMIN_BIND_ANY_IP`, `ADMIN_TRUSTED_SUBNETS`, `ADMIN_ENABLE_ENV_EXPORT`, `ADMIN__ENABLE_ENV_EXPORT` | Admin listener/security policy | Manual review only. Import never broadens network or export access. |
| `CORS_ALLOWED_ORIGINS`, `CORS_ALLOWED_METHODS`, `CORS_ALLOWED_HEADERS`, `CORS_ALLOW_CREDENTIALS` and their `CORS__*` aliases | Browser/network policy | Manual review only. Import never broadens cross-origin access. |
| `DOWNLOAD_PATH`, `KEPT_PATH`, `LIBRARY_DOWNLOAD_PATH`, `LIBRARY_KEPT_PATH` | Mounted media roots | Review and map to real mounts before startup. Never rewrite stored media paths just because a legacy path differs. |
| `SPOTIFY_LYRICS_API_URL` | Optional external provider endpoint | Manual review only. Import must not authorize a new outbound origin. |
| `MUSICBRAINZ_USERNAME`, `MUSICBRAINZ_PASSWORD` | MusicBrainz bootstrap configuration | The current core has no durable MusicBrainz provider-account contract. Keep both in protected deployment configuration or re-enter them manually; never manufacture a provider account. Always redact the password. |
| `DEBUG_LOG_ALL_REQUESTS`, `DEBUG_REDACT_SENSITIVE_REQUEST_VALUES` | Runtime diagnostics | Manual review only. Redaction remains on regardless of a legacy value during import. |

### Durable Non-Secret Settings

These keys use `tenant_runtime_settings`, which has tenant scope, a stable key, declared value type, normalized JSON value, source, actor, timestamps, revision, and optimistic concurrency. `DurableRuntimeSettingsService` applies an allowlist and validates the whole batch before staging any row. Deployment values and secrets are not in its catalog and cannot enter this table.

| Setting group | Legacy keys | Target scope and notes |
| --- | --- | --- |
| Cache TTLs | `CACHE_SEARCH_RESULTS_MINUTES`, `CACHE_PLAYLIST_IMAGES_HOURS`, `CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS`, `CACHE_SPOTIFY_MATCHED_TRACKS_DAYS`, `CACHE_LYRICS_DAYS`, `CACHE_GENRE_DAYS`, `CACHE_METADATA_DAYS`, `CACHE_ODESLI_LOOKUP_DAYS`, `CACHE_PROXY_IMAGES_DAYS`, `CACHE_TRANSCODE_MINUTES` | Tenant setting. Validate the same numeric bounds as the runtime model. These do not import Valkey or file-cache contents. |
| Apple download gateway | `APPLE_DOWNLOAD_URL`, `APPLE_MUSIC_AIO_URL`, `APPLE_DOWNLOAD_QUALITY`, `APPLE_MUSIC_QUALITY` | Tenant provider settings. The `APPLE_MUSIC_*` names are legacy aliases for the current `APPLE_DOWNLOAD_*` names. Import only when the target setting is absent. Preview the gateway origin for administrator review; it must identify an Allstarr-compatible gateway, not raw wrapper-v2. Importing the URL does not create an Apple MusicKit account or copy a gateway session. |
| Provider preference | `SQUIDWTF_QUALITY`, `SQUIDWTF_MIN_REQUEST_INTERVAL_MS`, `DEEZER_QUALITY`, `DEEZER_MIN_REQUEST_INTERVAL_MS`, `QOBUZ_QUALITY`, `QOBUZ_MIN_REQUEST_INTERVAL_MS` | Tenant provider policy, not a credential. Validate choices and bounds against the typed runtime-setting catalog. A setting for an unavailable optional provider stays dormant rather than being deleted. |
| Provider routing | `MULTI_PROVIDER_METADATA_ORDER`, `MULTI_PROVIDER_DOWNLOAD_ORDER`, `MULTI_PROVIDER_STREAMING_ORDER`, `MULTI_PROVIDER_PLAYLIST_ORDER`, `MULTI_PROVIDER_LYRICS_ORDER`, `MULTI_PROVIDER_ENABLED_SEARCH`, `MULTI_PROVIDER_ENABLED_PLAYLIST`, `MULTI_PROVIDER_DISABLED_PROVIDERS` | Tenant capability policy. Normalize comma-separated provider IDs, remove duplicates, and preserve lane separation. Provider and extension IDs may remain configured while their capability is unavailable. Do not convert `MUSIC_SERVICE` into all three lanes. |
| Library behavior | `ENABLE_EXTERNAL_PLAYLISTS`, `PLAYLISTS_DIRECTORY`, `EXPLICIT_FILTER`, `DOWNLOAD_MODE`, `STORAGE_MODE`, `CACHE_DURATION_HOURS` | Tenant default policy. Paths remain deployment-owned; only validated behavior values are durable. Favorite and managed-file policies are not inferred from these flags. |
| MusicBrainz behavior | `MUSICBRAINZ_ENABLED` | Tenant capability policy. Credentials are classified separately. |
| Spotify compatibility behavior | `SPOTIFY_API_ENABLED`, `SPOTIFY_API_CACHE_DURATION_MINUTES`, `SPOTIFY_API_RATE_LIMIT_DELAY_MS`, `SPOTIFY_API_PREFER_ISRC_MATCHING`, `SPOTIFY_IMPORT_ENABLED`, `SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS` | Tenant compatibility setting. Enabling a flag does not create an account, playlist link, or schedule. |
| Scrobbling behavior | `SCROBBLING_ENABLED`, `SCROBBLING_LOCAL_TRACKS_ENABLED`, `SCROBBLING_SYNTHETIC_LOCAL_PLAYED_SIGNAL_ENABLED`, `SCROBBLING_LASTFM_ENABLED`, `SCROBBLING_LISTENBRAINZ_ENABLED` | Tenant defaults only. No target becomes ready until a user explicitly connects it. Import must warn about duplicate-scrobble risk when local scrobbling is enabled. |

### Encrypted Global Provider Secrets

These are the only legacy shared credential bundles eligible for global-account import. The WebUI confirmation names the disabled shared accounts that will be created. Each bundle is validated atomically, encrypted through `EncryptedSecretStore`, and attached to one new admin-owned `ProviderAccountRecord` with `Scope=Global`. The account starts disabled until an administrator reviews its provider, non-personal capability policy, and health result. A global account never authorizes personal playlists, personal libraries, favorites, listening history, recommendations, or scrobbling.

| Provider bundle | Legacy keys | Atomicity and target |
| --- | --- | --- |
| Deezer | `DEEZER_ARL`, optional `DEEZER_ARL_FALLBACK` | At least the primary ARL is required. Store both in one versioned secret document for provider `deezer`. |
| Qobuz | `QOBUZ_USER_AUTH_TOKEN`, `QOBUZ_USER_ID` | Both are required. Store them together for provider `qobuz`; never import a token without its account ID. |
| Spotify shared session | `SPOTIFY_API_SESSION_COOKIE`, optional `SPOTIFY_API_SESSION_COOKIE_SET_DATE` | Import only with explicit shared-account acknowledgement. Validate the date if present. The resulting account is eligible only for reviewed non-personal catalog/metadata behavior. Personal playlist access must use an explicitly connected user-scoped Spotify account. This does not import user cookie maps or playlist links. |

A pre-existing provider account is a conflict even when the display name differs. The importer must not merge secret JSON, compare plaintext values, rotate an existing reference, or attach a new secret to an existing account. An administrator can use the normal credential replacement flow after reviewing the dry run.

### Per-User Or Ownership-Ambiguous

| Legacy keys or objects | Reason |
| --- | --- |
| `SPOTIFY_API_SESSION_COOKIES`, `SPOTIFY_API_SESSION_COOKIE_SET_DATES` | The JSON keys are legacy backend/user identifiers and cannot be assumed to be current tenant user IDs. Each linked user reconnects Spotify. |
| `SCROBBLING_LASTFM_API_KEY`, `SCROBBLING_LASTFM_SHARED_SECRET`, `SCROBBLING_LASTFM_USERNAME`, `SCROBBLING_LASTFM_PASSWORD`, `SCROBBLING_LASTFM_SESSION_KEY` | When the administrator session is linked to an Allstarr user, import the bundle into one encrypted, enabled, user-scoped Last.fm account owned by that user. Never create a global account or overwrite an existing personal account. Other users reconnect separately. |
| `SCROBBLING_LISTENBRAINZ_USER_TOKEN` | When the administrator session is linked to an Allstarr user, import the token into one encrypted, enabled, user-scoped ListenBrainz account owned by that user. Never create a global account or overwrite an existing personal account. |
| `SPOTIFY_IMPORT_PLAYLISTS` | Legacy rows may omit owner, backend playlist ID, provider account, library, target credential, conflict mode, and durable schedule identity. The importer must parse and preserve every valid source definition in a non-secret handoff artifact/report with its name, Spotify playlist ID, local-track position, source order, and validation result. It must not activate a durable link. An administrator assigns the owner, exact user-scoped Spotify account, backend target, library, mode, and schedule through the provider-neutral playlist workflow. |
| Legacy favorite settings, Spotify mapping files, playlist cache files, Redis/Valkey keys, extension enable state, job files, and version markers | None carries enough authenticated scope and current schema evidence for safe import. Rebuild or recreate through current APIs. |

### Ignored Or Deprecated

| Legacy keys | Reason |
| --- | --- |
| `MUSIC_SERVICE` | The single primary provider conflicts with capability-specific metadata, streaming, download, playlist, and lyrics lanes. Report it as a hint only; never fan it out automatically. |
| Pre-overhaul split playlist variables such as `SPOTIFY_IMPORT_PLAYLIST_*` | The supported legacy aggregate was `SPOTIFY_IMPORT_PLAYLISTS`, which is manual-only above. Do not guess a playlist from partial variables. |
| `EXTENSION_REPOSITORIES` | Registry trust requires explicit current review. Report repository URLs in redacted-safe form, but do not add or trust them during import. |
| Redis host/password/database keys from old deployments | Standard Compose owns the current Valkey connection. Cache data is expendable and is never imported. |
| Removed Redis-to-Valkey overlay flags or conversion markers | The conversion overlay is retired. They have no new-baseline meaning. |

## Conflict And Transaction Policy

The dry-run report is the source of truth for operator review. It includes the source file SHA-256, parser version, each recognized key's class and proposed target, validation errors, conflicts, ignored keys, and unknown keys. Secret rows contain only `present`, source line, bundle completeness, and proposed provider/scope. They never contain a value, length, prefix, suffix, hash, or fingerprint.

Apply follows these rules:

1. The source fingerprint and target-state revision must match the reviewed preview token.
2. Key matching is case-insensitive. When a hand-edited file contains duplicate active assignments, the last active
   assignment wins, matching common `.env` loading behavior. The preview identifies the winning and ignored source
   line numbers without displaying either value. Commented definitions do not count. Review every duplicate warning
   before applying because an earlier assignment may have been retained accidentally.
3. Malformed lines, invalid UTF-8, invalid JSON, and invalid recognized values block apply. Unknown keys are reported for manual review and are never imported.
4. Deployment-only, manual, ignored, and unknown keys never write durable data.
5. A durable setting is inserted only when its exact target scope/key is absent. Existing values are kept and reported as non-blocking conflicts. The migration wizard has no bulk replacement mode.
6. Secret-account conflicts always keep the target. There is no bulk overwrite flag for credentials.
7. Credential bundles are all-or-nothing. Partial bundles are validation errors, not partially useful imports.
8. All durable settings, accounts, secret references, secret versions, and the import audit record commit in one database transaction. Any validation, encryption, constraint, or concurrency failure rolls the transaction back.
9. A successful source SHA-256 is recorded durably. Replaying the same confirmed source is idempotent and does not create duplicates. A different source requires a new preview and confirmation.
10. Imported global accounts remain disabled. Enabling one is a separate authenticated admin action after health and permission review.

The importer does not require the whole database to be empty. It checks the target tenant settings, eligible global
accounts, and prior successful source fingerprint before apply. This lets operators add reviewed legacy settings
to a freshly bootstrapped tenant without weakening conflicts around identities, accounts, or secrets.

## Minimum Verification

Coverage must remain in place for every classification row or key group, deterministic duplicate-key handling and
redacted duplicate warnings, empty values, malformed
JSON, partial credential bundles, secret redaction, unknown keys, dry-run/apply hash mismatch, idempotent reapply
rejection, setting conflicts, account conflicts, transaction rollback, tenant scope, encryption/key rotation
compatibility, and absence of startup import behavior.

SQLite tests prove the complete transactional apply path, including encrypted secrets and rollback. Native Postgres integration verifies the portable runtime-settings migration and table contract. Release rehearsal must also exercise the WebUI migration against the target Postgres deployment before cutover.
