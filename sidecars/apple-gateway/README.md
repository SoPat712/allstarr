# Allstarr Apple download gateway

This optional service implements Allstarr's Apple download gateway API. It installs the official `gamdl` 3.9.1
Python package and runs its CLI in an isolated process with selected-account binding. The image does not bundle
wrapper-v2 source, Apple native libraries, credentials, or session data.

The optional root Compose profile builds the locked official wrapper-v2 0.0.2 source beside this gateway and puts
both on Allstarr's private network. Do not expose the gateway or wrapper login endpoints to the public Internet.

## GAMDL qualification

Checked on 2026-10-08: the exact `3.9.1` pin matches the latest stable
[official GitHub release](https://github.com/glomatico/gamdl/releases/tag/3.9.1) and
[PyPI release](https://pypi.org/project/gamdl/3.9.1/), published on 2026-09-22 from commit
`bc3bcd25ed61dcdb78c28ffb0ef32053b618c7e3`.

Qualification in disposable environments on macOS ARM64 with Python 3.13.15 passed all 83 gateway/account-entrypoint fixtures
(0 failures, 0 skips) and 15 offline CLI argument cases using the installed GAMDL parser. These cases cover
the runner's codecs, isolated cookies path, wrapper HTTP/decrypt arguments, output paths, artist selection,
and lyrics-only mode. Mocked factories against the installed GAMDL CLI verify that only the selected account API
reaches media operations and an account mismatch fails before that boundary. The native media extension imports successfully. No live Apple account, wrapper
activation, download, or deployment was exercised.

Python remains `>=3.10`; the existing Python 3.12 Bookworm image needs no version change for this pin.
The release provides CPython 3.10 ABI-compatible Linux wheels for x86_64 and ARM64 requiring glibc 2.34+.
uv 0.12.23 produced the lock, and the image's uv 0.11.29 passed both a lock check and a fresh
frozen install. Existing dependency versions remain unchanged except GAMDL; its new PlayReady dependency
adds the required transitive packages. No version-specific argument changes are required; account binding is
described below.

## Runtime contract

The media-only sidecar API is version `2.0.0`. Catalog metadata is owned by Allstarr's MusicKit client.

- `GET /api/capabilities`
- `GET /api/health`
- `GET /api/me`
- `POST /api/login`
- `POST /api/login/2fa`
- `GET /api/download/{songId}?quality=...` — complete managed FLAC artifact
- `GET /api/stream/{songId}?quality=...` — progressive FLAC with an immediate,
  empty 10-byte ID3v2 prelude while Apple fetch/decryption completes; transcoded
  sources carry a duration-stamped FLAC header when source duration is known
- `GET /api/lyrics/{songId}?quality=...` — synced lyrics artifact
- `HEAD /api/stream/{songId}?quality=...` and `HEAD /api/download/{songId}?quality=...` — FLAC type/disposition only
- `HEAD /api/lyrics/{songId}?quality=...` — JSON type only; no media preparation

Song downloads return a FLAC artifact because that is the current Allstarr managed-song contract. The old catalog
and generic download-job endpoints are removed. Durable download jobs are owned by Allstarr; existing sidecar
job output is left untouched.

Every media GET or HEAD requires these headers from Allstarr's selected account credential lease:

| Header | Required value |
| --- | --- |
| `Music-User-Token` | Exact selected account token; never placed in the URL |
| `X-Apple-Storefront` | Two lowercase letters |
| `X-Allstarr-Account-Context` | 64 lowercase hexadecimal characters derived from account ID and revision |

Missing, duplicate, or invalid headers return `401` before media work. Prepared media, lyrics, and concurrent
preparation are scoped by account context, storefront, numeric catalog song ID, and requested quality. The token
is not a cache key and is not persisted with artifacts. Each GAMDL process receives a unique mode-`0600` Netscape
cookie file inside its request temporary directory; the file is removed on completion, failure, or cancellation.
Configured operator cookie files are not used for media requests.

GAMDL's wrapper CLI normally uses the wrapper account even when a cookie path is supplied. The gateway's isolated
entrypoint instead builds its Apple API from the request cookie file and compares that account's ID with the
wrapper account before allowing media operations. Missing or differing identities fail with `account_mismatch`;
the gateway never switches wrapper accounts automatically. The selected API remains responsible for catalog,
private library, and web playback calls. Wrapper playback/decryption is allowed only after the account match,
and upstream process output is suppressed to keep account details out of logs.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `APPLE_GATEWAY_WRAPPER_URL` | `http://wrapper-v2` | Separate wrapper-v2 origin |
| `APPLE_GATEWAY_WRAPPER_DECRYPT_HOST` | `wrapper-v2` | Wrapper-v2 raw decrypt host reachable from this container |
| `APPLE_GATEWAY_WRAPPER_DECRYPT_PORT` | `10020` | Wrapper-v2 raw decrypt port reachable from this container |
| `APPLE_GATEWAY_DATA_ROOT` | `/data` | Persistent artifact and temporary root |
| `APPLE_GATEWAY_MAX_CONCURRENCY` | `2` | Shared GAMDL/FFmpeg subprocess limit |
| `APPLE_GATEWAY_PROCESS_TIMEOUT_SECONDS` | `900` | Hard subprocess deadline |
| `APPLE_GATEWAY_WRAPPER_TIMEOUT_SECONDS` | `45` | Wrapper request deadline |
| `APPLE_GATEWAY_MAX_PROCESS_OUTPUT_BYTES` | `32768` | Maximum retained stdout and stderr per process |

Prepare wrapper-v2 with the operator's legal APK/APKM, then use the saved Apple profile:

```bash
./allstarr.sh prepare-apple /private/path/apple-music.apkm x86_64
./allstarr.sh up
```

The root Compose profile configures `http://apple-gateway:8000` internally. Removing the profile does not change
Allstarr's SQLite state, existing media, gateway state, or wrapper login session.

Media URLs are constructed from the validated storefront and numeric catalog song ID. The gateway passes
arguments as an exec array, never through a shell. It rejects unsafe artifact paths, symlinks, redirects from
wrapper-v2, and unbounded process output.
