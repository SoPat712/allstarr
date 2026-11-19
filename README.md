# Allstarr

[![Build Status](https://github.com/SoPat712/allstarr/actions/workflows/ci.yml/badge.svg)](https://github.com/SoPat712/allstarr/actions/workflows/ci.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

Allstarr is a self-hosted music server that sits in front of your Jellyfin or Subsonic/OpenSubsonic server. Your music apps connect to Allstarr instead of the media server. Your own library keeps working as before, and Allstarr adds music services, playlists, mixes, lyrics, scrobbling and listening history on top.

Each installation fronts one server type, Jellyfin or Subsonic, and serves one household. Every music service is optional. With none connected, your apps behave as they do against the media server. Allstarr is not a replacement for Jellyfin, Navidrome or another media server.

```text
music apps ──► Allstarr (port 5274) ──► Jellyfin or Subsonic server
                    │
                    └──► music services, lyrics, scrobbling

dashboard: http://localhost:5275
```

> **Beta:** `3.1.0-beta.1` is a beta for testing. Back up before you upgrade, and never run two Allstarr versions against the same data or media folders.

What it does:

- Connect music services such as Spotify, Deezer, Qobuz and Apple Music with your own accounts, or install extensions. Search results combine your library with the services that can search and play.
- Each song is matched to your library and to the services that have it, with evidence you can review and correct.
- Provider playlists can be imported once or kept in sync. You can keep downloaded copies of their songs.
- Lyrics come from many sources, including word-timed lyrics.
- Plays can be scrobbled to Last.fm and ListenBrainz, and kept for personal stats and mixes.
- Each listener can set each service to On, Manual only or Off for themselves.
- Imports, matching, downloads, playlist changes and other long work run as jobs you can follow in Activity.
- Third-party extensions are installed only after checksum verification and a permission review.

Allstarr keeps its database, encryption key ring and the music it saves in one data folder. It treats your media server's files as read-only.

## Requirements

- Docker with the Compose plugin, on a 64-bit x86 or ARM host.
- A Jellyfin server, or a Subsonic or OpenSubsonic server such as Navidrome, with an administrator account.
- A private network, VPN or authenticated proxy. Allstarr holds access to your media server and service accounts, so don't expose it directly to the internet.

## Quick start

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
docker compose up -d
```

This starts one container. No `.env` file or setup script is needed. Allstarr keeps its data in `./data`.

Open the dashboard at `http://localhost:5275`. It only listens on the host's loopback address. On a headless server, open an SSH tunnel from your own computer and then browse to `http://localhost:5275`:

```bash
ssh -L 5275:127.0.0.1:5275 you@your-server
```

To open the dashboard from other machines on your network instead, see [private dashboard access](docs/operations/webui-access.md).

## First-run setup

1. **Connect your media server.** Choose Jellyfin or Subsonic and enter an address that works from inside the container. For a server on the same host, use `http://host.docker.internal:8096` for Jellyfin or `http://host.docker.internal:4533` for Navidrome. `localhost` inside the container means Allstarr itself. Sign in with a media-server administrator account. Allstarr verifies it, saves the connection, discards the password and restarts.
2. **Sign in and follow the setup guide.** Everyone signs in with their usual media-server account. Administrators come from the media server. The setup guide walks through sources and household settings. Reopen it any time from **Settings → Backups and health → Setup guide**. Nothing is reset.
3. **Connect services.** Add service accounts in **Sources → Accounts**, and extensions in **Sources → Services → Add extension**. An account is **Shared** (used for the whole household) or **Personal** (one listener's). Listeners can connect their own accounts unless an administrator turns that off in **Settings → Music sources**. Source order for search, downloads, streaming, playlists and lyrics is also in **Settings → Music sources**.
4. **Set up apps.** In **Settings → Apps**, set the **Music-client address** your listeners will use, for example `http://192.168.1.10:5274` or your HTTPS hostname. Apps built for Allstarr's own client API need **Client API for apps** turned on there. It is off by default.

The [user guide](docs/user-guide.md) covers the dashboard in detail.

## Connect music apps

Point your app at Allstarr's address, `http://<allstarr-host>:5274`, instead of your media server. Sign in with your usual media-server username and password. Use the Jellyfin or Subsonic option in the app, whichever matches your server.

The dashboard shows the address and your username under **Connect a music app** in your settings. The [user guide](docs/user-guide.md#connect-your-music-app) has the steps for each of these apps:

- Jellyfin: Finer Player, Musiver, Feishin, Finamp, Jellyfin Web and Symfonium.
- Subsonic: Feishin, Tempo and Symfonium.

See [client compatibility](docs/operations/client-compatibility.md) for known limits. Listeners can send problems to their administrator from **Report a problem**.

## Optional extras

- **Apple Music playback and downloads:** upload a legally obtained Apple Music APK or APKM in **Sources → Services → Apple Music** (**Sign in to Apple Music → Advanced setup**), then run `./allstarr.sh enable apple`. Allstarr does not distribute Apple code. See [Apple download provider](docs/operations/apple-download-provider.md).
- **Automatic extension sign-in renewal:** `./allstarr.sh enable sessions` adds an optional Chromium service that renews approved `api.zarz.moe` extension sign-ins when they expire. See [session verifier](sidecars/session-verifier/README.md).
- **Adding songs to your Jellyfin library:** mount your music folder into the container at the same path Jellyfin uses, then choose it in **Settings → Library → Where added songs go**. See [storage operations](docs/operations/storage.md).
- **Mixes from sound:** connect AudioMuse in **Settings → Mixes and discovery**. See [AudioMuse](docs/operations/audiomuse.md).
- **Alerts:** **Settings → Alerts** can send problems to ntfy, Discord or a webhook.
- **HTTPS, remote apps and single sign-on:** see [HTTPS, private dashboard access and SSO](docs/operations/webui-access.md).

## Update and back up

Back up first. **Settings → Backups and health** makes verified backups of your settings, accounts and history, and restores them. These backups don't include music files. For a complete copy, stop Allstarr and copy the whole data folder. Use `sudo cp -a` if Docker created the files as root.

```bash
docker compose stop allstarr
cp -a ./data ../allstarr-data-backup
docker compose up -d --no-deps allstarr
```

Then update:

```bash
./allstarr.sh update
```

This pulls the image named in Compose and restarts Allstarr with any optional extras you enabled. To move to a different release, set `ALLSTARR_IMAGE` in `.env` first. Run `./allstarr.sh help` for the other commands. See [storage operations](docs/operations/storage.md) for restore and rollback.

## Deployment settings

Most settings live in the dashboard. A few values are needed before Allstarr starts, and these go in an optional `.env` file next to `docker-compose.yml`. Copy `.env.example` to start. Recreate the container after a change with `./allstarr.sh up` or `docker compose up -d`.

| Variable | Default | Purpose |
| --- | --- | --- |
| `ALLSTARR_IMAGE` | `ghcr.io/sopat712/allstarr:3.1.0-beta.1` | Image to run. |
| `ALLSTARR_DATA_PATH` | `./data` | Host folder or named volume for the data folder. |
| `ALLSTARR_RELEASE_PROFILE` | `core` | Feature set. Leave it at `core`. |
| `PROXY_BIND_ADDRESS`, `PROXY_PORT` | `0.0.0.0`, `5274` | Where music apps connect. |
| `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` | `127.0.0.1`, `5275` | Where the dashboard listens on the host. |
| `ADMIN_BIND_ANY_IP`, `ADMIN_TRUSTED_SUBNETS` | `false`, empty | Allow the dashboard from other machines on the networks you list. |
| `ADMIN_BASE_PATH` | empty | Serve the dashboard under a path such as `/allstarr` behind a reverse proxy. |
| `BACKEND_TYPE`, `JELLYFIN_URL`, `SUBSONIC_URL` | `Jellyfin`, empty, empty | Optional first-run defaults. The connection saved in the dashboard wins. |
| `SESSION_VERIFIER_URL` | empty | Address of the sign-in renewal service. `./allstarr.sh enable sessions` sets it for you. |

See [configuration](docs/operations/configuration.md) for the rest.

## Documentation

| Need | Start here |
| --- | --- |
| Use the dashboard | [User guide](docs/user-guide.md) |
| Configure Allstarr | [Configuration](docs/operations/configuration.md) |
| Install, update and add optional services | [Deployment profiles](docs/operations/deployment-profiles.md) |
| Back up, restore or move | [Storage operations](docs/operations/storage.md) |
| Use HTTPS, remote access or SSO | [HTTPS, private dashboard access and SSO](docs/operations/webui-access.md) |
| Check a music app | [Client compatibility](docs/operations/client-compatibility.md) |
| Set up lyrics | [Lyrics sources](docs/operations/lyrics.md) |
| Fix scrobbling | [Scrobble delivery and recovery](docs/operations/scrobbling.md) |
| Build an app | [Client API](docs/api/README.md) |
| Build an extension | [Extension SDK](docs/extensions/sdk-v1.md) |

The complete index is in [docs/README.md](docs/README.md).

## Upgrading from version 2

Keep your existing Compose file, `.env` and folders, and follow the version 2 upgrade steps in [deployment profiles](docs/operations/deployment-profiles.md) and the [legacy `.env` import](docs/operations/legacy-env-import.md) guide.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building from source, tests and the code layout.

## License

Allstarr is licensed under [GPL-3.0](LICENSE). See the
[upstream reuse notices](docs/architecture/reference-projects.md#deezer-implementation-notice)
for adapted provider code.
