# Project Progress

## Deployment status

The client-parity, remote-control, and live-playback package is complete.

- Runtime revision: `c6893cd82e073c5b1f1d021867bcd4fb639676c3`
- Branch: `dev`
- Deployment: live and healthy on `192.168.1.116`
- CI: run `31451827156` passed every required job

## Delivered

- Exact native Jellyfin object, artwork, lyrics, and bounded-stream comparisons.
- Jellyfin music-client remote-control allowlist and WebSocket frame, auth, close, and session coverage.
- Tenant-scoped admin Now Playing API with user, client, device, source, progress, and scrobble state.
- Responsive Home Now Playing rail using the existing playback and scrobble owners.
- Expanded playlist, provider, injected-object, range, cancellation, concurrency, and timing smoke coverage.

## Verification

- .NET: 2,278 passed; state-transfer: 90 passed; Release build and format clean.
- WebUI: 44 unit and 129 browser tests passed; check, build, and budgets passed.
- Apple gateway: 19 passed; Compose profiles and seven helper tests passed.
- Read-only live smoke: 199 checks, 0 failures, 4 unavailable fixtures.
- Guarded throwaway-playlist smoke: 218 checks, 0 failures, 3 unavailable fixtures; direct Jellyfin confirmed deletion with `404`.
- Browser smoke passed at phone, tablet, and desktop widths with no overflow or console errors.

## Known live fixture limits

- The test user had no visible injected playlist.
- The selected external songs had genuine lyric misses.
- The YouTube Music extension omitted an album relationship ID for its sampled item.
- External playback remains for user-run Feishin testing; no automated Feishin or Computer Use was used.
