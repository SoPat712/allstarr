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

The default `release` mode uses reviewed published images. To run the checked-out commit instead:

```bash
./allstarr.sh mode source
./allstarr.sh up
```

Source mode includes `docker-compose.dev.yml` automatically. Switch back with `./allstarr.sh mode release`.

## Optional services

Spotify lyrics needs only its cookie in the protected `.env`:

```bash
./allstarr.sh enable spotify-lyrics
./allstarr.sh up
```

Apple needs a legally obtained Apple Music Android 3.6.0-beta build 1109 APK/APKM. Upload it in Sources > Apple
download, then Allstarr verifies every native library against the official wrapper-v2 lock before it will build:

```bash
./allstarr.sh install-apple x86_64
```

Use `arm64-v8a` instead of `x86_64` on an ARM64 Docker host. Finish Apple login and 2FA in the WebUI. The Apple
download login is separate from every user's Apple MusicKit account.

An existing installation can pass its staged library directory instead of the APK/APKM. The same upstream hashes
must pass before the profile is enabled:

```bash
./allstarr.sh install-apple /backup/apple_libs x86_64
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

In release mode, the command pulls reviewed images and rebuilds the repository-owned Apple gateway only when that
profile is enabled. In source mode, `update` refuses tracked local changes, runs `git pull --ff-only`, then rebuilds
the Allstarr source and enabled Apple gateway. The private wrapper image is rebuilt only by `prepare-apple`, when
its verified inputs may have changed. Both modes recreate the saved profile and show the resulting containers.
`up` does not run `git pull`, and neither command removes Postgres, Valkey, Allstarr state, media, or
provider-session volumes.

Use `./allstarr.sh status` to see the deployment mode and active profile, and `./allstarr.sh logs SERVICE` for a bounded starting log
window followed by new events.

## Removing and re-adding a service

```bash
./allstarr.sh disable spotify-lyrics
./allstarr.sh disable apple
./allstarr.sh up
```

Disabling a profile removes its containers on the next `up` but keeps its volumes and durable Allstarr settings.
Re-enable Spotify lyrics with `enable spotify-lyrics`; re-enable Apple by running `install-apple` again or adding `apple` to the
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
