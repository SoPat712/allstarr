# Latest Session Work

## AudioMuse built-in correction and truthful import receipts — deployed, 2026-08-20

- AudioMuse is a built-in Allstarr Intelligence integration, not an extension. Intelligence → Automation owns its self-hosted server URL, optional API token, and optional AudioMuse server selector.
- The new typed adapter covers health, recommendations, similarity, text/lyrics search, paths, blends, map pages, clustering playlists, and analysis jobs while retaining exact user/library provider-account scope.
- The configuration action no longer depends on provider readiness: an unconfigured or unhealthy AudioMuse connection can always be created or edited directly in Intelligence.
- Completed history imports are now non-interactive receipts instead of disabled selection rows. Overview shows the effective retention, and a completed receipt with zero currently retained listens explains that the original files must be re-imported.
- Revision `1edd625e6ae20c3eff5e29175a6a870b67fa731f` is pushed and deployed to both Allstarr stacks on `192.168.1.116`; both application containers are healthy on image `sha256:dfa13b6d908101fb7e0325ebd8c5f3b299948fa8d7898f4aad07def3f47a45ac`.
- Local verification: affected non-database .NET 68/68, Svelte diagnostics clean, WebUI unit 46/46, production build and budgets green, and focused responsive browser checks 3/3. The six PostgreSQL import tests were not run because `ALLSTARR_TEST_POSTGRES` was not configured; no storage code changed.
- Live browser verification: Intelligence → Automation exposes the built-in AudioMuse connection and its server URL, optional token, and optional music-server fields without an extension prerequisite. Intelligence → Import shows 20 completed receipts, no unusable include checkboxes, and a truthful re-import warning because no imported listens are currently retained. The browser console contains no errors.

## Unlimited history display and retention — deployed, 2026-08-20

- Revision `218a703fe74180d835c7c2398b554891edcc13e4` is pushed and deployed to both Allstarr stacks on `192.168.1.116`; both clean checkouts and both application containers use that revision.
- Intelligence Overview and History now open on **All time** and omit `from`/`to` query bounds until the user chooses a finite or custom range.
- The backend accepts valid history reporting windows longer than ten years, eliminating `listening_history_period_invalid` for legitimate old imports.
- Retention remains separately controlled: `0` is still the default and means unlimited. Existing saved policies are not rewritten, and no history is deleted unless the user chooses a finite retention or explicitly clears/removes it.
- Verification: focused .NET contract 2/2, Svelte diagnostics clean, unit 46/46, production build and budgets green at 47.3 KiB initial JavaScript and 23.8 KiB CSS, and focused mobile/desktop browser checks 2/2.
- Live verification: both application containers are healthy on image `sha256:d717f94c2a15c97befff49f4c4d2ba46162bde1ba0945b4ef64d3915edbea127`; both internal readiness responses report PostgreSQL ready, both trusted-LAN WebUIs return HTTP 200, and neither startup log contains an error- or critical-level entry.

## Superseded AudioMuse extension placement — deployed, 2026-08-20

- Revision `218a703f` first moved the AudioMuse connection task into Intelligence but incorrectly retained an extension prerequisite. The current local correction replaces that model with a built-in typed provider.
- The existing schema-driven `ConnectSourceDialog` remains the single encrypted account editor.
- Services still owns shared audience and diagnostics; Extensions no longer owns AudioMuse installation or permissions.
- Provider schema and accounts are fetched only when Automation is opened, avoiding extra requests on Intelligence Overview, History, Import, and Discover.
- Verification: Svelte diagnostics clean, unit 46/46, production build and budgets green at 47.3 KiB initial JavaScript and 23.8 KiB CSS, and focused mobile/desktop Intelligence plus Services browser flows 4/4.
- The implementation from `18c84037` is included in deployed revision `218a703f`.

## Exact import undo and Spotify video exclusion — 2026-08-20

- Revision `e6e7a2dc59843732753af801ecf956fda63007c8` is pushed and deployed to both Allstarr stacks on `192.168.1.116`; both containers are healthy on image `sha256:d5603bf21c67fdfeab4c788952f0343a7448ec0b89dcf6a0b5f03964775ea030`.
- Completed imports now expose a confirmed **Undo import** action. Removal is scoped by tenant, user, backend, library, and exact import provenance, and deletes only that import's stored listens, delivery checkpoints, durable import record, and temporary artifact. Active imports must be cancelled first.
- Spotify `Streaming_History_Video_*` files are rejected before staging with guidance to choose `Streaming_History_Audio` JSON files.
- Verification: focused .NET/PostgreSQL 8/8, Svelte diagnostics clean, unit 46/46, production build and budgets green, and focused browser interaction 1/1. The disposable local PostgreSQL container was removed. Both live readiness endpoints are green, deployed UI assets contain the undo action, and startup logs contain no failure-level entries.

## Unlimited listening history and truthful imports — 2026-08-19

- Revision `9336e6c7c9e62d2cb10312a20c2b2587b127d243` is pushed and deployed to both Allstarr stacks on `192.168.1.116`; both containers are healthy on image `sha256:2b7a5d3a827acd0ccaffd6291497457c4f0e87c8070230868fe410331863a20a`.
- Listening-history retention now supports `0` as unlimited. New policy defaults are unlimited, while existing saved policies are preserved until the user changes them.
- Import previews and apply jobs use the exact saved retention policy. Finite policies show rows outside retention before apply, and completed imports are loaded from durable storage after navigation or restart.
- Live verification showed the existing `joshp / Music` policy remains `10 years`, the new `Unlimited` option is available, and all prior completed Spotify import records are visible under Intelligence → Import.
- Shared disclosures now have explicit labels and consistent affordances; the mobile extension-permission dialog keeps confirmation and actions visible; trusted LAN extension installation no longer requires the remote-install switch.
- Verification: focused .NET/PostgreSQL 49/49, Svelte diagnostics clean, unit 46/46, production build and budgets green at 47.3 KiB initial JavaScript and 23.8 KiB CSS, and focused browser 5/5. The exact disposable PostgreSQL container was removed.

## Delivered dashboard refinement — 2026-08-19

- Application revision `f951adef2d45ca1d2582ccf1a0e3f8d1b9940649` is pushed and deployed to both Allstarr stacks on `192.168.1.116`.
- The five post-delivery WebUI commits clarify Intelligence imports and controls, adopt the Material control-room system, streamline Home and mobile matching, align storage tables and filter labels, and keep nested segmented tabs from scrolling the page away from its heading.
- Current exact-source evidence: Svelte diagnostics clean, unit 46/46, production build and budgets green at 47.2 KiB initial JavaScript and 23.5 KiB CSS, and browser 93/93 without retries.
- Responsive light-theme screenshots were reviewed at 390×844 and 1280×800. The Integrations heading remains fully visible when the off-screen Extensions tab activates.
- The live follow-up smoke covered 12 desktop routes and 8 mobile routes with no overflow or console errors. It exposed stale durable mappings in Home's managed-audio count; Home now counts only mapped files that still exist, and its `0 cached · 0 kept` result agrees with both storage inventories.
- The focused storage regression lane passed 7/7. GitHub Actions run `32323295754` is green across WebUI, build/test, release-critical, format, Apple, Compose, and release-manifest jobs.
- Both application containers are healthy on image `sha256:ed0affbd62d1c177c3fbb5cfe9739a39602274aaff5cd8be211c19d56e1b208b`; PostgreSQL and all provider sidecars were left running and Navidrome was not modified.
- Reproducible dependencies, build output, and test artifacts are removed after verification to keep the checkout near 77 MiB.

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

This package is complete. Preserve the unrelated dirty files and begin a new scoped package for any newly observed behavior.
