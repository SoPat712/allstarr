# Latest Session Work

## Completed delivery — 2026-08-19

- Completed all phases of the rich-dashboard and provider-parity package and deployed application revision `e42f38deaa2047b8e4f3e9850e1bf09aad715efb` to both Jellyfin and Subsonic stacks.
- Matching now queries verified identities plus title+artist, title+album, title, artist, and album; deduplicates the union; and scores it through the existing decision engine. `Crush` by Selena Gomez & The Scene guards the weak-artist regression.
- The review dialog exposes every credible candidate, final and raw confidence, every component score, reasons, warnings, normalized titles, source/candidate ISRCs, artist overlap, album evidence, duration delta, route, and all provider IDs through an accessible disclosure. Manual provider search exposes the same available scoring evidence.
- Cached/Kept distinguish indexed, referenced, and diagnostic files. Cache-mode full streams publish only completed managed artifacts; unknown and interrupted files are never adopted.
- External relationships are provider-neutral in Jellyfin and Subsonic. Lyrics use source-native lookup, Odesli identity translation, and distinct configured fallbacks without fetching media merely for lyrics.
- The live Jellyfin kit exact-compares native music counts in addition to existing full native objects, native artwork bytes, dynamic external traversal, external playback/lyrics, Finer query-key file, Musiver playlist shape, Feishin-class headers, and WebSocket sessions.
- Replaced an unawaited download-sidecar test with a public, awaited PostgreSQL behavior test that proves the exact audio file, adjacent lyrics sidecar, and durable mapping are removed together.
- The final provider gateway fix publishes songs only from implementations with a usable streaming or download-backed route. Metadata-only Apple and Spotify extensions remain available for enrichment and lyrics without creating unplayable Jellyfin/Subsonic audio rows.
- Local release gates pass: WebUI check clean, unit 46/46, build/budgets green at 46.2 KiB initial JavaScript and 22.7 KiB CSS, browser 92/92; PostgreSQL fast lane 2,213/2,213; release-critical 104/104; Apple gateway 20/20; format clean; three Compose profiles valid; deterministic Subsonic 5/5; release-manifest self-tests 2/2; shell syntax and diff checks clean.
- The fast PostgreSQL lane has no test above three seconds. Release-critical deliberately retains the measured migration, rollback, lineage, state-transfer, backup/restore, clone-pool, and 10,000-track contracts.
- Local sandboxed .NET test processes cannot create MSBuild named pipes, so the final fast lane ran with approved escalation against the tmpfs-only OrbStack PostgreSQL container `allstarr-codex-fastlane-b9c962`. The exact container was removed after verification.
- GitHub Actions run `32286289552` is green for the exact deployed revision across every required job.
- Live Jellyfin qualification passed 181/181 provider/client checks, 194/194 actor-bound checks including the exact private throwaway playlist lifecycle and cleanup, and 5/5 WebSocket checks.
- Live OpenSubsonic/Navidrome qualification passed 67/67 checks. Browser-only responsive qualification passed 27/27 route/viewport checks with no console errors.
- Both server checkouts are clean at `e42f38de`; both app containers are healthy on image `sha256:dc0bb67a64d747009a0776c8ff242854cc38e4215c6a180894faf731568ba4fc`.
- The disposable PostgreSQL container and temporary redacted live-report directories were removed. No unrelated service or provider playlist was changed.

## Next entry point

No remaining action in this package.
