# Deployment

## Install

Install Docker with Compose, obtain this repository, and run:

```bash
docker compose up -d
```

This starts one Allstarr container using the pinned application image. Its only persistent mount is `./data:/app/data`. No `.env` file, source build or optional service is required. Music clients use port 5274. The dashboard listens at `http://localhost:5275`, with the host port bound to loopback.

Open the dashboard, choose Jellyfin or Subsonic / OpenSubsonic, and verify a media-server administrator. Use an address reachable from the container; `localhost` inside the container refers to Allstarr itself. The connection is saved in SQLite and the password is discarded. Allstarr restarts after its response, then the dashboard offers sign-in and the shared setup guide. Reviewed and skipped steps persist across browsers. Reopen it under Settings → Setup without resetting choices. Product settings and encrypted accounts belong in the dashboard. See [configuration](configuration.md) for listener and trusted-network settings.

## Upgrade from version 2

Keep the existing Compose file, `.env`, mount paths and project name. Do not replace them with the new default layout during the upgrade. Version 3 honors legacy backend reachability and administrator network settings. When no explicit data mount exists, it uses `/app/cache/allstarr-v3` inside the existing persistent cache mount for SQLite and the key ring. It keeps `/health` and native proxy requests available while upgrade setup is pending.

Before upgrading, record the previous image tag and immutable image ID. Retain that image locally or in your registry. Stop Allstarr and copy its configuration, cache, download and kept folders, plus any other persistent paths. Preserve old optional-service state separately if you will need it for rollback. Never run both versions against the same folders.

When the image reference in your existing Compose file points to the release you intend to run:

```bash
docker compose stop allstarr
# Make and verify the stopped-folder backup before continuing.
docker compose pull allstarr
docker compose up -d --no-deps allstarr
```

These commands use the image already named in your Compose file. A pinned v2 tag stays pinned; `latest` advances only when a release is published. This beta does not mean that `latest` has already changed.

Sign in and review the legacy `.env` through Settings → Setup before applying its import. The source file stays unchanged. Existing saved settings and account revisions take precedence. Original kept and permanent-download audio stays in its existing mounts. Valkey cache and old matching state are not imported. New managed media stays within the existing media mounts so backend library paths continue to work. Inspect rejected or unsupported settings and test playback before removing anything from the old installation. The guided checklist explains source changes, original-file adoption, playlists, routing, lyrics and optional Apple downloads. Finish for now skips remaining optional steps; it does not erase their configuration. Verify the import jobs and ordinary playback before declaring the upgrade complete.

Old Valkey and Spotify-lyrics services may remain in an unchanged v2 Compose file. The new runtime does not use them. Leave deployment tidy-up until the upgrade is verified; removing a service that is still named in `depends_on` requires a deliberate Compose edit.

## Update a new deployment

Create and download a backup in Settings → Backups, and retain the previous image and a stopped copy of the data folder. Then run:

```bash
./allstarr.sh update
```

The command validates deployment files, pulls the configured Allstarr image and recreates the saved profiles. A failed pull or Apple gateway build prevents startup. It does not change Git files or delete volumes. Changing to a different pinned release requires setting `ALLSTARR_IMAGE` in the optional `.env` first.

`./allstarr.sh up` starts the saved profiles without pulling the application image. `status` lists their containers; `logs [service]` follows their logs. `down` stops the saved deployment without deleting its volumes.

When updating an earlier installation that used the project’s `allstarr-data` volume, `up` and `update` retain that volume automatically if `ALLSTARR_DATA_PATH` is unset and `./data` is missing or empty. The script appends `ALLSTARR_DATA_PATH=allstarr-data` to `.env`, preserving existing settings and permissions, and reports the retained volume. Keep the same Compose project name, including any `COMPOSE_PROJECT_NAME` setting. No files are copied or moved. If both locations contain data, startup stops until you explicitly choose `ALLSTARR_DATA_PATH=./data` or `ALLSTARR_DATA_PATH=allstarr-data`. Checking an existing volume alongside populated `./data` requires the configured Allstarr image to be available locally; a failed check also stops startup and asks for an explicit path. Plain `docker compose up` does not perform this check.

## Optional Apple downloads

The current deployment files put Apple's gateway and wrapper behind the `apple` profile and publish no host ports. An untouched v2 Compose file does not contain these services. Finish and verify its core upgrade first; adding Apple is a separate opt-in deployment change using the current host helper and profile definitions. Preserve the original cache, kept and download container paths when preparing that change. Upload a legally obtained supported Apple Music APK/APKM through Integrations → Services → Apple Music, then run:

```bash
./allstarr.sh enable apple
```

Architecture is detected automatically; pass `x86_64` or `arm64-v8a` when needed. Uploads are stored in `data/apple-upload` with the default layout. The script resolves a custom bind-mounted data path from Compose and also recognizes the older `.apple-provider/incoming` staging folder. With a named volume or a separately managed layout, pass an explicit package path: `./allstarr.sh enable apple /path/to/package.apkm x86_64`.

The command verifies the package and locked wrapper inputs, builds the local Apple images, saves the profile and starts it. Git, jq and unzip are required for preparation. `prepare-apple` verifies and builds without starting; `install-apple` remains a compatibility alias for `enable apple`. The dashboard displays the single `enable apple` host step.

Configure the gateway address as `http://apple-gateway:8000` in the Apple service settings, then complete sign-in and testing in the dashboard. See [Apple downloads](apple-download-provider.md).

```bash
./allstarr.sh disable apple
```

This stops and removes only the two Apple containers and forgets the saved profile. It preserves gateway data, wrapper sessions, application records and media. The script reads `.allstarr-profiles`; plain Compose does not read that file. For manual Compose operation, supply `--profile apple` explicitly when Apple should run. Neither path needs a Docker socket mounted in Allstarr.

Spotify lyrics run inside Allstarr with a selected Spotify account and need no Compose profile.

## Backup, restore and rollback

Use Settings → Backups for verified database-and-key-ring archives. The archive excludes media, extension packages, cache, deployment configuration, and optional provider sessions. Stop Allstarr before you copy the complete data folder, and include external mounts and optional provider volumes separately. Follow [storage operations](storage.md) for exact restore steps and copied-file verification. The shell `init`, `upgrade`, `backup` and `restore` commands are removed: use guided setup, the normal image update, verified database archives and complete stopped-folder copies.

To roll back, stop the new container, restore the complete pre-upgrade folders to their original mounts, and select the previous image. For a new-layout installation, set `ALLSTARR_IMAGE` to its saved tag or digest in `.env`, then run `docker compose up -d --no-deps --pull never allstarr`. Keep the failed upgrade's folders separately for inspection. Do not run an older image against a database migrated by a newer version.

For an unchanged v2 layout, use its untouched deployment configuration and original folders. If its image reference was a moving tag, a temporary Compose override can select the saved immutable image without editing the original file:

```yaml
# rollback.yml
services:
  allstarr:
    image: ghcr.io/sopat712/allstarr@sha256:REPLACE_WITH_SAVED_DIGEST
```

```bash
docker compose -f docker-compose.yml -f rollback.yml up -d --no-deps --pull never allstarr
```

Use the same Compose project name and all original mounts. Verify native playback, administrator access and accounts before resuming normal traffic. Never use `down -v` during recovery.

## Source development

Source mode lives under `tools/`:

```bash
./tools/develop.sh mode source
./tools/develop.sh up
./tools/develop.sh update
```

The developer script adds `tools/docker-compose.source.yml` to the base file. `up` builds the current checkout. `update` requires a clean tracked tree on a tracked branch, fast-forwards it and builds before recreation. It supports Git worktrees. It does not prune shared images or build caches. Set `./tools/develop.sh mode release` to return to published images. If an older installation has saved source mode, the normal script stops with instructions to use the developer script.
