# Apple download provider

Apple downloads are optional. The `apple` profile in `docker-compose.yml` adds Allstarr's Apple gateway, GAMDL
3.8.2, and a locally built, source-locked
wrapper-v2 0.0.2 service. It does not contain or download Apple code.

The URL must point to a gateway that wraps GAMDL and wrapper-v2 and implements the API contract Allstarr expects.
Do not enter the raw wrapper-v2 URL. wrapper-v2 supplies account, playback, and decryption services, but it is not a
GAMDL search and download HTTP gateway by itself.

## Prepare wrapper-v2

Obtain Apple Music for Android legally from a source you are permitted to use. Allstarr does not download or
redistribute that package. Open Sources > Apple download in the WebUI, upload the APK/APKM, and run:

```bash
./allstarr.sh install-apple x86_64
```

Use `--apkm` for an APKM bundle. For ARM64 use `--arch arm64-v8a`, then set
`APPLE_WRAPPER_TARGET_ARCH=arm64-v8a` and `APPLE_WRAPPER_RUNTIME_PLATFORM=linux/arm64`.

An old installation's archived libraries can be reused only when they pass the official hash lock:

```bash
./allstarr.sh prepare-apple /backup/rootfs/system/lib64 x86_64
```

The controller clones official wrapper-v2 tag `0.0.2`, verifies its full locked commit, stages the official
AOSP runtime, and rejects Apple or AOSP libraries that do not match wrapper-v2's `LIBS_VERSION.json`. The generated
`.apple-provider/wrapper-v2` directory is local deployment input and must not be committed.

## What the profile runs

The repository gateway is a narrow HTTP adapter around the official upstream projects
[GAMDL](https://github.com/glomatico/gamdl) and
[wrapper-v2](https://github.com/glomatico/wrapper-v2). It exposes catalog song lookup, download-backed streaming,
managed song downloads, health, login, and 2FA. It runs on the private Compose network and publishes no host port.

The gateway advertises only routes that Allstarr has implemented and tested. GAMDL can do more upstream, but album,
playlist, artist, library, video, lyrics-artifact, and artwork lanes stay unavailable in Allstarr until their
managed-artifact contracts exist. That keeps the UI honest and prevents an upstream feature name from becoming a
false promise.

Before connecting it, confirm that:

- the gateway is reachable from the Allstarr container through an explicit HTTP or HTTPS URL;
- its health endpoint reports the expected API and capability manifest;
- its account/session and 2FA flow works without exposing credentials in the URL;
- its output and session paths are persistent and backed up according to the gateway's own runbook; and
- any reverse proxy preserves streaming responses and uses a private, authenticated network boundary.

### Gateway contract

Allstarr first requests `GET /api/capabilities`. A compatible version 1 response looks like this:

```json
{
  "sidecarApiVersion": "1.0.0",
  "capabilities": [
    { "id": "metadata-search-song", "state": "supported" },
    { "id": "metadata-song", "state": "supported" },
    { "id": "stream-audio-song", "state": "supported" },
    { "id": "download-audio-song", "state": "supported" }
  ]
}
```

The included gateway also advertises its verified ALAC and AAC input support. Allstarr displays absent features as
unsupported.

The current typed lanes use these routes:

| Purpose | Gateway route |
| --- | --- |
| Capability discovery | `GET /api/capabilities` |
| Runtime health | `GET /api/health` |
| Account status | `GET /api/me` |
| Login and 2FA | `POST /api/login`, `POST /api/login/2fa` |
| Song search and detail | `GET /api/search`, `GET /api/song/{id}` |
| Managed track artifact | `GET /api/download/{id}?quality={quality}` |
| Progressive playback | `GET /api/stream/{id}?quality={quality}` |

Health must report the gateway, GAMDL runtime, and wrapper dependency truthfully. A raw wrapper-v2 `/health`
response is not enough. Allstarr refuses redirects during discovery and login so credentials cannot be forwarded to
an unexpected host.

## Connect Allstarr

Open the dashboard and find the Apple download provider. The Compose profile configures its private gateway URL.
Finish Apple login or 2FA there; credentials are forwarded only to wrapper-v2 and are not stored in Postgres.

The legacy `APPLE_MUSIC_AIO_URL` name is recognized only by the migration review so an operator can identify the
old endpoint. New deployments use `APPLE_DOWNLOAD_URL`.

Apply deployment changes through the saved profile:

```bash
./allstarr.sh up
./allstarr.sh status
```

For later application and gateway updates, use `./allstarr.sh update`. It preserves the Apple profile and its
session volumes. Run `prepare-apple` again only when the verified wrapper inputs need to change; use
`install-apple` when you want that verification, build, and startup handled in one command.

Use the dashboard to check the provider state, finish login or 2FA if the compatible gateway supports that flow,
and test the capabilities you intend to route. A reachable container is not enough. Allstarr should select only the
capabilities reported by the gateway and accepted by its health and compatibility checks.

Keep Apple MusicKit accounts separate. A per-user Music User Token belongs to Apple MusicKit library and playlist
access. It is not a GAMDL or wrapper account and must not be copied into the download gateway.

## Disable or replace the gateway

Run `./allstarr.sh disable apple` followed by `./allstarr.sh up`. This removes the optional containers from the
active profile. It does not delete Postgres records, Allstarr-managed media, the wrapper session volume, gateway
state, or a user's Apple MusicKit account.

To replace the gateway, validate the replacement independently, change the URL in the dashboard, and repeat the
provider health and capability checks. Do not combine that change with a database restore or media move. Keeping
those operations separate makes rollback much easier.
