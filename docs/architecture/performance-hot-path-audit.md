# Performance hot-path audit

Scope: playlist summary/detail APIs, automatic matching, provider search, extension
bridges, cache side effects, and canonical playlist orchestration. This audit identifies
work to measure and remove before the beta release; it does not treat lower latency from
one warm run as proof.

## Findings

| Priority | Hot path and evidence | Risk | Required remediation |
| --- | --- | --- | --- |
| P0 | `PlaylistController` calls `SpotifyMappingService.GetMappingAsync` inside per-track loops in summary and detail paths | N+1 Redis/database reads and divergent counts for the same playlist | Load one source snapshot and batch all mapping identities through the canonical mapping repository before classification |
| P0 | `SpotifyTrackMatchingService` builds local candidates with nested Jellyfin-track × Spotify-track loops | Quadratic CPU and allocation growth on large libraries/playlists | Index local tracks once by ISRC, provider ID, normalized title/artist tokens, then retrieve bounded candidate sets |
| P0 | `SpotifyTrackMatchingService` can issue ISRC, full-query, stripped/title-only, fallback, and per-provider searches for one source track | Duplicate network work, rate-limit pressure, and inconsistent candidates | Route all queries through one per-run candidate cache keyed by provider, capability, normalized query, and policy version |
| P1 | `PlaylistController` requests `SpotifyPlaylistFetcher.GetPlaylistTracksAsync` independently in multiple summary/fallback branches | Repeated source parsing/fetching and stale branch disagreement | Resolve one immutable playlist snapshot per request and pass it to summary/detail classification |
| P1 | `SpotifyPlaylistFetcher` and `SpotifyTrackMatchingService` walk playlists serially with fixed delays while durable jobs also exist | Long full-rematch wall time, no shared backpressure, duplicate scheduling | Use the durable queue with provider-scoped bounded concurrency and rate-limit-aware retry-after scheduling |
| P1 | `MultiProviderMetadataService` wraps synchronous extension SDK calls in `Task.Run`; `ExtensionManager`, signed-session, and artifact bridges use synchronous waits | Thread-pool starvation and request-thread blocking under concurrent searches | Add async extension capability contracts or isolate synchronous modules behind a bounded dedicated worker pool |
| P2 | `BaseDownloadService`, Deezer, Qobuz, and SquidWTF launch fire-and-forget `Task.Run` cache/metadata work | Unobserved failures and unconstrained background work | Use one bounded best-effort side-effect queue; durable user-visible downloads remain durable jobs |
| P2 | `JellyfinSessionManager` blocks briefly with `WebSocket.CloseAsync(...).Wait(...)` during shutdown | Shutdown thread blocking and hidden timeout behavior | Await close from async disposal with an explicit cancellation timeout |

## Audited non-findings

- `PlaylistOrchestrationService` batches source entries, library candidates, stored
  matches, and overrides before its decision loop, and batches new match persistence.
  Preserve this model as the target path.
- `AppleMusicMetadataService` reads task results only after the grouped asynchronous
  operations complete; the `.Result` access is not an independent blocking request.
- Short CPU-bound extension calls may use a worker boundary, but unbounded `Task.Run`
  fan-out is not an acceptable async implementation.

## Measurement gates

Capture cold and warm runs for representative small and large fixtures:

1. A 50-track playlist against the current server library.
2. All managed playlists in one rematch run.
3. A 1,000-track synthetic playlist against 100,000 indexed library tracks.
4. At least four simultaneously enabled playback providers.

For each run record:

- database command count and total database time;
- mapping repository round trips;
- provider requests grouped by provider, capability, and normalized query;
- wall-clock and CPU time;
- peak allocated bytes and process resident memory;
- durable retries, rate-limit waits, cancellation latency, and failed side effects.

## Acceptance criteria

- Summary and detail endpoints perform a bounded number of mapping/database queries
  independent of playlist track count.
- Local candidate generation is index-based, not a library × playlist nested scan.
- A provider receives each normalized candidate query at most once per rematch run and
  policy version unless its explicit retry policy permits another attempt.
- Full-rematch concurrency is bounded globally and per provider; no fixed sleeps are used
  as the primary rate limiter.
- Request paths contain no synchronous network waits or unbounded `Task.Run` fan-out.
- Canonical summary, detail, playback, and event counts remain identical before and after
  each optimization.

