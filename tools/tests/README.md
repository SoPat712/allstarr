# Jellyfin compatibility qualification

The reusable Jellyfin kit has deterministic and live layers:

- `ProtocolSupportMatrixTests` evaluates all 364 operations in the pinned
  Jellyfin 12.0.0 OpenAPI and all 388 operations in the pinned Jellyfin
  10.11.11 OpenAPI against the executable deny-by-default music policy.
- `jellyfin-openapi-qualification.json` records the 12.0 allow-list, typed
  synthesized-resource modes, playlist modes, DTO requirements, intentional
  differences, and unavailable-live-runtime blockers.
- `jellyfin-openapi-10.11-qualification.json` records the complete delta from
  12.0, including legacy audio HLS and query-form artist instant-mix routes.
- `live_jellyfin_smoke.sh` compares a real Jellyfin instance directly with
  Allstarr. It covers bootstrap and authentication, native structural/stable
  data parity, non-empty virtual playlist projections with client-indexable
  track/artist/album fields, exact full-object parity between every matched
  injected entry and its original Jellyfin item (apart from playlist context
  and source labels), metadata-only visibility for unmatched source rows,
  rejection of their file/stream/universal/playback-info routes,
  virtual/external DTOs and artwork, lyrics,
  playlists, security denials, exact bounded stream bytes (including Finer's
  query-only `Items/{id}/File?ApiKey=...` request), and latency.

The source URLs, versions, commits, paths, and SHA-256 hashes for both OpenAPI
files are locked in `allstarr.Tests/Fixtures/Protocols/protocol-source-lock.json`.

Run the deterministic contract:

```bash
dotnet test allstarr.Tests/allstarr.Tests.csproj \
  --filter FullyQualifiedName~ProtocolSupportMatrixTests
```

Run the safe live suite without putting a token in the command history:

```bash
read -rs JELLYFIN_TOKEN
export JELLYFIN_TOKEN
SAMPLES=5 bash tools/tests/live_jellyfin_smoke.sh | tee /tmp/allstarr-jellyfin-live.log
unset JELLYFIN_TOKEN
```

Pin a configured native playlist alias when qualifying injected playlists.
This catches clients such as Musiver that open the original Jellyfin playlist
ID instead of an `allstarr-vpl-*` ID. The kit requires one browse row with the
expected count, replays the observed playlist-items query shape, validates
every item ID and playlist context, and runs the existing full native-object
parity checks against matched entries:

```bash
INJECTED_PLAYLIST_ID=ddc3db277be524ad6f54e4b276cc619a \
INJECTED_PLAYLIST_EXPECTED_COUNT=50 \
JELLYFIN_USER_ID=1635cd7d23144ba08251ebe22a56119e \
SAMPLES=5 bash tools/tests/live_jellyfin_smoke.sh
```

Do not run the live layer while either endpoint is unhealthy. The default live
suite does not write playlists, favorites, played state, ratings, display
preferences, or lyrics. It also avoids provider-backed audio downloads. Every
ranged request retains at most 65,536 bytes; curl may report a final transport
buffer already received before the pipe closes. Provider streams exercise
prefix and suffix byte ranges when the provider supports them. An honest
non-seekable provider response is early-closed at the retained-body ceiling,
recorded as blocked instead of passed, and does not waste a second request on a
suffix it cannot serve. Invalid partial-content or range headers still fail.

Add `TEST_EXTERNAL_STREAM=1` only when a bounded cold/cache provider stream is
intended:

```bash
TEST_EXTERNAL_STREAM=1 SAMPLES=5 bash tools/tests/live_jellyfin_smoke.sh
```

Playlist editing is an explicit stateful mode. It creates one uniquely named
throwaway native playlist, verifies direct visibility, rename, add, reorder,
remove, ACL read/share/unshare, instant mix, and deletes that exact playlist.
The exit trap attempts direct cleanup only for the playlist created by the
current run.

```bash
TEST_PLAYLIST_WRITES=1 \
PLAYLIST_WRITE_CONFIRM=create-and-delete-throwaway-playlist \
SAMPLES=5 bash tools/tests/live_jellyfin_smoke.sh
```

Favorite, played/unplayed, rating, display-preference, lyric mutation, and
destructive mapping benchmarks remain blocked by the kit because they need
exact pre-state capture and restoration. The script prints a `BLOCKED` line for
each unperformed stateful class.

`stable-data-parity` compares identity and non-volatile values.
`structural-parity` compares recursive JSON field types. `declared-diff`
prints native-versus-synthesized differences that are expected and reviewed.
`BLOCKED` lines name qualification that was not performed rather than silently
counting it as passed.

The injected-playlist live comparison requires a user-bound Jellyfin access
token. A server API key can qualify native relays but cannot safely establish
the Allstarr user whose virtual playlists should be exposed, so the kit reports
that case as a failed precondition instead of silently passing Jellyfin's
native fallback. It still records direct Jellyfin and actor-visible Allstarr
counts; for example, a five-track native source beneath a 50-track injected
projection is reported as `direct=5 allstarr=5 expected=50 actor_bound=0`.

Every run prints a UTC start time and unique user agent. Use those two values
to inspect only the matching bounded server-log window; never copy tokens,
authorization headers, signed media URLs, or raw private payloads into a
saved report. Unset and revoke temporary credentials after the run.

## OpenSubsonic/Navidrome qualification

`live_subsonic_smoke.py` is read-only by default. It checks password and
token+salt authentication, JSON/XML and GET/form POST variants, inventory,
independent `search3` windows, dynamically selected artist/album/song details,
cover art, lyrics, similar/top songs, and a 65,536-byte stream range. It never
prints or stores credentials or selected media IDs; its JSON report contains
only redacted status, shape, size, and timing facts.

Run its offline safety checks first:

```bash
python3 tools/tests/test_live_subsonic_smoke.py
```

Then supply the endpoint and credentials through inherited secret input. If
`SUBSONIC_PASSWORD` is absent, the script prompts without echoing it.

```bash
export SUBSONIC_BASE_URL=http://127.0.0.1:4533
export SUBSONIC_USERNAME=qualification-user
read -rs SUBSONIC_PASSWORD
export SUBSONIC_PASSWORD
python3 tools/tests/live_subsonic_smoke.py
unset SUBSONIC_PASSWORD
```

Set `ALLSTARR_SUBSONIC_BASE_URL` only for a dedicated Allstarr-backed instance;
the script then compares required response shapes without recording values.
Playlist writes require both an explicit option and confirmation. The script
creates one unique playlist, verifies its exact returned ID and name before
every cleanup attempt, and never deletes by name or search result.

```bash
SUBSONIC_STATEFUL_CONFIRM=create-and-delete-throwaway-playlist \
python3 tools/tests/live_subsonic_smoke.py --stateful
```
