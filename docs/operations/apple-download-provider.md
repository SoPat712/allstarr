# External Apple download provider

Apple downloads are optional. Standard Compose and AIO do not include GAMDL, wrapper-v2, or an Apple provider
gateway. Allstarr connects to a separately deployed compatible gateway by URL.

The URL must point to a gateway that wraps GAMDL and wrapper-v2 and implements the API contract Allstarr expects.
Do not enter the raw wrapper-v2 URL. wrapper-v2 supplies account, playback, and decryption services, but it is not a
GAMDL search and download HTTP gateway by itself.

## Prepare the external gateway

Deploy the gateway in its own stack by following its maintainer's instructions. Keep its session state, downloaded
artifacts, backups, upgrades, and rollback outside the Allstarr stack. The upstream projects are
[GAMDL](https://github.com/glomatico/gamdl) and
[wrapper-v2](https://github.com/glomatico/wrapper-v2). Neither project by itself implements the gateway contract
below. The gateway is the HTTP adapter between those tools and Allstarr.

For a GAMDL-backed gateway, use GAMDL 3.8.2 or newer with wrapper-v2 0.0.2 or the exact compatible pair required by
the gateway. GAMDL 3.8.2 introduced compatibility with wrapper-v2 0.0.2. Do not update one half of the pair without
the other. The gateway's runtime capability and version manifest is authoritative; an upstream feature does not
become an Allstarr capability until the gateway advertises it and Allstarr's probe accepts it.

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

The gateway may additionally advertise `metadata-album`, `metadata-artist`, `download-album`,
`download-playlist`, `library-read`, `stream-music-video`, `synced-lyrics-artifact`, `tagging-artwork`,
`codec-alac`, and `codec-aac`. Allstarr displays absent features as unsupported. It never turns an upstream GAMDL
feature into an Allstarr route merely because GAMDL supports it.

The current typed lanes use these routes:

| Purpose | Gateway route |
| --- | --- |
| Capability discovery | `GET /api/capabilities` |
| Runtime health | `GET /api/health` |
| Account status | `GET /api/me` |
| Login and 2FA | `POST /api/login`, `POST /api/login/2fa` |
| Song search and detail | `GET /api/search`, `GET /api/song/{id}` |
| Managed track artifact | `GET /api/download/{id}?quality={quality}` |

Health must report the gateway, GAMDL runtime, and wrapper dependency truthfully. A raw wrapper-v2 `/health`
response is not enough. Allstarr refuses redirects during discovery and login so credentials cannot be forwarded to
an unexpected host.

## Connect Allstarr

Open the dashboard, find the Apple download provider, and set its gateway URL. This durable WebUI setting takes
effect without recreating Allstarr. A Compose deployment may instead supply the initial bootstrap value:

```dotenv
APPLE_DOWNLOAD_URL=https://apple-gateway.example.internal
```

The legacy `APPLE_MUSIC_AIO_URL` name is recognized only by the migration review so an operator can identify the
old endpoint. New deployments use `APPLE_DOWNLOAD_URL`.

Recreate only the Allstarr container when changing that deployment-owned environment value:

```bash
docker compose config --quiet
docker compose up -d --no-deps --force-recreate allstarr
docker compose ps
curl --fail http://127.0.0.1:5274/health/ready
```

Use the dashboard to check the provider state, finish login or 2FA if the compatible gateway supports that flow,
and test the capabilities you intend to route. A reachable container is not enough. Allstarr should select only the
capabilities reported by the gateway and accepted by its health and compatibility checks.

Keep Apple MusicKit accounts separate. A per-user Music User Token belongs to Apple MusicKit library and playlist
access. It is not a GAMDL or wrapper account and must not be copied into the download gateway.

## Disable or replace the gateway

Clear the configured URL in the dashboard. This disables only the Apple download provider and takes effect without
restarting Allstarr. It does not delete Postgres records, Allstarr-managed media, the external gateway's session
state, or a user's Apple MusicKit account. If the URL came from `APPLE_DOWNLOAD_URL`, remove that bootstrap value
and recreate only the Allstarr container.

To replace the gateway, validate the replacement independently, change the URL in the dashboard, and repeat the
provider health and capability checks. Do not combine that change with a database restore or media move. Keeping
those operations separate makes rollback much easier.
