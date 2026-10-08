# Deployment profiles

`allstarr.sh` is the supported install and update entry point. It wraps the single checked-in `docker-compose.yml`, validates the resulting Compose model, remembers explicitly enabled optional profiles, and does not delete volumes.

## First install

```bash
./allstarr.sh init
# edit .env if required
./allstarr.sh up
```

The default release mode uses the configured published image. Source mode builds the checked-out tree:

```bash
./allstarr.sh mode source
./allstarr.sh up
```

Switch back with `./allstarr.sh mode release`.

## Default stack

The default stack runs one Allstarr container with SQLite. Its `/app/data` mount holds the database, encryption key ring, managed media, extensions, backups, and cache. `.env.example` selects the host folder `./data`; if `ALLSTARR_DATA_PATH` is unset, Compose uses the `allstarr-data` named volume. Use local disk and only one Allstarr process per data folder.

The optional Apple setup upload folder remains a separate bind mount. Apple gateway and wrapper session volumes belong to the optional Apple profile. There is no database sidecar, Redis, Valkey, AIO image, conversion container, or Compose overlay in the supported topology.

## Optional profiles

Spotify lyrics uses the pinned upstream image already declared in the native `spotify-lyrics` profile:

```bash
./allstarr.sh enable spotify-lyrics
./allstarr.sh up
```

Apple download requires a legally obtained supported Apple Music Android package. Upload it through the Apple source setup, then run:

```bash
./allstarr.sh install-apple x86_64
```

Use `arm64-v8a` on an ARM64 Docker host. The script verifies the staged package and upstream wrapper inputs before enabling the native `apple` profile. Allstarr distributes its own thin integration layer, not the upstream provider implementation or Apple package.

See [Spotify lyrics](spotify-lyrics-sidecar.md) and [Apple download](apple-download-provider.md).

## Update

```bash
./allstarr.sh update
```

Release mode pulls the configured images and recreates the active profiles. Source mode requires a clean tracked tree, fast-forwards the checkout, rebuilds, and recreates the active profiles. `up` does not pull repository changes.

Before updating, create and download a backup from Settings → Maintenance. Keep a separate copy of deployment configuration and managed media.

## Backup and restore

Use Settings → Maintenance to create or download a verified backup, or upload one for restore on restart. The archive includes the database and encryption key ring. It excludes media, extension packages, cache, deployment configuration, and optional provider sessions. The former shell `backup`, `restore`, and `upgrade` commands direct users to Maintenance.

To move an entire installation, stop Allstarr and copy the complete data folder along with deployment configuration and any explicit external paths or optional provider volumes. Start the new container with the same image version and mounts, and keep the original stopped. Follow [the storage runbook](storage.md) for recovery and verification.

## Operations

```bash
./allstarr.sh status
./allstarr.sh logs allstarr
./allstarr.sh disable spotify-lyrics
./allstarr.sh disable apple
./allstarr.sh down
```

Disabling an optional profile removes its running containers on the next reconciliation but preserves its durable volumes and Allstarr configuration. Re-enable it explicitly when needed.

## Adding future optional integrations

A future integration belongs in the single Compose file behind an explicit profile and an `allstarr.sh` command. Pin the upstream image or verified source, publish no unnecessary host ports, persist only owned state, and keep the Docker socket out of application containers. Do not vendor or redistribute upstream implementation code when Allstarr only needs an integration layer.
