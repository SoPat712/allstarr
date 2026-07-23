# Allstarr v3 Beta Stabilization Plan

## Release goal

Ship a beta focused on provider-backed playlist intake, local-library-first matching,
provider fallback playback/downloads, extension management, and observable operations.
Defer full music-library organization and Beets-style file management.

## Active implementation checklist

Items remain unchecked until the combined local and browser verification pass.

- [ ] Make playlist list rows and detail dialogs consume one canonical coverage result.
- [ ] Expose provider-level coverage and render the accessible segmented coverage bar.
- [ ] Split the Home playlist inventory into Managed and Unmanaged totals.
- [ ] Enforce the mandatory compact icon rail without a collapse control at tablet widths.
- [ ] Remove nested horizontal scrolling from playlist details at 776px and below.
- [ ] Audit duplicate playback, matching, cache, and task/job implementations.
- [ ] Run the combined build, targeted tests, and desktop/tablet/mobile browser checks.
- [ ] Push and deploy the verified batch, then run rematch and server-log checks.
- [ ] Check CI and formatting together at the end and repair any remaining failures.

## Product and WebUI

- Apply `webui-design.md` to every route, modal, loading, empty, error, and responsive
  state.
- Consolidate Library into Playlists, Mappings, Cached, and Kept.
- Consolidate Settings into General, Accounts, Provider routing, Extensions, and
  Maintenance.
- Make navigation behavior deterministic by viewport: wide layouts may use the full
  sidebar, compact desktop/tablet layouts such as 776px must use the icon-only rail
  without an expand/collapse button, and phone layouts must use the hamburger drawer.
  Never let sidebar preference override the mandatory compact rail.
- Split the Home playlist summary into equal Managed and Unmanaged halves. Managed
  counts playlists controlled or synchronized by Allstarr; Unmanaged counts playlists
  discovered on the active media server but not controlled by Allstarr. Derive both
  from one canonical inventory and make each half link to its filtered playlist view.
- Replace duplicate/manual playlist entry surfaces with the provider-aware four-step
  wizard and provider/media-server artwork.
- Make playlist details responsive at compact desktop/tablet widths, not only phone
  widths. At 776px the track list must use a fitted compact layout with no nested
  horizontal scrollbar, clipped provider badge, or off-screen action column.
- Redesign mapping review with artwork, provider identity, confidence, reasons,
  interactive local/provider candidate selection, and technical details.
- Redesign Event log as a grouped timeline with artwork, readable event descriptions,
  outcome pills, source-to-target routes, and expandable identifiers.

## Providers, accounts, and extensions

- Support playlist discovery from every enabled provider or extension exposing the
  Playlist capability, including Apple Music Music User Token accounts.
- Existing account configuration must expose owner and audience: only me, everyone,
  or one library. Shared accounts show the media-server owner identity.
- Distinguish or deduplicate built-in and extension routes with stable route IDs.
- Stage extension packages temporarily, review permissions first, and atomically
  install/enable only after approval. Cancel, denial, or failure removes staging.
- Support extension update, disable, uninstall, registry dependency errors, grouped
  capabilities, and readable activity.

## Matching and playlists

- Use one canonical playlist summary for list rows and detail dialogs.
- Inventory every path that classifies a track as local, externally playable, or
  unmatched. Consolidate summary APIs, detail APIs, sync/rematch jobs, playback
  routing, cache restoration, mappings, and event generation behind one shared
  classification contract. Remove duplicate or legacy paths only after parity tests
  prove the replacement preserves current valid decisions.
- Inventory duplicate background tasks and job handlers across controllers, hosted
  services, queues, and extension bridges. Give each operation one owner, one
  idempotency model, one progress/event stream, and one retry policy instead of
  parallel task implementations for the same work.
- Enforce metric-generation consistency: a playlist row and its open detail dialog
  must report the same local, external, playable, matched, and unmatched counts from
  the same source snapshot and completed match generation. Refresh/rematch must
  invalidate or replace stale summary caches atomically. Require
  `total = local + external + unmatched` and add regression coverage for the observed
  `0/30` row versus `25/5` dialog discrepancy.
- Replace the playlist table's text-only status cell with an accessible segmented
  coverage bar backed by canonical provider counts. Render local Jellyfin matches in
  Jellyfin purple, Spotify matches in Spotify green, Apple Music matches in Apple red,
  other providers in their registered brand color, and unmatched tracks in neutral
  gray. Preserve the status label and expose exact counts/percentages in text and
  hover/focus details.
- Search the local library first, then every eligible provider in configured order.
- Use ISRC plus normalized title, artist, album, duration, and conservative alternate
  queries; record rejection and fallthrough reasons.
- Import valid v2 mappings as canonical mappings. Convert unavailable legacy routes
  to unresolved review items and rematch them without deleting evidence.
- Run a complete rematch after deployment and investigate the residual unmatched set,
  including punctuation, stylization, and examples such as `Pillowtalk`/`P1ll0wtalk`.

## Diagnostics, performance, and operations

- Measure endpoint latency and expose four-bar connectivity with exact timings.
- Run cold CTS checks with rotating known tracks, cleared request/media caches, bounded
  reads, quality metadata, and no retained media.
- Audit hot paths for repeated database/provider work, N+1 calls, unbounded loops,
  blocking I/O, and duplicate metadata searches.
- Record matching, caching, streaming, playlist, scrobble, extension, and administrative
  events with artwork and stable identifiers.
- Review application, worker, database, extension, and container logs; fix reproducible
  errors and classify external authentication, rate limiting, and outages.

## Release gates

- Unit/integration coverage for every changed endpoint and lifecycle.
- Browser coverage for every screen and modal at desktop, tablet, and mobile sizes.
- CI format, build, test, migration, and container checks pass.
- No recurring unexplained errors, false playlist metrics, route-stale dialogs,
  permission-bypassing installs, or secret exposure.
- Deploy, rematch, and verify representative Jellyfin and provider playlists before
  publishing the beta.
