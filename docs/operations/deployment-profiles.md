# Deployment profiles

Allstarr runs as one core container. Two optional profiles add containers: `apple` for Apple Music playback and downloads, and `sessions` for automatic extension sign-in renewal. `./allstarr.sh` saves which profiles you use in `.allstarr-profiles` and starts them with the core.

## Install

Install Docker with Compose, get this repository, and run:

```bash
docker compose up -d
```

This starts one Allstarr container from the pinned application image. Its only persistent mount is `./data:/app/data`. No `.env` file, source build or optional service is needed. Music apps use port 5274. The dashboard is at `http://localhost:5275`, bound to the host's loopback address.

On a server without a desktop, reach the dashboard through an SSH tunnel, for example `ssh -L 5275:127.0.0.1:5275 you@server`, then open `http://localhost:5275` on your own computer. To open it from your network instead, see [keep administration private](webui-access.md#keep-administration-private).

Open the dashboard, choose Jellyfin or Subsonic / OpenSubsonic, and verify a media-server administrator. Use an address the container can reach; `localhost` inside the container is Allstarr itself. The connection is saved in the database and the password is discarded. Allstarr restarts, then offers sign-in and the setup guide. The guide remembers reviewed and skipped steps across browsers; reopen it from **Settings → Backups and health → Setup guide** without resetting anything. Settings and accounts live in the dashboard; see [configuration](configuration.md).

## Update

Create and download a backup in **Settings → Backups and health**, and keep the previous image and a stopped copy of the data folder. Then run:

```bash
./allstarr.sh update
```

The command checks the deployment files, pulls the configured Allstarr image and recreates the saved profiles. A failed pull or optional service build stops startup. It does not change Git files or delete volumes. To move to a different pinned release, set `ALLSTARR_IMAGE` in `.env` first.

Other commands:

- `./allstarr.sh up` starts the saved profiles without pulling the application image.
- `./allstarr.sh status` lists their containers.
- `./allstarr.sh logs [service]` follows their logs.
- `./allstarr.sh down` stops the saved deployment without deleting its volumes.

An earlier installation may keep its data in the project's `allstarr-data` volume. When `ALLSTARR_DATA_PATH` is unset and `./data` is missing or empty, `up` and `update` keep using that volume: the script adds `ALLSTARR_DATA_PATH=allstarr-data` to `.env` and reports it. Keep the same Compose project name, including any `COMPOSE_PROJECT_NAME`. Nothing is copied or moved. If both the volume and `./data` hold data, startup stops until you choose `ALLSTARR_DATA_PATH=./data` or `ALLSTARR_DATA_PATH=allstarr-data`. That check needs the configured Allstarr image to be available locally. Plain `docker compose up` does not run this check.

## Optional Apple downloads

The `apple` profile adds Allstarr's Apple gateway and the wrapper it uses. Neither publishes a host port.

Get a supported Apple Music for Android APK or APKM legally. In **Sources → Services → Apple Music**, choose **Sign in to Apple Music**, open **Advanced setup** and upload the package. Then run:

```bash
./allstarr.sh enable apple
```

The architecture is detected automatically; pass `x86_64` or `arm64-v8a` when needed. Uploads are stored in `data/apple-upload` with the default layout. The script finds a custom bind-mounted data path from Compose, and also accepts the older `.apple-provider/incoming` staging folder. With a named volume or another layout, pass the package path yourself: `./allstarr.sh enable apple /path/to/package.apkm x86_64`.

The command verifies the package and the locked wrapper inputs, builds the local Apple images, saves the profile and starts it. It needs Git, jq and unzip. `./allstarr.sh prepare-apple` verifies and builds without starting. `install-apple` is an older name for `enable apple`.

Then set **Optional download gateway URL** to `http://apple-gateway:8000` on the Apple Music service, sign in and test. See [Apple downloads](apple-download-provider.md).

```bash
./allstarr.sh disable apple
```

This stops and removes the two Apple containers and forgets the saved profile. It keeps gateway data, wrapper sessions, Allstarr's records and media. Plain Compose does not read `.allstarr-profiles`; pass `--profile apple` yourself when you run Compose by hand. Neither way needs the Docker socket mounted in Allstarr.

Spotify lyrics run inside Allstarr with a connected Spotify account and need no profile.

## Optional extension sign-in verification

`./allstarr.sh enable sessions` builds an optional Chromium service and gives Allstarr its private address. It renews approved `api.zarz.moe` connections after they expire or before a capped session runs out. The saved profile works with release and source deployments. `./allstarr.sh disable sessions` returns to manual verification without deleting connections. The service publishes no host port and stores no provider sessions. See the [session verifier](../../sidecars/session-verifier/README.md) for its limits, retries and direct Compose setup.

## Upgrade from version 2

Keep the existing Compose file, `.env`, mount paths and project name. Do not replace them with the new default layout during the upgrade. Version 3 honours the old backend address and dashboard network settings. When there is no data mount, it keeps the database and key ring in `/app/cache/allstarr-v3` inside the existing persistent cache mount. `/health` and ordinary music-app requests keep working while setup is pending.

Before upgrading, record the previous image tag and image ID, and keep that image locally or in your registry. Stop Allstarr and copy its configuration, cache, download and kept folders, plus any other persistent paths. Keep old optional-service state too if you may need it for rollback. Never run both versions against the same folders.

When the image in your existing Compose file points to the release you want:

```bash
docker compose stop allstarr
# Make and verify the stopped-folder backup before continuing.
docker compose pull allstarr
docker compose up -d --no-deps allstarr
```

These commands use the image already named in your Compose file. A pinned v2 tag stays pinned; `latest` moves only when a release is published.

Sign in and open the setup guide's **Review previous settings** step to preview and import the old `.env`; see [legacy import](legacy-env-import.md). The file itself is not changed. Existing saved settings and accounts take precedence. Original kept and downloaded audio stays in its mounts, and new songs go into those mounts too, so media-server library paths keep working. Valkey cache and old matching state are not imported. Check rejected or unsupported settings and test playback before you remove anything from the old installation. **Finish for now** skips the remaining optional steps without erasing them. Check the import jobs and ordinary playback before you call the upgrade done.

Old Valkey and Spotify-lyrics services may stay in an unchanged v2 Compose file; the new runtime does not use them. Leave that tidy-up until the upgrade is verified. Removing a service that is still named in `depends_on` needs a deliberate Compose edit.

An unchanged v2 Compose file has no Apple services. Finish and verify the core upgrade first; adding Apple is a separate change using the current helper and profile definitions. Keep the original cache, kept and download container paths when you do.

## Backup, restore and rollback

Use **Settings → Backups and health** for verified database-and-key-ring archives. The archive excludes media, extension packages, cache, deployment configuration, and optional provider sessions. Stop Allstarr before you copy the complete data folder, and include external mounts and optional provider volumes separately. [Storage operations](storage.md) has the restore steps and how copied folders are checked.

To roll back, stop the new container, restore the complete pre-upgrade folders to their original mounts, and select the previous image. For a new-layout installation, set `ALLSTARR_IMAGE` to its saved tag or digest in `.env`, then run `docker compose up -d --no-deps --pull never allstarr`. Keep the failed upgrade's folders separately for inspection. Never run an older image against a database a newer version has migrated.

For an unchanged v2 layout, use its untouched deployment files and original folders. If its image was a moving tag, a temporary Compose override can select the saved image without editing the original file:

```yaml
# rollback.yml
services:
  allstarr:
    image: ghcr.io/sopat712/allstarr@sha256:REPLACE_WITH_SAVED_DIGEST
```

```bash
docker compose -f docker-compose.yml -f rollback.yml up -d --no-deps --pull never allstarr
```

Use the same Compose project name and all original mounts. Check music-app playback, dashboard access and accounts before you let listeners back in. Never use `down -v` during recovery.

## Source development

Source builds use the developer script under `tools/`:

```bash
./tools/develop.sh mode source
./tools/develop.sh up
./tools/develop.sh update
```

The script adds `tools/docker-compose.source.yml` to the base file. `up` builds the current checkout. `update` needs a clean tracked tree on a tracked branch; it fast-forwards and builds before recreating. It supports Git worktrees and does not prune shared images or build caches. `./tools/develop.sh mode release` returns to published images. If an older installation saved source mode, `./allstarr.sh` stops and points you to the developer script.
