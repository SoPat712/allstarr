# Allstarr v3 Beta Stabilization Plan

## Release goal

Ship a beta focused on provider-backed playlist intake, local-library-first matching,
provider fallback playback/downloads, extension management, and observable operations.
Defer full music-library organization and Beets-style file management.

## Product and WebUI

- Apply `webui-design.md` to every route, modal, loading, empty, error, and responsive
  state.
- Consolidate Library into Playlists, Mappings, Cached, and Kept.
- Consolidate Settings into General, Accounts, Provider routing, Extensions, and
  Maintenance.
- Replace duplicate/manual playlist entry surfaces with the provider-aware four-step
  wizard and provider/media-server artwork.
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
