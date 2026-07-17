# Allstarr

[![Build Status](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml/badge.svg?branch=main)](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

Allstarr is a self-hosted music gateway for Jellyfin and Subsonic-compatible clients. Put it in front of Jellyfin or a server such as Navidrome, connect the providers you actually use, and keep one familiar client while Allstarr handles search, matching, streaming, downloads, playlists, lyrics, scrobbling, favorites, and recommendations.

Allstarr does not put songs inside Postgres. Audio stays as normal files in the mounted `downloads`, `kept`, cache, and managed-library folders. Postgres holds control-plane state such as identities, provider accounts, encrypted-secret references, jobs, matches, playlist links, health history, and audits. Valkey accelerates rebuildable cache work and is not the source of truth.

## Before You Install

The `v3.0.0-beta.1` overhaul release is a breaking fresh-install baseline. Do not reuse the old Redis-to-Valkey conversion overlay or expect legacy Redis, mapping, extension, or job state to import automatically. Keep the old stack stopped for rollback, attach the existing backend library read-only when practical, and give the separate version 3 deployment its own writable download, kept, cache, and managed-library roots. Never let both versions write the same media roots. Then use the administrator WebUI to preview and import the safe parts of the old `.env`. Deployment values remain a checklist, personal accounts are reconnected by their owners, and the original file is never replaced. Follow the [legacy environment upgrade procedure](docs/operations/legacy-env-import.md#upgrade-procedure) before cutting clients over.

Standard Compose runs Allstarr, Postgres, and Valkey. It exposes one client protocol per deployment because the Jellyfin and Subsonic surfaces both own catch-all routes. Choose `Jellyfin` or `Subsonic`; a Subsonic deployment can use Navidrome as its backend.

## Quick Start

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
cp .env.example .env
mkdir -p secrets downloads kept
umask 077
openssl rand -base64 32 > secrets/postgres-password.txt
key="$(openssl rand -base64 32)"
printf '{"activeKeyId":"key-1","keys":{"key-1":"%s"}}\n' "$key" > secrets/allstarr-keyring.json
unset key
chmod 600 .env secrets/postgres-password.txt secrets/allstarr-keyring.json
```

Edit `.env`. At minimum, select `BACKEND_TYPE`, set the matching backend URL, and review the image tag and mounted paths. Jellyfin server-side library operations also need its API key and user ID.

```bash
docker compose config --quiet
docker compose pull
docker compose up -d
docker compose ps
curl --fail http://127.0.0.1:5274/health/ready
```

The standard stack is the smaller, recommended default. The AIO override mounts the checksum-locked offline
first-party package bundle, but it does not force optional provider sidecars on anyone:

```bash
docker compose -f docker-compose.yml -f docker-compose.aio.yml up -d
```

The bundle lock is still authoritative. A bundled package marked blocked is not staged or activated merely because
the AIO files are mounted.

Apple downloads are optional and are not bundled with Standard or AIO. Run a compatible Apple provider gateway
separately, then give Allstarr its URL through the dashboard or `APPLE_DOWNLOAD_URL`. The URL must point to the
gateway API, not directly to wrapper-v2. Removing that URL disables Apple download routes without changing Postgres
or media volumes. See [Apple download provider setup](docs/operations/apple-download-provider.md).

Spotify lyrics are optional too. To run the pinned private-network sidecar, add
`docker-compose.spotify-lyrics.yml` to the Compose command and follow the
[Spotify lyrics sidecar guide](docs/operations/spotify-lyrics-sidecar.md). Importing an old `.env` can restore the
endpoint URL, but it cannot start the sidecar or pass a cookie to it.

Client traffic uses `http://localhost:5274`. The separate dashboard is on `http://localhost:5275`. Standard Compose publishes the dashboard on host loopback and only trusts the container gateway needed to cross that mapping. LAN or reverse-proxy access requires `ADMIN_BIND_ADDRESS=0.0.0.0`, `ADMIN_BIND_ANY_IP=true`, and an explicit `ADMIN_TRUSTED_SUBNETS` CIDR. Please keep it behind a private network, VPN, or authenticated access proxy. This software has meaningful access to your media server and provider accounts.

The complete install, backup, restore, and rollback instructions live in [the storage runbook](docs/operations/storage.md). Configuration keys are explained in [CONFIGURATION.md](CONFIGURATION.md).

## What It Does

- Proxies either the Jellyfin or Subsonic/OpenSubsonic surface while preserving native backend authentication and normal pass-through behavior.
- Merges local results with policy-eligible metadata providers and streams or downloads through separately selected capability routes.
- Keeps every real media file in accessible folders. Managed files have ownership, checksum, placement, and job records so Allstarr knows what it is allowed to change.
- Matches one real recording to local copies and multiple provider identities. Matching is provider-neutral and records why a link was accepted or rejected.
- Imports provider playlists as virtual views or materializes exact local matches into Jellyfin or Navidrome/Subsonic. Materialization preserves order, reuses existing tracks, supports reconcile or explicit recreate mode, and does not download unmatched entries.
- Runs long work as durable, inspectable jobs with retries, leases, cancellation, idempotency, and visible failure state.
- Supports opt-in favorite workflows for download, tagging, managed placement, and backend refresh. Unfavorite does not delete music.
- Collects opt-in listening signals and can build explained playlists from Jellyfin InstantMix, Last.fm similarity, ListenBrainz collaborative filtering, MusicBrainz-enriched local relationships, local rules, and optional AudioMuse-AI.
- Scrobbles to Last.fm and ListenBrainz through durable delivery checkpoints.
- Installs verified provider extensions through the provider SDK permission and lifecycle boundary. No third-party registry is added automatically.

Provider availability depends on the configured accounts, optional sidecars, permissions, and health. Missing optional services reduce capability instead of taking the whole application down.

## Storage At A Glance

| Location | Purpose | Authoritative? |
| --- | --- | --- |
| Postgres | Users, accounts, secret references, jobs, matches, playlists, intelligence, health, audits | Yes, for application state |
| Valkey | Search, metadata, lyrics, image, and other acceleration caches | No |
| `downloads` / `kept` / managed roots | Playable audio and related files | Yes, for media |
| `/app/state/backups` | Verified database backup artifacts and manifests | Copy these off the host |
| key-ring file | Keys used to open encrypted application secrets | Yes, back it up separately |

A database backup does not contain your songs or encryption key ring. Back up those separately.

## Clients And Backends

Allstarr supports Jellyfin clients and Subsonic/OpenSubsonic clients through the selected deployment surface. Client behavior varies, especially around search, offline indexing, playlists, and lyrics. See [CLIENTS.md](CLIENTS.md) for the tested list and reporting checklist.

## Documentation

- [Architecture](ARCHITECTURE.md)
- [Configuration](CONFIGURATION.md)
- [Client compatibility](CLIENTS.md)
- [Storage operations](docs/operations/storage.md)
- [Extension SDK](docs/extensions/sdk-v1.md)
- [Contributing](CONTRIBUTING.md)
- [Implementation charter and phase history](OVERHAUL.md)

## Why “Allstarr”?

The goal is to bring the useful parts of different music services into one library experience and let every provider be good at the part it actually does well.

## License

Allstarr is licensed under [GPL-3.0](LICENSE).
