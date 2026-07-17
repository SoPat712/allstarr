# Allstarr Apple download gateway

This optional service implements Allstarr's Apple download gateway API. It installs the official `gamdl` 3.8.2
Python package and calls its documented CLI. It does not contain wrapper-v2 source, Apple native libraries,
credentials, or session data.

The optional root Compose profile builds the locked official wrapper-v2 0.0.2 source beside this gateway and puts
both on Allstarr's private network. Do not expose the gateway or wrapper login endpoints to the public Internet.

## Runtime contract

- `GET /api/capabilities`
- `GET /api/health`
- `GET /api/me`
- `POST /api/login`
- `POST /api/login/2fa`
- `GET /api/search?q=...&type=song&limit=...`
- `GET /api/song/{songId}`
- `GET /api/download/{songId}?quality=...`
- `GET /api/stream/{songId}?quality=...`
- `POST /api/jobs/download` for supported Apple catalog or library URLs
- `GET /api/jobs/download/{jobId}`

Song downloads return a FLAC artifact because that is the current Allstarr managed-song contract. Generic jobs
preserve GAMDL's own media, artwork, tag, and synced-lyrics outputs in the gateway data volume.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `APPLE_GATEWAY_WRAPPER_URL` | `http://wrapper-v2` | Separate wrapper-v2 origin |
| `APPLE_GATEWAY_WRAPPER_DECRYPT_HOST` | `wrapper-v2` | Wrapper-v2 raw decrypt host reachable from this container |
| `APPLE_GATEWAY_WRAPPER_DECRYPT_PORT` | `10020` | Wrapper-v2 raw decrypt port reachable from this container |
| `APPLE_GATEWAY_STOREFRONT` | `us` | Two-letter Apple storefront |
| `APPLE_GATEWAY_DATA_ROOT` | `/data` | Persistent artifact and temporary root |
| `APPLE_GATEWAY_COOKIES_PATH` | empty | Optional cookies file used by GAMDL |
| `APPLE_GATEWAY_MAX_CONCURRENCY` | `2` | Shared GAMDL/FFmpeg subprocess limit |
| `APPLE_GATEWAY_PROCESS_TIMEOUT_SECONDS` | `900` | Hard subprocess deadline |
| `APPLE_GATEWAY_WRAPPER_TIMEOUT_SECONDS` | `45` | Wrapper request deadline |
| `APPLE_GATEWAY_MAX_PROCESS_OUTPUT_BYTES` | `32768` | Maximum retained stdout and stderr per process |

Prepare wrapper-v2 with the operator's legal APK/APKM, then use the saved Apple profile:

```bash
./allstarr.sh prepare-apple /private/path/apple-music.apkm x86_64
./allstarr.sh up
```

The root overlay configures `http://apple-gateway:8000` internally. Removing the profile does not change
Allstarr's Postgres state, existing media, gateway state, or wrapper login session.

The supported GAMDL URL kinds are songs, albums, playlists, artists, music videos, posts, and Apple library URLs.
The gateway passes arguments as an exec array, never through a shell. It rejects non-Apple hosts, URL credentials,
fragments, unsafe artifact paths, symlinks, redirects from wrapper-v2, and unbounded process output.
