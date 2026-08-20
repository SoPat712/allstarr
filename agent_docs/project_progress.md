# Project Progress

## Active package — rich control dashboard and provider parity

### Outcome

Turn the existing administrator WebUI into one coherent, data-rich music control dashboard while preserving native Jellyfin/Subsonic fidelity and the existing provider-neutral backend owners.

This is a replacement and consolidation package. Do not create parallel matching, routing, scrobbling, caching, scheduling, or recommendation systems. Delete each old UI surface after its replacement passes.

### Verified delivery state

- Application revision `218a703fe74180d835c7c2398b554891edcc13e4` is pushed and deployed to both Jellyfin and Subsonic stacks.
- Both server checkouts are clean at that revision. Both app containers use image `sha256:d717f94c2a15c97befff49f4c4d2ba46162bde1ba0945b4ef64d3915edbea127` and are healthy; both readiness responses report PostgreSQL ready, both trusted-LAN WebUIs return HTTP 200, and startup logs contain no error- or critical-level entries.
- GitHub Actions run `32323295754` is green across build/test, release-critical, Apple, WebUI, format, Compose, and release-manifest jobs.
- Current WebUI baseline is 47.3 KiB initial JavaScript and 23.8 KiB CSS; unit is 46/46. The import, retention, disclosure, audio-quality, and extension-permission browser slice is 5/5.
- Completed listening-history imports can be undone by exact import provenance; this removes only their stored listens, checkpoints, saved record, and temporary artifact. Spotify `Streaming_History_Video_*` exports are rejected before staging.
- Canvas UI remains blocked by MIT plus Commons Clause redistribution terms. A focused replacement review found no permissive renderer that would delete more code than it adds, so use Svelte 5, shadcn-svelte, Bits UI, Lucide, CSS, SVG, and native Web Animations.
- Koito `a079fa693569d21e03c00df163f20ac5e137c490`, Explo `4fc75874de691ff1e26b10d88b859cfac8ee2992`, and Multi-Scrobbler `bc28de66b14db1c99eb79ad75d1cdf4c9dfff7cc` are MIT reference inputs. Adapt useful behavior and presentation into existing owners; do not import their application architectures.
- LAN/VPN access was enabled only after the local release gates passed, then used for exact-revision deployment and qualification.

### Product rules

- Show every useful supported fact, using overview → expansion → detail so primary screens remain scannable.
- Never display invented popularity, media, listening, readiness, latency, or provider facts.
- Work only from observed production behavior, captured client shapes, public protocol contracts, and donor workflows deliberately adopted here. Do not build speculative edge cases.
- Preserve authentication, authorization, secret redaction, destructive-operation, migration, data-loss, and protocol safety coverage.
- Matched native items relay the complete backend object unchanged. Virtual objects expose every available field with stable, internally consistent relationships.

## Phase 0 — package and donor baseline

- [x] Record exact donor UI paths, adaptation boundaries, licenses, and destination owners in the reference ledger.
- [x] Capture current route request counts, response sizes, render timings, long tasks, bundle sizes, and table geometry.
- [x] Record the old Signal Boot implementation and current shared UI owners before editing.

Acceptance: reproducible before-state evidence exists and no donor code enters production without provenance.

## Phase 1 — shared shell, tables, motion, and visual language

- [x] Restore Signal Boot for real authentication/bootstrap work with no artificial delay, reduced motion, and retryable failure state.
- [x] Standardize desktop rows, artwork, numeric columns, actions, gutters, and mobile cards across playlists, Integrations, Activity, Mappings, Cached, Kept, and Intelligence history.
- [x] Centralize provider colors/icons so Jellyfin, Deezer, Apple, Spotify, YouTube, and extensions remain distinguishable.
- [x] Add restrained native motion for focused artwork, mapping decisions, delivered scrobbles, and expanding detail; never reorder content under the pointer.
- [x] Delete superseded per-page table, loading, icon, and animation CSS.

Acceptance: geometry is aligned at desktop/tablet/mobile, keyboard and reduced-motion behavior pass, and no renderer dependency is added.

## Phase 2 — unified Integrations hub

- [x] Rename Sources to Integrations with Services, Accounts, Extensions, and Routing tabs.
- [x] Group logical services with their built-in, extension, sidecar, backend, and account implementations.
- [x] Put configuration beside the exact implementation; keep lifecycle/permissions in Extensions and secrets/audience in Accounts.
- [x] Move quality, provider ordering, local preference, and extension penalty to Routing; leave only app-wide system/maintenance controls in Settings.
- [x] Add stable service/implementation projection facts and deep links; redirect old Sources/Accounts/Extensions routes.
- [x] Show capability coverage, readiness, latest probe, CTS, managed p95, last failure, account scope, version, and routing priority without ambiguous blanks.
- [x] Delete duplicate provider/account/extension/settings forms after the replacement passes.

Acceptance: one route owns discovery, setup, health, accounts, configuration, extensions, and routing without duplicating durable state.

## Phase 3 — Intelligence and imports

- [x] Replace the five equal tabs with Overview, History & Imports, Discover, and Automation.
- [x] Adapt Koito period controls, heatmap, totals, streaks, top music, history density, and recap presentation.
- [x] Adapt Explo recommendation, generated-playlist, schedule, run-now, next-run, and prior-run presentation.
- [x] Redesign imports as multi-file drag/drop with automatic previews, valid files selected by default, one batch summary, per-file detail, and Add all ready files.
- [x] Add bounded grouped daily/monthly and source/provider/client aggregates using the existing listening-occurrence authority.
- [x] Refresh Overview, History, and Discover after imports and replace generic prerequisites with exact Integrations deep links.

Acceptance: imported and live history agree, recommendations use saved data, and rich analytics require no second database.

## Phase 4 — Home and Activity

- [x] Add one aggregate Home read endpoint composed from existing owners while retaining individual endpoints for compatibility.
- [x] Show active listeners, playlist/playable/unresolved totals, cache/kept usage, playable-source health, jobs, scrobbles, recent trend, top music, and actionable setup problems.
- [x] Expand now playing with user/client/device, actual implementation, cache route, progress, scrobble threshold, and per-target delivery state.
- [x] Reuse existing delivery checkpoints/activity state; add no second persistence or realtime channel.
- [x] Adapt Multi-Scrobbler status, now-playing, retry, and per-target outcome patterns.
- [x] Redesign Activity with colored icons, provider accents, rich filters, concise summaries, retry/auth state, and expandable redacted details.

Acceptance: Home loads through the aggregate plus now-playing update, and every stat and scrobble mark reflects durable state.

## Phase 5 — Mappings, Cached, and Kept

- [x] Search verified identities first, then run title+artist, title+album, title, artist, and album queries and score the deduplicated union.
- [x] Prevent weak artist mismatches from becoming automatic suggestions and keep every credible candidate accessible with complete scoring evidence.
- [x] Bump the matcher revision and use the existing preview/rematch job for unresolved, suggested, ambiguous, and stale decisions only.
- [x] Add the Selena Gomez `Crush` regression and protect accepted, pinned, rejected, and manual decisions.
- [x] Present Mappings as All, Review, Unresolved, and History with full score/identity/route details on expansion; accepted rows leave Review immediately.
- [x] Add cache/kept totals, provider/lifecycle facts, last access, expiry, quality, publication, references, filters, and previewed bulk actions.
- [x] Reindex only completed files with valid Allstarr ownership metadata; show unknown files as diagnostics and never adopt/delete them automatically.

Acceptance: automatic suggestions are credible, manual decisions remain authoritative, and Cached/Kept agree with managed-file owners.

## Phase 6 — external object, streaming, and lyrics parity

- [x] Use one provider-neutral external relationship projection for primary albums, credited tracks, and Appears On in Jellyfin and Subsonic.
- [x] Keep external IDs, artwork, relationships, pagination, and traversal stable and internally consistent.
- [x] Discover and qualify every ready streaming or download-backed implementation dynamically.
- [x] Verify metadata → PlaybackInfo → bounded audio → range/cancellation → artwork → lyrics while recording the selected implementation/account.
- [x] Preserve configured quality and return a truthful failure instead of silently substituting another track.
- [x] Run source-native lyrics first, then Odesli identity translation and distinct configured fallbacks without downloading media merely to find lyrics.

Acceptance: native objects remain exact, virtual objects satisfy the full client contract, unrelated Appears On albums fail, and real external playback failures are classified.

## Phase 7 — performance, release, and live delivery

- [x] Keep initial JS/CSS within existing budgets and reject unexplained growth above 10%; keep route chunks under 100 KiB gzip.
- [x] Use grouped queries, lazy routes/artwork, keyed row updates, and off-screen content visibility; add no charting/rendering/virtualization framework.
- [x] Expand the Jellyfin kit to union-key native comparison, native artwork/count parity, dynamic external traversal, every ready playable provider, and checked Finer/Feishin/Musiver request shapes.
- [x] Run focused owner tests, WebUI check/unit/build/budget/E2E, both PostgreSQL lanes, format, Apple gateway, Compose, shell, and deterministic protocol kits.
- [x] Ask the user to enable LAN/VPN only after the exact final SHA is locally green.
- [x] After exact-revision authorization, push, deploy, run bounded browser/provider/client qualification, and record the deployed SHA.

Acceptance: local release evidence is complete before LAN access, no unrun gate is called passing, and live failures are separated into provider/configuration versus Allstarr defects.

### Local release evidence

- WebUI: check clean, unit 46/46, build and budgets green at 46.2 KiB initial JavaScript and 22.7 KiB CSS, browser 92/92 without retries.
- Backend: PostgreSQL fast lane 2,213/2,213 and release-critical lane 104/104; the slow release-critical tests remain protected migration, lineage, state-transfer, backup, clone-pool, and 10,000-track contracts.
- Supporting gates: format clean, Apple gateway 20/20, all three Compose profiles valid, deterministic Subsonic 5/5, release-manifest self-tests 2/2, shell syntax clean.
- The live Jellyfin kit now exact-compares native music counts in addition to existing full native objects, artwork bytes, dynamic external traversal, playback, Finer, Feishin, Musiver, and WebSocket contracts.

### Live release evidence

- Jellyfin provider/client qualification: 181 checks, zero failures; Apple GAMDL, Deezer, and YouTube Music delivered bounded audio. Metadata/lyrics-only extensions are no longer advertised as playable tracks.
- Actor-bound Jellyfin qualification: 194 checks, zero failures; the exact private throwaway playlist passed create, rename, add, reorder, remove, share, unshare, mix, delete, and direct 404 cleanup verification.
- Jellyfin WebSocket qualification: 5/5 for header authentication, bidirectional frames, Sessions delivery, and invalid-token rejection.
- OpenSubsonic/Navidrome qualification: 67 checks, zero failures across password/token auth, XML/JSON, playlists, browse/search, artwork, lyrics, exact range bytes, concurrency, cancellation, and direct-vs-Allstarr shape parity.
- Browser-only responsive qualification: 27/27 route/viewport checks across desktop, tablet, and mobile with no overflow, crash state, missing main heading, or console error.
- External sources without range support were retained as truthful bounded progressive delivery rather than falsely advertising seek support.

### Post-delivery WebUI refinement — delivered

- Application revision `f951adef2d45ca1d2582ccf1a0e3f8d1b9940649` includes the Material control-room visual system, clearer Intelligence import controls, denser Home/review/storage workflows, truthful empty-value filter labels, horizontal-only segmented-tab activation, and truthful managed-audio totals that ignore missing mapped files.
- The segmented-tab fix prevents nested mobile tabs from moving the document vertically. The browser regression waits for mounted content and settled layout, then proves zero document scroll and a fully visible page heading.
- Exact-revision WebUI evidence: Svelte diagnostics clean, unit 46/46, production build and budgets green at 47.2 KiB initial JavaScript and 23.5 KiB CSS, browser 93/93 without retries, design detector clean, and responsive light-theme screenshots reviewed at 390×844 and 1280×800.
- Two proposed shared-control/artwork changes were measured and discarded before commit: loading a Bits UI checkbox into the root shell raised initial JavaScript to 69.6 KiB, and blank provider logos were traced to the test fixture's intentionally empty SVG rather than production assets.
- Live browser qualification covered 12 desktop routes and 8 mobile routes with no document overflow, console error, or heading displacement. Home now reports `0 cached · 0 kept`, matching the live Cached and Kept inventories.

### Post-delivery AudioMuse setup placement — built-in correction verified locally

- [x] Register AudioMuse as a built-in Intelligence and health capability; it is not an extension package.
- [x] Keep its self-hosted server URL, optional API token, and optional multi-server selector in Intelligence → Automation.
- [x] Reuse the encrypted Source account dialog and durable account store; add no second credential store.
- [x] Perform real account-bound `/api/health` checks and route recommendation, search, path, blend, map, clustering-playlist, and analysis calls through the typed built-in adapter.
- [x] Keep Services available for shared account audience and diagnostics without making Extensions a prerequisite.
- [x] Verify the correction locally: affected non-database .NET 68/68, Svelte diagnostics clean, WebUI unit 46/46, production build and budgets green, and focused mobile/desktop browser checks 3/3.
- [ ] Commit, push, deploy, and verify the exact revision on both Allstarr stacks.

### Post-delivery unlimited history range — deployed

- [x] Default Overview and History reporting to **All time**, with no `from`/`to` bounds sent until the user chooses a finite or custom range.
- [x] Remove the artificial ten-year reporting-window rejection while retaining ordered-date validation.
- [x] Keep listening retention defaulted to `0` (unlimited); do not migrate saved user choices or delete retained history.
- [x] Preserve finite 30-day, 90-day, one-year, and custom reporting choices.
- [x] Verify the default/unbounded contracts: focused .NET 2/2, Svelte diagnostics clean, unit 46/46, production build and budgets green, and focused responsive browser checks 2/2.
- [x] Commit the implementation as `218a703f`.
- [x] Push and deploy the exact revision after authorization.

## Next action

Commit and deploy the verified built-in AudioMuse correction and truthful import receipts, then verify the live Intelligence form and retained-history state.
