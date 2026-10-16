# Allstarr

[![Build Status](https://github.com/SoPat712/allstarr/actions/workflows/ci.yml/badge.svg)](https://github.com/SoPat712/allstarr/actions/workflows/ci.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

**Your music, connected.**

Allstarr is a self-hosted music gateway for Jellyfin and Subsonic/OpenSubsonic clients. It sits in front of an existing media server, preserves normal local-library behavior, and adds provider-neutral search, matching, playback, playlists, lyrics, scrobbling, listening history, and discovery.

> **Beta status:** `3.1.0-beta.1` is intended for testing. In-place upgrades from version 2 are supported by the runtime, but release qualification is still in progress. Save the previous image, configuration and stopped data folders before upgrading. Run one version at a time against any shared media paths.

## What Allstarr owns

- SQLite stores users, encrypted account references, jobs, matches, playlist state, intelligence data, health, and audit records in a local data folder.
- Audio and artwork remain ordinary files in mounted cache, download, kept, and managed-library folders.
- The encryption key ring remains a separate file that must be backed up with the database.
- The original backend library is treated as read-only input.

Allstarr does not put songs in the database and is not a replacement for Jellyfin, Navidrome, or another media server.

## Quick start

Requirements: Docker with Compose, a Jellyfin or Subsonic/OpenSubsonic backend, and a private network or authenticated access proxy.

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
docker compose up -d
```

The default stack is one container. It stores its database, encryption key ring, kept music and cache in `./data`. No `.env` file or host setup script is required to start it. Music clients use port 5274; the dashboard is at `http://localhost:5275` and binds to loopback by default.

The backend-selection API is available, but browser setup is still being completed in this beta. For now, create optional deployment defaults with `./allstarr.sh init`, set `BACKEND_TYPE` and either `JELLYFIN_URL` or `SUBSONIC_URL` in `.env`, then run `docker compose up -d` again. Use an address reachable from inside the container, such as `http://host.docker.internal:8096` for a Jellyfin server on the Docker host. Sign in with that media server's administrator account, review Settings → Setup, and connect the services you use.

One deployment serves one household. Backend administrators manage deployment settings and Shared provider accounts; listeners manage their own playlists, Personal accounts, match choices, and listening preferences. Listener account connections are enabled by default and can be disabled by an administrator without removing existing accounts.

The dashboard binds to loopback by default. LAN or reverse-proxy access requires an explicit trusted-network policy; see [configuration](docs/operations/configuration.md). Keep Allstarr behind a private network, VPN, or authenticated proxy because it can access media-server and provider accounts.

Read the [user guide](docs/user-guide.md) for the dashboard map, setup order, playlist modes, matching, cache, and the retained development-only Intelligence workspace.

## Upgrade or recover

An existing version 2 installation can keep its Compose file and `.env` unchanged. When its configured image reference receives version 3, pull and recreate only `allstarr`. Without a data mount, the runtime stores SQLite and its key ring under the existing cache mount at `/app/cache/allstarr-v3`. Native proxying stays available while you review the legacy settings import. The new default Compose file is for new installations; do not replace a working v2 layout during the upgrade. See [deployment and rollback](docs/operations/deployment-profiles.md).

For a normal update, save a backup, then run `./allstarr.sh update`. It pulls the configured application image and preserves enabled Apple services. An unchanged v2 deployment should use its own Compose commands instead.

Settings → Maintenance creates and restores verified database-and-key-ring backups. For a complete offline backup, stop Allstarr and copy the whole data folder, along with any external media roots and optional Apple volumes. Database archives exclude music and optional provider sessions. Copied media recovery is still being completed in this beta; verify playback before retiring an original folder. See [storage operations](docs/operations/storage.md) for backup, restore and rollback instructions.

Source builds use the developer entry point:

```bash
./tools/develop.sh mode source
./tools/develop.sh up
```

Use `./tools/develop.sh update` for later source updates. It requires a clean tracked tree on a tracked branch and fast-forwards before building. Return to published images with `./tools/develop.sh mode release`.

## Product map

- **Home** shows current playback, listeners, health, storage, work, and recent activity.
- **Library** owns provider playlists, match review, cached audio, and kept audio.
- **Integrations** owns Services, encrypted Accounts, extension packages, health, and provider Routing.
- **Activity** explains completed and failed work with correlation details.
- **Settings** owns deployment-level behavior, matching, playback, cache policy, maintenance, backup, and recovery.

Intelligence is deferred from the first release and hidden from navigation. Its development workspace and existing deep links remain available for now; accounts, history, and opt-in background work are unchanged.

## Capabilities

- Presents one selected Jellyfin or Subsonic/OpenSubsonic surface while relaying native backend behavior.
- Merges local results with configured metadata and playable providers.
- Matches one recording to a local item and multiple provider identities with reviewable evidence.
- Imports personal provider playlists once or keeps them linked, with per-user credentials and an explicit option to retain every resolved downloadable song in owner-scoped kept storage.
- Routes streaming, download, lyrics, and artwork through typed, account-aware capabilities.
- Runs imports, matching, downloads, playlist changes, scrobbling, and other long work as durable inspectable jobs.
- Installs verified third-party provider extensions through an explicit registry, permission review, staged activation.

Provider availability depends on connected accounts, optional sidecars, permissions, and health. Missing optional services reduce only the affected capability.

## Optional services

Apple/GAMDL download support uses an optional Compose profile.

```bash
./allstarr.sh enable apple x86_64
```

Spotify lyrics run inside Allstarr using a selected Spotify account. The Apple profile requires a legally obtained compatible APK/APKM supplied by the operator. Allstarr does not distribute Apple binaries. Follow the [Apple provider](docs/operations/apple-download-provider.md) and [Spotify lyrics](docs/operations/spotify-lyrics.md) guides.

## Documentation

| Need | Start here |
| --- | --- |
| Use the dashboard | [User guide](docs/user-guide.md) |
| Install and configure | [Configuration](docs/operations/configuration.md) |
| Back up, restore, or move | [Storage runbook](docs/operations/storage.md) |
| Check a client | [Client compatibility](docs/operations/client-compatibility.md) |
| Understand the system | [Architecture overview](docs/architecture/overview.md) |
| Build an extension | [Extension SDK](docs/extensions/sdk-v1.md) |
| Contribute code | [Contributing](CONTRIBUTING.md) |

The complete index is in [docs/README.md](docs/README.md).

## License

Allstarr is licensed under [GPL-3.0](LICENSE). See the
[upstream reuse notices](docs/architecture/reference-projects.md#deezer-implementation-notice)
for adapted provider code.
