# Allstarr v3 Beta Stabilization Plan

## Release goal

Ship a beta focused on provider-backed playlist intake, local-library-first matching,
provider fallback playback/downloads, extension management, and observable operations.
Defer full music-library organization and Beets-style file management.

## Active implementation checklist

Items remain unchecked until the combined local and browser verification pass.

- [x] Make playlist list rows and detail dialogs consume one canonical coverage result.
- [x] Expose provider-level coverage and render the accessible segmented coverage bar.
- [x] Split the Home playlist inventory into Managed and Unmanaged totals.
- [x] Enforce the mandatory compact icon rail without a collapse control at tablet widths.
- [x] Remove nested horizontal scrolling from playlist details at 776px and below.
- [ ] Audit duplicate playback, matching, cache, and task/job implementations.
- [x] Run the combined build, targeted tests, and desktop/tablet/mobile browser checks.
- [x] Push and deploy the verified batch, then verify container health and server logs.
- [ ] Run a full rematch and investigate the residual unmatched set.
- [ ] Fix or correctly classify the actionable post-deploy log signals.
- [x] Check CI and formatting together at the end and repair any remaining failures.

## Product and WebUI

- [ ] Apply `webui-design.md` to every route, modal, loading, empty, error, and responsive
  state.
- [ ] Consolidate Library into Playlists, Mappings, Cached, and Kept.
- [ ] Consolidate Settings into General, Accounts, Provider routing, Extensions, and
  Maintenance.
- [x] Make navigation behavior deterministic by viewport: wide layouts may use the full
  sidebar, compact desktop/tablet layouts such as 776px must use the icon-only rail
  without an expand/collapse button, and phone layouts must use the hamburger drawer.
  Never let sidebar preference override the mandatory compact rail.
- [x] Split the Home playlist summary into equal Managed and Unmanaged halves. Managed
  counts playlists controlled or synchronized by Allstarr; Unmanaged counts playlists
  discovered on the active media server but not controlled by Allstarr. Derive both
  from one canonical inventory and make each half link to its filtered playlist view.
- [ ] Replace duplicate/manual playlist entry surfaces with the provider-aware four-step
  wizard and provider/media-server artwork.
- [x] Make playlist details responsive at compact desktop/tablet widths, not only phone
  widths. At 776px the track list must use a fitted compact layout with no nested
  horizontal scrollbar, clipped provider badge, or off-screen action column.
- [ ] Redesign mapping review with artwork, provider identity, confidence, reasons,
  interactive local/provider candidate selection, and technical details.
- [ ] Redesign Event log as a grouped timeline with artwork, readable event descriptions,
  outcome pills, source-to-target routes, and expandable identifiers.

## Providers, accounts, and extensions

- [ ] Support playlist discovery from every enabled provider or extension exposing the
  Playlist capability, including Apple Music Music User Token accounts.
- [ ] Existing account configuration must expose owner and audience: only me, everyone,
  or one library. Shared accounts show the media-server owner identity.
- [ ] Distinguish or deduplicate built-in and extension routes with stable route IDs.
- [ ] Stage extension packages temporarily, review permissions first, and atomically
  install/enable only after approval. Cancel, denial, or failure removes staging.
- [ ] Support extension update, disable, uninstall, registry dependency errors, grouped
  capabilities, and readable activity.

## Matching and playlists

- [x] Use one canonical playlist summary for list rows and detail dialogs.
- [ ] Inventory every path that classifies a track as local, externally playable, or
  unmatched. Consolidate summary APIs, detail APIs, sync/rematch jobs, playback
  routing, cache restoration, mappings, and event generation behind one shared
  classification contract. Remove duplicate or legacy paths only after parity tests
  prove the replacement preserves current valid decisions.
- [ ] Inventory duplicate background tasks and job handlers across controllers, hosted
  services, queues, and extension bridges. Give each operation one owner, one
  idempotency model, one progress/event stream, and one retry policy instead of
  parallel task implementations for the same work.
- [x] Enforce metric-generation consistency: a playlist row and its open detail dialog
  must report the same local, external, playable, matched, and unmatched counts from
  the same source snapshot and completed match generation. Refresh/rematch must
  invalidate or replace stale summary caches atomically. Require
  `total = local + external + unmatched` and add regression coverage for the observed
  `0/30` row versus `25/5` dialog discrepancy.
- [x] Replace the playlist table's text-only status cell with an accessible segmented
  coverage bar backed by canonical provider counts. Render local Jellyfin matches in
  Jellyfin purple, Spotify matches in Spotify green, Apple Music matches in Apple red,
  other providers in their registered brand color, and unmatched tracks in neutral
  gray. Preserve the status label and expose exact counts/percentages in text and
  hover/focus details.
- [ ] Search the local library first, then every eligible provider in configured order.
- [ ] Use ISRC plus normalized title, artist, album, duration, and conservative alternate
  queries; record rejection and fallthrough reasons.
- [ ] Import valid v2 mappings as canonical mappings. Convert unavailable legacy routes
  to unresolved review items and rematch them without deleting evidence.
- [ ] Run a complete rematch after deployment and investigate the residual unmatched set,
  including punctuation, stylization, and examples such as `Pillowtalk`/`P1ll0wtalk`.

## Diagnostics, performance, and operations

- [ ] Measure endpoint latency and expose four-bar connectivity with exact timings.
- [ ] Run cold CTS checks with rotating known tracks, cleared request/media caches, bounded
  reads, quality metadata, and no retained media.
- [ ] Audit hot paths for repeated database/provider work, N+1 calls, unbounded loops,
  blocking I/O, and duplicate metadata searches.
- [ ] Record matching, caching, streaming, playlist, scrobble, extension, and administrative
  events with artwork and stable identifiers.
- [ ] Review application, worker, database, extension, and container logs; fix reproducible
  errors and classify external authentication, rate limiting, and outages.
- [ ] Add bounded retry/backoff and correct severity for the repeating missing-track-file
  poll instead of logging the same warning every few seconds.
- [ ] Give extension runtime failures safe structured error codes and provider context;
  opaque redacted error messages are not actionable. Investigate the observed Amazon
  search `403` and Spotify client-token `400` without exposing credentials.
- [ ] Do not initialize or report disabled/missing scrobbling credentials during unrelated
  asset requests such as `/favicon.ico`. Health warnings must belong to the relevant
  account or capability check.

## Release gates

- [ ] Unit/integration coverage for every changed endpoint and lifecycle.
- [ ] Browser coverage for every screen and modal at desktop, tablet, and mobile sizes.
- [x] CI format, build, test, migration, and container checks pass.
- [ ] No recurring unexplained errors, false playlist metrics, route-stale dialogs,
  permission-bypassing installs, or secret exposure.
- [ ] Deploy, rematch, and verify representative Jellyfin and provider playlists before
  publishing the beta.

## Database-backed mapping and cache consolidation

- [ ] Confirm PostgreSQL is the durable source of truth for canonical recordings, provider routes, accepted/rejected mappings, and reusable track-match decisions; no mapping needed after restart may exist only in Valkey or process memory.
- [ ] Inventory every `RedisCacheService` key and classify it as durable state, derived cache, coordination/lock state, transient session state, or disposable telemetry, including TTL, typical payload size, read/write rate, and current fallback behavior.
- [ ] Remove duplicate mapping stores and route all matching, rematching, playlist playback, and event-log lookups through one repository contract backed by PostgreSQL.
- [ ] Design a pluggable cache backend (`PostgreSQL`, `Valkey`, or bounded in-process memory) so a single-container/small-install deployment can run without Valkey while larger installs can retain it when benchmarks justify it.
- [ ] Implement the PostgreSQL cache backend with explicit expiry, bounded cleanup, namespaced keys, payload-size limits, concurrency-safe upserts, and indexes that prevent cache maintenance from degrading durable application queries.
- [ ] Keep cache records reconstructable and disposable; dropping or expiring the PostgreSQL cache must never delete canonical mappings, user decisions, playlist links, accounts, or kept-media metadata.
- [ ] Add an automated migration path that preserves reusable mappings, warms only valuable hot entries, and permits rollback between Valkey and PostgreSQL cache modes without data loss.
- [ ] Benchmark cold/hot lookup latency, rematch throughput, database CPU/I/O, total resident memory, and container overhead on the target server before choosing the beta default; document when Valkey remains beneficial.
- [ ] Add restart, cache-loss, concurrent-rematch, expiry, and backend-parity tests proving PostgreSQL-only operation produces the same playback and matching decisions.

## Playlist detail visual semantics

- [x] Render the actual target backend icon in the playlist-detail target summary for Jellyfin, Subsonic/Navidrome, and future targets; do not substitute a generic library glyph when a branded target icon exists.
- [x] Derive the playable summary icon/accent from the playable ratio using the shared semantic scale (red through amber to green), with accessible text/contrast and no color-only status communication.
- [x] Apply the target-icon and playable-ratio treatment consistently at desktop, compact-sidebar, tablet, and mobile modal widths without changing the authoritative coverage calculation.
- [ ] Redesign the synchronization summary as a compact, scannable operation strip with stronger grouping for local, external, unmatched, source-refreshed, last-synced, and next-rematch values; preserve exact values and accessible labels without presenting six equal-weight boxes.
- [ ] Replace the basic playlist-detail track cards/table with one responsive streaming-style track component: artwork, title, explicit badge when applicable, artist, album, duration, source and playable-provider identity, and only the metadata available from the canonical recording/provider route.
- [ ] Include useful secondary track data on demand, including ISRC, source/provider track IDs, target item ID, codec, bitrate, bit depth/sample rate, match confidence, and route provenance without overcrowding the default row.
- [ ] Center the overflow action within a fixed touch target, use a vertical-ellipsis icon, prevent row-click handling from swallowing the action, and open the same accessible mapping/action menu on pointer and keyboard input.
- [ ] Preserve streaming-service information hierarchy on compact widths instead of converting each track into a definition list; keep artwork and primary identity together, move secondary metadata below, and avoid nested horizontal scrolling.
- [ ] Use the shared track-row component in playlist detail, mapping review, cached media, kept media, and event details so artwork, duration, provider badges, overflow actions, and responsive behavior are not reimplemented.
