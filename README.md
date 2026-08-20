# Allstarr

[![Build Status](https://github.com/SoPat712/allstarr/actions/workflows/ci.yml/badge.svg)](https://github.com/SoPat712/allstarr/actions/workflows/ci.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

**Your music, connected.**

Allstarr is a self-hosted music gateway for Jellyfin and Subsonic/OpenSubsonic clients. It sits in front of an existing media server, preserves normal local-library behavior, and adds provider-neutral search, matching, playback, playlists, lyrics, scrobbling, listening history, and discovery.

> **Beta status:** `3.1.0-beta.1` is a breaking fresh-install baseline intended for testing. Keep the previous deployment stopped and available for rollback. Do not let two Allstarr versions write the same cache, download, kept, or managed-library paths.

## What Allstarr owns

- PostgreSQL stores users, encrypted account references, jobs, matches, playlist state, intelligence data, health, and audit records.
- Audio and artwork remain ordinary files in mounted cache, download, kept, and managed-library folders.
- The encryption key ring remains a separate file that must be backed up with the database.
- The original backend library is treated as read-only input.

Allstarr does not put songs in PostgreSQL and is not a replacement for Jellyfin, Navidrome, or another media server.

## Quick start

Requirements: Docker with Compose, a Jellyfin or Subsonic/OpenSubsonic backend, and a private network or authenticated access proxy.

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
./allstarr.sh init
```

Review `.env`, choose `BACKEND_TYPE`, and confirm the bind addresses and mounted paths. Then start the stack:

```bash
./allstarr.sh up
curl --fail http://127.0.0.1:5274/health/ready
```

Open the dashboard at `http://localhost:5275`. Sign in with the selected backend, complete onboarding, choose the music library, and connect only the services you use. Music clients connect to `http://localhost:5274`.

The dashboard binds to loopback by default. LAN or reverse-proxy access requires an explicit trusted-network policy; see [configuration](docs/operations/configuration.md). Keep Allstarr behind a private network, VPN, or authenticated proxy because it can access media-server and provider accounts.

Read the [user guide](docs/user-guide.md) for the dashboard map, setup order, listening-history imports, playlist modes, matching, cache, and Intelligence.

## Upgrade or recover

`allstarr.sh` remembers enabled optional profiles, validates Compose, protects generated secrets, and never deletes volumes during normal operation.

```bash
./allstarr.sh upgrade
./allstarr.sh restore /path/to/allstarr-upgrade-….tar.gz --confirm-replace
```

`upgrade` creates a portable state export before updating. The export includes PostgreSQL state, configuration, key-ring material, provider profiles, mappings, playlist state, and durable work. Downloaded and kept music remain in their mounted folders and need their own backup policy.

Beta testers and contributors can run the checked-out source instead of a published image:

```bash
./allstarr.sh mode source
./allstarr.sh up
```

Later source updates use `./allstarr.sh update`; the command requires a clean tracked tree, fast-forwards the current branch, rebuilds, and recreates enabled services. See [deployment profiles](docs/operations/deployment-profiles.md) and the [storage runbook](docs/operations/storage.md) before production use.

## Product map

- **Home** shows current playback, listeners, health, storage, work, and recent activity.
- **Library** owns provider playlists, match review, cached audio, and kept audio.
- **Intelligence** owns listening history, imports, recommendations, automation, and the built-in AudioMuse-AI connection.
- **Integrations** owns Services, encrypted Accounts, extension packages, health, and provider Routing.
- **Activity** explains completed and failed work with correlation details.
- **Settings** owns deployment-level behavior, matching, playback, cache policy, maintenance, backup, and recovery.

## Capabilities

- Presents one selected Jellyfin or Subsonic/OpenSubsonic surface while relaying native backend behavior.
- Merges local results with configured metadata and playable providers.
- Matches one recording to a local item and multiple provider identities with reviewable evidence.
- Projects provider playlists as virtual views or materializes exact local matches into the backend without silently downloading unresolved entries.
- Routes streaming, download, lyrics, and artwork through typed, account-aware capabilities.
- Runs imports, matching, downloads, playlist changes, scrobbling, and other long work as durable inspectable jobs.
- Supports opt-in listening history and imports from Spotify Extended Streaming History, Last.fm, ListenBrainz, Koito, and Maloja exports.
- Builds explained recommendations from enabled sources and an optional self-hosted AudioMuse-AI server configured inside Intelligence.
- Installs verified third-party provider extensions through an explicit registry, permission review, staged activation, and rollback boundary.

Provider availability depends on connected accounts, optional sidecars, permissions, and health. Missing optional services reduce only the affected capability.

## Optional services

Spotify lyrics and Apple/GAMDL download support are explicit Compose profiles, not default dependencies.

```bash
./allstarr.sh enable spotify-lyrics
./allstarr.sh install-apple x86_64
./allstarr.sh up
```

The Apple profile requires a legally obtained compatible APK/APKM supplied by the operator. Allstarr does not distribute Apple binaries. Follow the [Apple provider](docs/operations/apple-download-provider.md) and [Spotify lyrics](docs/operations/spotify-lyrics-sidecar.md) guides.

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
| Guide a coding agent | [Agent guide](AGENTS.md) |

The complete index is in [docs/README.md](docs/README.md).

## License

Allstarr is licensed under [GPL-3.0](LICENSE).
