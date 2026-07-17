# Configuration

Start with [.env.example](.env.example). It is the checked-in list of deployment-facing settings for standard Compose. The dashboard can manage supported runtime settings, but secrets should still enter through protected bootstrap files or the encrypted provider-account flow, not source control.

For the turnkey install, optional-service, update, and custom-overlay commands, use the
[deployment profile guide](docs/operations/deployment-profiles.md).

`v3.0.0-beta.1` is a fresh-install baseline. Recreate the deployment instead of copying an old `.env` wholesale.
After the new database is ready, the administrator WebUI can preview and import the safe allowlisted subset without
replacing existing settings. Old Redis, mapping, extension, and job state is not imported automatically. Follow the
[legacy environment upgrade procedure](docs/operations/legacy-env-import.md#upgrade-procedure).

## Required First Choices

### Backend and protocol

Set exactly one backend type:

```dotenv
BACKEND_TYPE=Jellyfin
```

or:

```dotenv
BACKEND_TYPE=Subsonic
```

For Jellyfin, set `JELLYFIN_URL`, `JELLYFIN_API_KEY`, `JELLYFIN_USER_ID`, and optionally `JELLYFIN_LIBRARY_ID`. The API key and user ID are for server-side library operations. Client authentication is still passed through to Jellyfin.

For Navidrome or another Subsonic-compatible backend, set `SUBSONIC_URL`. User-specific operations that need backend credentials store an encrypted credential reference through the UI/API instead of borrowing another user's credentials.

`ALLSTARR_BACKEND_INSTANCE_ID` gives the configured backend a stable identity. Do not casually change it after users, policies, jobs, or playlist links exist.

### Durable storage and encryption

Standard Compose always selects Postgres and reads its password from `POSTGRES_PASSWORD_FILE`. Create the file and the Allstarr key ring before first startup:

```bash
mkdir -p secrets
umask 077
openssl rand -base64 32 > secrets/postgres-password.txt
key="$(openssl rand -base64 32)"
printf '{"activeKeyId":"key-1","keys":{"key-1":"%s"}}\n' "$key" > secrets/allstarr-keyring.json
unset key
chmod 600 secrets/postgres-password.txt secrets/allstarr-keyring.json
```

The key ring is not stored in Postgres and is not included in database backups. Losing it makes encrypted provider secrets unreadable. Keep a protected backup.

Postgres stores application state, not audio. Set `DOWNLOAD_PATH` and `KEPT_PATH` to persistent, accessible host folders. Other managed-library roots are selected by their scoped policies. Valkey is included for cache and acceleration; there is no legacy Redis conversion step.

Standard Compose intentionally omits optional provider services. The AIO override adds the verified offline
first-party package bundle, not the external Apple gateway or another optional provider service:

```bash
docker compose -f docker-compose.yml -f docker-compose.aio.yml up -d
```

Mounting that bundle does not bypass package state or permission review. Entries marked blocked remain inactive.

Apple downloads are not part of Standard or AIO. The optional `docker-compose.apple.yml` profile builds Allstarr's
gateway with GAMDL 3.8.2 and the source-locked official wrapper-v2 0.0.2 checkout. It does not contain or download
Apple code. Prepare it with `./allstarr.sh prepare-apple FILE [ARCH]`, using a legally obtained compatible APK/APKM.
Adding or removing the profile does not replace the database, Valkey, application state, media volumes, or the
wrapper session volume. Follow [the Apple download provider procedure](docs/operations/apple-download-provider.md).

The optional Spotify lyrics service is likewise absent from Standard and AIO. Add the pinned
`docker-compose.spotify-lyrics.yml` overlay only when needed, following
[docs/operations/spotify-lyrics-sidecar.md](docs/operations/spotify-lyrics-sidecar.md). Its cookie stays in the host
`.env`; the dashboard migration imports endpoint configuration but never manages Docker or exports provider secrets.

Custom manual deployments may select SQLite explicitly. SQLite bootstrap has an intentional one-shot confirmation requirement, and no automatic Postgres-to-SQLite fallback exists. Follow [docs/operations/storage.md](docs/operations/storage.md) instead of guessing these settings.

### Identity and provider-account ownership

`ALLSTARR_MULTI_USER_MODE` controls stable user provisioning. `Hybrid` is the normal default. `ALLSTARR_PROVIDER_ACCOUNT_MANAGEMENT_MODE` accepts `AdminManaged`, `UserManaged`, or `Hybrid` and controls who can connect provider accounts.

Account scope is part of every decision:

- global accounts are admin-owned;
- user accounts belong to one tenant user;
- library accounts apply only to their configured library roots.

The selected mode does not make credentials interchangeable. Playlist, scrobbling, favorites, and intelligence work still resolve the exact allowed account at execution time.

## Listener And Network Settings

Client traffic is served on proxy port `5274`. The dashboard and admin API use port `5275`.

Standard Compose publishes the admin listener on host loopback and permits only the resolved container gateway needed to cross that mapping. To make it reachable through a LAN or reverse proxy, set all three:

```dotenv
ADMIN_BIND_ADDRESS=0.0.0.0
ADMIN_BIND_ANY_IP=true
ADMIN_TRUSTED_SUBNETS=192.168.1.0/24
```

Replace the example with the network that should be trusted. For a reverse proxy, include its Docker network CIDR. CORS stays disabled unless `CORS_ALLOWED_ORIGINS` contains explicit origins. Keep allowed methods, headers, and credential support as narrow as the client requires.

## Providers And Capabilities

The old `MUSIC_SERVICE` primary-provider switch is retained only for compatibility. New work uses capability-specific provider order and account policy. Streaming, downloading, metadata, playlists, lyrics, enrichment, scrobbling, and recommendations may use different eligible providers.

Provider credentials should be connected through the provider-account UI/API so the durable record contains only an encrypted secret reference. Legacy environment fields for Deezer, Qobuz, Spotify, Last.fm, and ListenBrainz remain documented in `.env.example` where the compatibility path still reads them. Never commit real values.

Current built-in or first-party integrations include provider work for Deezer, Qobuz, SquidWTF, Spotify, Apple MusicKit, lyrics services, Last.fm, ListenBrainz, MusicBrainz, and optional AudioMuse-AI. A feature is ready only when its account, permissions, optional sidecar, and health checks pass. An optional integration may be absent without breaking unrelated startup.

AudioMuse-AI is deployment configuration in the current beta. Set its base URL before recreating the Allstarr
container:

```dotenv
INTELLIGENCE__AUDIOMUSE__URL=https://audiomuse.example.internal
```

Use the real AudioMuse service base URL, not the Jellyfin plugin URL. Allstarr checks `api/health` and calls
`api/sonic_fingerprint/generate`. The Intelligence screen then shows whether the source is ready for the selected
user and library scope. The current adapter does not send an AudioMuse API key or authorization header. Keep the
setting empty if the service requires separate HTTP authentication. Jellyfin users are identified with their
resolved Jellyfin principal. Subsonic/Navidrome recommendation runs use the target credential reference stored on
the exact intelligence policy; that credential is for the backend request body, not AudioMuse HTTP authentication.

Provider extensions are installed packages with declared capability hooks, scopes, network hosts, and secret permissions. Installation verifies the package and uses staged activation and rollback. Allstarr does not add a third-party registry on your behalf. See [docs/extensions/sdk-v1.md](docs/extensions/sdk-v1.md).

## Media, Downloads, And Favorites

`DOWNLOAD_PATH` is the persistent base for permanent downloads. Temporary cache behavior is controlled by `STORAGE_MODE`, `CACHE_DURATION_HOURS`, and the process/container temporary directory. `KEPT_PATH` is mounted separately in standard Compose.

`DOWNLOAD_MODE=Track` downloads only the requested track. `Album` may queue the remaining album after the requested track. Provider quality and rate-limit settings remain provider-specific.

Original libraries are read-only inputs. Favorite-triggered work is off until an exact-scope policy enables it. A policy may queue matching, provider download, managed placement, enrichment, and backend refresh. Unfavorite does not remove source or managed audio. Managed removal is a separate confirmed action.

## Playlists

Playlist links are provider-neutral. A source playlist can be shown virtually or materialized into Jellyfin or a Subsonic-compatible backend. Materialization uses local matches only, keeps source order and metadata where the target supports them, and explains unmatched entries.

The default mode reconciles without destroying the target. Recreate-on-every-run is an explicit per-link option. Links can run manually or as durable scheduled jobs. Neither mode deletes media files.

The Spotify Import compatibility settings remain available for Jellyfin deployments. Direct Spotify access can provide ordering and stronger identifiers. New playlist workflows should use the provider-neutral link and matching records rather than assuming every identity is Spotify-specific.

## Playback, Scrobbling, And Intelligence

Scrobbling is opt-in. Last.fm and ListenBrainz targets are user-scoped and delivered through durable jobs. Local-track scrobbling should generally stay disabled when the backend already has its own scrobbling plugin, which avoids duplicate submissions.

Intelligence is also opt-in at an exact user/backend/library scope. Configure retention and available recommendation sources in the Intelligence UI. The AudioMuse service URL remains deployment-owned as described above; the UI shows its scoped readiness but does not configure that URL. Habit profiles can feed Last.fm, ListenBrainz, MusicBrainz-informed local similarity, Jellyfin InstantMix, local rules, and optional AudioMuse-AI. Generated playlists materialize local matches only; recommendations do not silently trigger downloads. Disabling collection stops new signals, and purge removes retained intelligence data for the scope.

## Cache Settings

The `CACHE_*` variables tune rebuildable data such as search results, playlist images, playlist items, lyrics, genres, metadata, Odesli lookups, proxy images, and transcoded files. Longer TTLs reduce provider traffic but may show stale results. These values do not control durable jobs or Postgres retention.

Valkey is not a backup. It is safe for cache data to rebuild after loss.

## Backup, Restore, And Upgrades

Create a verified database backup before changing the application image. The dashboard can create one, or the stopped host can run:

```bash
docker compose stop allstarr
docker compose run --rm --no-deps allstarr storage backup
```

Copy the dump and its neighboring manifest out of `/app/state/backups`. Back up the media roots and encryption key ring separately. Restore Postgres into a new database, verify it, then switch the configured database name. Do not run a destructive restore against the live database and do not treat a schema down-migration as rollback.

The exact tested commands, checksum rules, SQLite behavior, state transfer, and cutover procedure are in [docs/operations/storage.md](docs/operations/storage.md).

## Validation And Troubleshooting

Before startup:

```bash
docker compose config --quiet
```

After startup:

```bash
docker compose ps
docker compose logs -f allstarr
curl --fail http://127.0.0.1:5274/health/live
curl --fail http://127.0.0.1:5274/health/ready
```

Liveness means the process is running. Readiness additionally covers the selected database, expected schema, key ring, required paths, and required capabilities. If readiness fails, use its reason and the redacted diagnostics rather than deleting state or switching database providers.
