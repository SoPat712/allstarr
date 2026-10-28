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

The media-only sidecar API is version `2.1.0`. Catalog metadata is owned by Allstarr's MusicKit client.

- `GET /api/capabilities`
- `GET /api/health`
- `GET /api/me`
- `POST /api/login`
- `POST /api/login/2fa`
- `GET /api/audio/{songId}?quality=...` — complete, cached FLAC for lossless sources, or the original AAC MP4 file
- `HEAD /api/audio/{songId}?quality=...` — prepares the same file and returns its real length and type
- `GET /api/download/{songId}?quality=...` — complete managed FLAC artifact
- `GET /api/stream/{songId}?quality=...` — immediate headers and an empty ID3 prelude,
  followed by FLAC after account-scoped source preparation. Allstarr strips the prelude
  and applies duration guidance lazily from catalog metadata. Preparation failures after
  headers stop the stream; `/api/download` can return a structured preparation error.
- `GET /api/lyrics/{songId}?quality=...` — synced lyrics artifact
- `HEAD /api/stream/{songId}?quality=...` and `HEAD /api/download/{songId}?quality=...` — FLAC type/disposition only
- `HEAD /api/lyrics/{songId}?quality=...` — JSON type only; no media preparation

The `audio-file` capability tells current Allstarr versions to use `/api/audio` for playback. The first request
waits for GAMDL to finish, then converts lossless audio to a regular FLAC file with compression level 0.
A metadata pass with `metaflac` adds seek points every ten seconds; the finished file has its real total sample
count. If lossless preparation fails with a missing artifact or download failure, the gateway tries `aac-web`.
AAC sources stay in their original `.m4a` container and are served as `audio/mp4`.

Original-download playback requests opt into `original_container=true` on `/api/audio`. Completed ALAC and AAC
sources then keep their M4A bytes, avoiding an extra lossless conversion. Default playback and `/api/download`
retain their FLAC contracts. Original and converted outputs have separate cache and in-flight identities while
sharing the completed, account-scoped GAMDL source. Full source completion and the normal Allstarr cache
verification remain required before cache publication; encrypted or incomplete output is never exposed.
GAMDL 3.9.1 downloads the encrypted input before its native decrypt/mux call produces the completed file, so
this option removes conversion time but does not provide progressive decryption.

Successful `/api/audio` responses report integer-only `X-Allstarr-Prepare-Download-Ms` and
`X-Allstarr-Prepare-Process-Ms` durations. The first includes GAMDL preparation; the second includes codec
inspection and conversion or original-file copying. Cache hits and coalesced followers report zero. Current
Allstarr diagnostics retain valid bounded values as overlapping preparation/process phases, positioned before
the sidecar response arrives; those positions approximate the sidecar work rather than distributed clock time.

GET and HEAD return `Content-Length`, `Content-Type`, and `Accept-Ranges: bytes`. Byte ranges receive 206;
unsatisfiable ranges receive 416 through the file response handler. Cache hits refresh access time without changing the modification time or strong ETag, so If-Range reopens retain the same representation. HEAD prepares an uncached song before
returning headers. Requests for the same account, storefront, song, and quality share both source preparation
and conversion. Finished files are published with an atomic rename and share the prepared cache's six-hour
idle expiry and 32-track limit with their source files. Cache hits avoid downloading and converting again.
Preparation errors reach the caller before any media headers or bytes: account mismatch is 403 with
`X-Allstarr-Failure-Code: account_mismatch`, process timeout is 504, and other failures are 502. The JSON
`detail` contains the failure code. Each preparation logs the song, quality used, download and conversion
seconds, and byte count. Failures include the code and the last three retained GAMDL error lines with tokens,
cookies, and URLs removed.

Older Allstarr versions can continue using `/api/stream` with its existing prelude and sequential FLAC body.
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
is not a cache key and is not persisted with artifacts. Each GAMDL process receives a unique mode-`0600` Netscape
cookie file inside its request temporary directory; the file is removed on completion, failure, or cancellation.
Configured operator cookie files are not used for media requests.

GAMDL's wrapper CLI normally uses the wrapper account even when a cookie path is supplied. The gateway's isolated
entrypoint instead builds its Apple API from the request cookie file and compares that account's ID with the
wrapper account before allowing media operations. Missing or differing identities fail with `account_mismatch`;
the gateway never switches wrapper accounts automatically. The selected API remains responsible for catalog,
private library, and web playback calls. Wrapper playback/decryption is allowed only after the account match,
and account identity failures suppress upstream output. Other diagnostics retain only a bounded, redacted tail.

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

## Test and build

Run the offline fixtures with the project's locked dependencies:

```bash
cd sidecars/apple-gateway
uv sync --frozen --extra test
uv run --frozen --extra test pytest
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
