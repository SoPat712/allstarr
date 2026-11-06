# Allstarr Apple download gateway

This optional service implements Allstarr's Apple download gateway API. It installs the official `gamdl` 3.9.1
Python package and uses reusable worker processes with selected-account binding. The image does not bundle
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

The media-only sidecar API is version `2.1.0`. Catalog metadata is owned by Allstarr's MusicKit client.

- `GET /api/capabilities`
- `GET /api/health`
- `GET /api/me`
- `POST /api/login`
- `POST /api/login/2fa`
- `GET /api/audio/{songId}?quality=...` — progressive FLAC for lossless sources, or AAC in a fragmented MP4 container
- `HEAD /api/audio/{songId}?quality=...` — prepares the same file and returns its real length and type
- `GET /api/download/{songId}?quality=...` — complete managed FLAC artifact
- `GET /api/stream/{songId}?quality=...` — an empty ID3 prelude followed by progressive FLAC;
  completed cache hits return the FLAC file with byte-range support
- `GET /api/lyrics/{songId}?quality=...` — synced lyrics artifact
- `HEAD /api/stream/{songId}?quality=...` and `HEAD /api/download/{songId}?quality=...` — FLAC type/disposition only
- `HEAD /api/lyrics/{songId}?quality=...` — JSON type only; no media preparation

The `audio-file` capability tells current Allstarr versions to use `/api/audio` for playback. For an uncached
song, the gateway fetches catalog, playback, manifest, lyrics, and artwork before delivering media. Artwork
overlaps playback preparation. It downloads segments or byte ranges in order and decrypts each fragment
through the pinned native media implementation. One FFmpeg process consumes the clear fragments and writes
FLAC with compression level 0, or copies AAC into fragmented MP4. Tags and artwork precede the audio.
Copied audio is also decoded into a null output by the same FFmpeg process, so corrupt samples fail the
pipeline. Missing formats can fall back to `aac-web` before output starts.

Playback requests can opt into `original_container=true` on `/api/audio`. ALAC then stays in an M4A container
when it fits the requested quality ceiling. Conversion still enforces both sample rate and bit depth when
needed, including 24-bit to 16-bit output. Stream selection chooses the largest lossless stream within the
ceiling, or the smallest available lossless stream when conversion is necessary. AAC retains its source
sample rate. Original, converted, and compatibility-stream outputs have separate cache and in-flight identities.

Uncached `/api/audio` responses have no `Content-Length` or `Accept-Ranges`. HTTP chunking delivers encoded
bytes as they become available. `X-Allstarr-Duration-Seconds` supplies catalog duration when available;
Allstarr applies FLAC duration guidance without waiting for the song to finish. The progressive FLAC header
has no final sample count or seek table. `X-Allstarr-Prepare-Download-Ms` reports time to the pipeline's first
encoded chunk, shared by coalesced listeners. `X-Allstarr-Prepare-Process-Ms` is zero because processing
overlaps delivery. Cache hits report zero preparation time.

Each pipeline writes identical output bytes into a private disk spool. Requests for the same account,
storefront, song, quality, and output variant share that pipeline; later listeners replay from byte zero.
Only successful download, decryption, and FFmpeg completion allow atomic publication of the spool. Failures
abort every listener without a successful end of body and remove the partial artifact. The last listener's
disconnect cancels the pipeline and kills its worker process group. Other listeners keep the pipeline alive.

Completed files use `FileResponse` with `Content-Length`, byte-range support, and a stable strong ETag.
Byte ranges receive 206 and unsatisfiable ranges receive 416. Cache hits refresh access time without changing
modification time. HEAD prepares an uncached `/api/audio` file to completion. The prepared cache retains its
six-hour idle expiry and 32-track limit. `/api/download` materializes the same account-bound pipeline before
returning its complete FLAC artifact.

Before media starts, account mismatch is 403 with `X-Allstarr-Failure-Code: account_mismatch`, process timeout
is 504, and other failures are 502 with a JSON `detail` code. After delivery starts, failures truncate the
transfer. Logs identify the failing stage and delivered byte count; successful pipelines log first-byte and
total preparation times. Allstarr rejects truncated transfers and publishes its managed cache only after
complete delivery and audio verification.

Older Allstarr versions can continue using `/api/stream` with its prelude and progressive FLAC body.
The `/api/download` route keeps its complete FLAC artifact contract. The old catalog
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
is not a media-cache key and is not persisted with artifacts. Selected tokens travel to workers over stdin
and remain in memory. Configured operator cookie files are not used for media requests.

Each worker retains up to eight sessions keyed by selected token and storefront. Setup creates the selected
Apple API, verifies its identity against the wrapper, and retains HTTP clients, storefront, and device state
across songs. Sessions refresh after fifteen minutes, before the public web token expires, or once after a
401/403 response. Missing or differing identities fail with `account_mismatch`; session setup never signs in or
switches wrapper accounts. Per-song interfaces, manifest caches, artwork tasks, and conversion
state are separate. At most `APPLE_GATEWAY_MAX_CONCURRENCY` worker jobs run at once, sharing the existing
subprocess limit. Dead, timed-out, or abandoned workers are killed with their process groups and replaced.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `APPLE_GATEWAY_WRAPPER_URL` | `http://wrapper-v2` | Separate wrapper-v2 origin |
| `APPLE_GATEWAY_WRAPPER_DECRYPT_HOST` | `wrapper-v2` | Wrapper-v2 raw decrypt host reachable from this container |
| `APPLE_GATEWAY_WRAPPER_DECRYPT_PORT` | `10020` | Wrapper-v2 raw decrypt port reachable from this container |
| `APPLE_GATEWAY_DATA_ROOT` | `/data` | Persistent artifact and temporary root |
| `APPLE_GATEWAY_MAX_CONCURRENCY` | `2` | Shared worker-job and subprocess limit |
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

## Test and build

Run the offline fixtures with the project's locked dependencies:

```bash
cd sidecars/apple-gateway
uv sync --frozen --extra test
nice -n 19 uv run --frozen --extra test pytest
```

The short FLAC metadata check uses local `ffmpeg` and `metaflac` executables. The gateway image installs both
through the Debian `ffmpeg` and `flac` packages.

Build the updated gateway image from the repository root:

```bash
docker build -t allstarr/apple-gateway:1.1.0 sidecars/apple-gateway
```

For an existing saved Apple profile, `./allstarr.sh up` rebuilds the gateway and starts the profile with its
existing volumes. Deploy the matching Allstarr build to enable file playback; older builds keep using the
legacy routes. No new sign-in is needed for this update.
