# Allstarr

[![Build Status](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml/badge.svg?branch=main)](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

Allstarr is a self-hosted music gateway for Jellyfin and Subsonic-compatible clients. Put it in front of Jellyfin or a server such as Navidrome, connect the providers you actually use, and keep one familiar client while Allstarr handles search, matching, streaming, downloads, playlists, lyrics, scrobbling, favorites, and recommendations.

Allstarr does not put songs inside Postgres. Audio stays as normal files in the mounted `downloads`, `kept`, cache, and managed-library folders. Postgres holds control-plane state and bounded disposable metadata cache entries; artwork and other media cache payloads stay on bounded disk.

## Before You Install

The `v3.1.0-beta.1` overhaul release is a breaking fresh-install baseline. Do not reuse the old Redis-to-Valkey conversion overlay or expect legacy Redis, mapping, extension, or job state to import automatically. Keep the old stack stopped for rollback, attach the existing backend library read-only when practical, and give the separate version 3 deployment its own writable download, kept, cache, and managed-library roots. Never let both versions write the same media roots. Then use the administrator WebUI to preview and import the safe parts of the old `.env`. Deployment values remain a checklist, personal accounts are reconnected by their owners, and the original file is never replaced. Follow the [legacy environment upgrade procedure](docs/operations/legacy-env-import.md#supported-workflow) before cutting clients over.

Standard Compose runs Allstarr and Postgres. It exposes one client protocol per deployment because the Jellyfin and Subsonic surfaces both own catch-all routes. Choose `Jellyfin` or `Subsonic`; a Subsonic deployment can use Navidrome as its backend.

## Quick Start

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
./allstarr.sh init
```

Edit `.env`. Select `BACKEND_TYPE` and review the image tag, listeners, security opt-ins, and mounted paths. Complete the backend URL, credentials, library, and user mapping through onboarding after startup.

```bash
./allstarr.sh up
curl --fail http://127.0.0.1:5274/health/ready
```

`allstarr.sh` remembers optional profiles, validates the merged Compose model, creates secrets with private file
permissions, and never deletes volumes. For a normal upgrade, run `./allstarr.sh upgrade`; it briefly stops the
stack, creates a private portable export under `allstarr-backups/`, then updates and restarts the saved profile.
The export includes configuration, the encryption keyring, provider profiles, Postgres, mappings, playlist
caches, and durable application state. Downloaded and kept music stay in their existing host-mounted folders.
To move or recover an installation, initialize the destination and run
`./allstarr.sh restore /path/to/allstarr-upgrade-….tar.gz --confirm-replace`. Restore validates the archive,
creates a rollback backup of the destination, replaces its saved state, and restarts it if it was running.

The default `release` mode runs reviewed images. Beta testers and contributors who want the checked-out commit can
run `./allstarr.sh mode source`, then `./allstarr.sh up`. For later source updates, run
`./allstarr.sh update`; it refuses tracked local changes, fast-forwards the current tracked branch, rebuilds, and
recreates the services. The same volumes and optional-provider profiles remain attached in either mode.

Apple downloads are optional and are not part of the default installation. The Apple profile builds the repository's
small gateway with GAMDL 3.8.2 and the official wrapper-v2 0.0.2 source. Allstarr never supplies Apple binaries, so
the operator provides one legally obtained compatible APK/APKM through Sources > Apple download, then runs
`./allstarr.sh install-apple x86_64`. Removing the profile disables Apple download routes without
changing Postgres, media, or the persistent wrapper login session. See
[Apple download provider setup](docs/operations/apple-download-provider.md).

Spotify lyrics are optional too. To run the pinned private-network sidecar, add
it with `./allstarr.sh enable spotify-lyrics`, then run `./allstarr.sh up`. Follow the
[Spotify lyrics sidecar guide](docs/operations/spotify-lyrics-sidecar.md). Importing an old `.env` can restore the
endpoint URL, but it cannot start the sidecar or pass a cookie to it.

Client traffic uses `http://localhost:5274`. The separate dashboard is on `http://localhost:5275`. Standard Compose publishes the dashboard on host loopback and only trusts the container gateway needed to cross that mapping. LAN or reverse-proxy access requires `ADMIN_BIND_ADDRESS=0.0.0.0`, `ADMIN_BIND_ANY_IP=true`, and an explicit `ADMIN_TRUSTED_SUBNETS` CIDR. Please keep it behind a private network, VPN, or authenticated access proxy. This software has meaningful access to your media server and provider accounts.

The complete install, backup, restore, and rollback instructions live in [the storage runbook](docs/operations/storage.md). Configuration keys are explained in [the configuration guide](docs/operations/configuration.md).

## What It Does

- Proxies either the Jellyfin or Subsonic/OpenSubsonic surface while preserving native backend authentication and normal pass-through behavior.
- Merges local results with policy-eligible metadata providers and streams or downloads through separately selected capability routes.
- Keeps every real media file in accessible folders. Managed files have ownership, checksum, placement, and job records so Allstarr knows what it is allowed to change.
- Matches one real recording to local copies and multiple provider identities. Matching is provider-neutral and records why a link was accepted or rejected.
- Imports provider playlists as virtual views or materializes exact local matches into Jellyfin or Navidrome/Subsonic. Materialization preserves order, reuses existing tracks, supports reconcile or explicit recreate mode, and does not download unmatched entries.
- Runs long work as durable, inspectable jobs with retries, leases, cancellation, idempotency, and visible failure state.
- Supports opt-in favorite workflows for download, tagging, managed placement, and backend refresh. Unfavorite does not delete music.
- Collects opt-in listening signals and can build explained playlists from Jellyfin InstantMix, Last.fm similarity, ListenBrainz collaborative filtering, MusicBrainz-enriched local relationships, local rules, and an optional AudioMuse-AI extension service.
- Scrobbles to Last.fm and ListenBrainz through durable delivery checkpoints.
- Installs verified provider extensions through the provider SDK permission and lifecycle boundary. No third-party registry is added automatically.

Provider availability depends on the configured accounts, optional sidecars, permissions, and health. Missing optional services reduce capability instead of taking the whole application down.

## Storage At A Glance

| Location | Purpose | Authoritative? |
| --- | --- | --- |
| Postgres | Users, accounts, secret references, jobs, matches, playlists, intelligence, health, audits | Yes, for application state |
| PostgreSQL cache table and bounded `/app/cache` media tier | Rebuildable metadata, search, lyrics, and artwork acceleration | No |
| `downloads` / `kept` / managed roots | Playable audio and related files | Yes, for media |
| `/app/state/backups` | Verified database backup artifacts and manifests | Copy these off the host |
| key-ring file | Keys used to open encrypted application secrets | Yes, back it up separately |

A database backup does not contain your songs or encryption key ring. Back up those separately.

## Clients And Backends

Allstarr supports Jellyfin clients and Subsonic/OpenSubsonic clients through the selected deployment surface. Client behavior varies, especially around search, offline indexing, playlists, and lyrics. See [client compatibility](docs/operations/client-compatibility.md) for the tested list and reporting checklist.

## Documentation

- [Architecture](docs/architecture/overview.md)
- [Configuration](docs/operations/configuration.md)
- [Client compatibility](docs/operations/client-compatibility.md)
- [Storage operations](docs/operations/storage.md)
- [Deployment profiles and optional services](docs/operations/deployment-profiles.md)
- [Extension SDK](docs/extensions/sdk-v1.md)
- [Contributing](CONTRIBUTING.md)

## Why “Allstarr”?

The goal is to bring the useful parts of different music services into one library experience and let every provider be good at the part it actually does well.

## License

Allstarr is licensed under [GPL-3.0](LICENSE).
