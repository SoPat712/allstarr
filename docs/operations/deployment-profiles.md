# Deployment profiles

`allstarr.sh` is the normal Compose entry point. It keeps Standard small, remembers optional services, validates the
merged Compose model before starting it, and never deletes volumes.

## First install

```bash
./allstarr.sh init
```

This copies `.env.example` when needed, creates the media directories, and generates the Postgres password and
Allstarr encryption key ring with owner-only permissions. Edit `.env`, then start the core stack:

```bash
./allstarr.sh up
```

## Optional services

Spotify lyrics needs only its cookie in the protected `.env`:

```bash
./allstarr.sh enable spotify
./allstarr.sh up
```

Apple needs a legally obtained Apple Music Android 3.6.0-beta build 1109 APK/APKM. Allstarr verifies every native
library against the official wrapper-v2 lock before it will build:

```bash
./allstarr.sh prepare-apple /private/path/apple-music.apkm x86_64
./allstarr.sh up
```

Use `arm64-v8a` instead of `x86_64` on an ARM64 Docker host. Finish Apple login and 2FA in the WebUI. The Apple
download login is separate from every user's Apple MusicKit account.

An existing installation can pass its staged library directory instead of the APK/APKM. The same upstream hashes
must pass before the profile is enabled:

```bash
./allstarr.sh prepare-apple /backup/apple_libs x86_64
```

The AIO override remains an offline first-party extension bundle. It is not an everything-in-one image and does not
quietly enable provider accounts or sidecars.

```bash
./allstarr.sh enable aio
./allstarr.sh up
```

## Updates

```bash
./allstarr.sh update
```

The command pulls reviewed images, rebuilds local Apple components only when that profile is enabled, recreates the
saved profile, and shows the resulting containers. It does not run `git pull`; source updates are an explicit
operator action. It does not remove Postgres, Valkey, Allstarr state, media, or provider-session volumes.

Use `./allstarr.sh status` to see the active profile and `./allstarr.sh logs SERVICE` for a bounded starting log
window followed by new events.

## Removing and re-adding a service

```bash
./allstarr.sh disable spotify
./allstarr.sh disable apple
./allstarr.sh up
```

Disabling a profile removes its containers on the next `up` but keeps its volumes and durable Allstarr settings.
Re-enable Spotify with `enable spotify`; re-enable Apple by running `prepare-apple` again or adding `apple` to the
local `.allstarr-profiles` file after the locked wrapper context is present.

## Adding another sidecar

Keep custom services in a separate Compose override. Give the service a fixed image digest or locked build source,
join the existing `allstarr` network, publish no host ports unless an operator truly needs them, persist only the
state it owns, and point Allstarr at its private service URL. Do not mount the Docker socket into Allstarr or a
provider. Do not put provider source pulls in container startup.

Before using a new override:

```bash
docker compose -f docker-compose.yml -f docker-compose.my-provider.yml config --quiet
docker compose -f docker-compose.yml -f docker-compose.my-provider.yml up -d
```

An extension package is different from a sidecar. Provider SDK packages are installed through the WebUI registry
flow, where checksum, manifest, permissions, settings, health, activation, rollback, and uninstall state are
reviewed. See [Extension SDK v1](../extensions/sdk-v1.md).
