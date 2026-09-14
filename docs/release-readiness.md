# Allstarr: feature inventory and first-release plan

Assessment date: 2026-09-12. Reference commit: `adf3c585a3b97b025dfa65f99a33b4703c543183`.

This is a dated engineering assessment and proposed plan, not a statement that every described feature is released or qualified. It inventories the working tree, examines Git churn, and traces the main listening, acquisition, playlist, account, and recovery paths. It is a repository-wide inventory with focused code review of those paths, not a line-by-line review of every file. The 2026-09-13 addendum records the maintainer's Intelligence scope decision, the multi-source routing proposal, a WebUI audit, and a small navigation implementation. No deployment was performed for either assessment.

## Product contract

**Allstarr should help people listen to and grow their own music library.** It preserves their existing Jellyfin or Subsonic client, supplies missing recordings through secondary providers, and lets them explicitly acquire music into storage their media server can index.

The first release should make five promises:

1. Existing local music remains easy to find and play, including when an optional provider is unavailable.
2. External search and playback complement the local library with accurate recording identity and clear availability.
3. A user can keep an external song, then see whether it is downloaded, awaiting indexing, or available locally.
4. Playlists prefer qualified local recordings and retain their source order and user decisions.
5. Accounts, retained files, and background work respect user/library ownership and survive ordinary restarts and recovery.

Successful acquisition should have this observable lifecycle:

```text
External recording
  → explicit keep request
  → authorized durable download
  → verified, tagged file in the configured managed root
  → backend scan requested
  → native backend item observed and identity reconciled
  → subsequent playback uses the local library
```

The final steps are part of the product outcome. A successful file copy or accepted scan request does not establish that the recording is in the library.

Local-first must preserve recording correctness. The current matcher gives local candidates a seven-point preference window, with external provider windows of five, three, and one point; it does not make every local candidate win. Preserve manual pins/rejections and the raw acceptance threshold. For release, explicitly verify that an accepted local identity is reused and that acquisition can transition an external identity to that native item. Do not silently change the established matching policy during cleanup. See [`TrackMatchDecisionEngine`](../allstarr/Core/Matching/TrackMatchDecisionEngine.cs).

## First-release scope decision (2026-09-13)

**Maintainer decision: do not release the Intelligence workspace with the core application.** First-release navigation is Home, Library, Integrations, Activity, and Settings. Mobile exposes Home, Library, Activity, and More; More owns Integrations and Settings. Personal provider-playlist import stays in Library: it is different from importing listening history into Intelligence.

- **Implemented now:** remove Intelligence/Insights from the shared desktop, mobile, and More navigation. Mobile tracks size themselves from their contents instead of assuming five buttons. Keep the existing visual system and both themes.
- **Preserved now:** the direct `#/intelligence` route, its tests, existing accounts, history, data controls, and opt-in background jobs. This is a reversible navigation change, not a feature shutdown or data migration. The route is still bundled and the AudioMuse configuration link still reaches it; navigation hiding is neither authorization nor complete release exclusion.
- **Required before publication:** omit the deferred workspace from the release route/bundle, provide an explanatory destination for old links, and remove or label its remaining entry points. Inspect Services/AudioMuse, Home listening aggregates, configuration, onboarding, and public capability copy together. Retain core now-playing and independent scrobble delivery; do not accidentally disable playback observation needed by those features. Use one explicit release boundary, not scattered component flags.
- **Existing-user safety:** inventory scheduled recommendations, import jobs, listening-app keys, and consent/data-management dependencies before changing runtime registration. Specify how existing users export/manage their data or continue using an explicit development build. Do not purge history, revoke credentials, or silently change opt-in collection because a tab disappeared.
- **Separate later milestone:** qualify direct-listen history, imports, recommendations, AudioMuse, feedback, ephemeral playlists, and automation together. These are not blockers for the core release once safely isolated, and are not core-release marketing promises.

## Canonical recordings and multi-source routing (proposed)

Evolve the existing [identity service](../allstarr/Core/Matching/TrackIdentityService.cs), [router](../allstarr/Core/Routing/ProviderRouter.cs), and [protocol gateway](../allstarr/Core/Protocols/ProtocolProviderGateway.cs); do not build a second routing provider. They already separate provider identities, scoped capability/account eligibility, and stream leases. The missing product contract is **one recording with several verified playable sources**, rather than a provider-branded song that loses its alternatives when one candidate wins.

| Responsibility | Planned behavior | Acceptance condition |
| --- | --- | --- |
| Matching | Persist qualified alternatives and the evidence tying each to the recording. Keep manual authority and rejected candidates explicit. | An unavailable winner does not require rediscovering every source; a rejected or different-version recording cannot reappear through fallback. |
| Selection | Prefer the verified local source, then use the established 7/5/3/1-point preference policy among qualifying candidates. Evaluate against the strongest eligible confidence, not chained pairwise wins. | Preference selects an ordering; it never inflates raw confidence to cross acceptance gates. Manual pins only permit fallback according to an explicit policy. |
| Playback | Try authorized ready alternatives within one startup deadline. Translate identity before playback when possible; avoid serial catalog searches after pressing Play. Recheck account eligibility when serving. | Lease failure **and** failure opening the upstream HTTP response before commitment can advance to the next candidate. Expired access, ranges, cancellation, rate limits, and exhausted alternatives have deterministic outcomes. |
| Media continuity | Pin the opened media representation for the request/session's range contract. | No silent mid-stream splicing between different encodings, byte lengths, or recordings; a representation change needs a valid new playback attempt. |
| Presentation | One external recording entry with a stable Allstarr identity and inspectable source alternatives/current source. Preserve original native objects and IDs. | Provider failover does not duplicate playlist entries or invalidate saved references. Migrate existing provider-shaped IDs through aliases before changing emitted IDs. |
| Local handoff | After explicit acquisition and observed backend indexing, attach the native identity and prefer it for subsequent playback. | The track remains playable with external providers unavailable, without losing playlist membership, artwork, or manual decisions. |

Provider names can leave the main song title, but must remain available in details, diagnostics, and account decisions. Allstarr-injected songs use `[A]`; explicit injected songs use `[A] [E]`. Local backend titles and objects must remain unchanged.

Lidarr is a possible later acquisition and file-organization adapter, not the recording authority or a replacement matching database. Its job is monitoring releases and coordinating indexers, download clients, naming, and upgrades. Allstarr should continue to own the scoped recording-to-provider/local identity graph, using MusicBrainz recording IDs, ISRCs, duration/version evidence, and fingerprints when available. Keep recording identity separate from release-track identity: one recording may appear on several releases, while live, remix, instrumental, clean, and explicit audio must remain distinct. Integrate Lidarr only after the direct keep/download-to-index workflow is qualified, behind the existing durable job and managed-file boundaries.

**First-release sequence:** (1) specify source-set/manual-authority semantics and regressions; (2) finish scoped pre-commit failover in the existing gateway; (3) complete acquisition-to-native reconciliation; (4) migrate external identities and presentation only when aliases and client compatibility are proved. If identity migration cannot be qualified, retain current IDs for the first release rather than announcing provider-neutral catalog identity prematurely.

**Later, separately scoped:** merge artist catalogs and discographies. This needs artist disambiguation, recording versus release-track identity, edition/version preservation, source attribution, deterministic deduplication/pagination, and partial-provider failure behavior. Do not merge live/remix/clean/explicit versions merely because names resemble each other. Full cross-provider artist/album aggregation is not a prerequisite for reliable local-first playback.

## Current feature inventory

“Present” below means implementation was found. It does not imply an end-to-end release qualification. “Working-tree addition” identifies significant functionality that depends on uncommitted code at the assessment baseline.

| Feature | Current implementation and limits | First-release disposition |
| --- | --- | --- |
| Native Jellyfin gateway | Authentication, browse/search, items, artwork, audio/ranges, playlists, favorites, sessions, WebSocket relay, lyrics, and music-specific route policy. Versioned protocol fixtures exist. | Essential. Qualify exact backend/client versions and native parity. |
| Native Subsonic/OpenSubsonic gateway | Query/form authentication, XML/JSON, search, items, audio, art, lyrics, playlists, stars, and playback observations. One deployment selects one protocol. | Retain. Require equivalent live qualification before advertising equal support. |
| Local plus external search | Typed metadata gateway, backend response adapters, provider ordering, and external identities are present. Spotify is not a general searchable audio catalog adapter. | Essential. Preserve local metadata and distinguish playable results from source-only rows. |
| External playback | Typed leases for Deezer, Qobuz, Apple gateway, and eligible extensions; range behavior varies by provider. Controllers still have a legacy download-and-stream fallback. | Essential for selected qualified providers. Consolidate ownership and measure startup. |
| Playback cache and quality | Progressive cache publication, lower-quality requests, media metadata, and cleanup exist. Cache lookup and legacy storage policy do not use the same ownership model as managed acquisitions. | Keep; fix scope and consistent cache/keep semantics. |
| Explicit cache retention | Cached/Kept views, promote, download/export, delete, and reference checks exist. Promotion moves files directly in the controller. Kept root selection differs from managed placement. | Essential; route through the managed-file owner and correct the projection. |
| Favorite-triggered acquisition | Configurable durable actions: match, download, place, enrich, refresh, and scrobble. Acquisition actions are off by default. A favorite is not inherently a download. | Keep a simple opt-in policy. Explain its result independently from the favorite itself. |
| Safe managed file placement | Scoped ownership, checksums, references, path validation, staging/recovery journal, copy/link operations, and explicit removal exist. | Essential infrastructure. Reuse it across every retention entry point. |
| Backend refresh and indexing | Authenticated refresh adapters and a separate catalog indexing service exist. Refresh job success means the scan request was accepted. | Essential. Join acquisition to scan completion, native item discovery, and route reconciliation. |
| Personal provider accounts | Encrypted account secrets, exact scope, audiences, backend identity mapping, and account-aware routing exist. Apple gateway login is deployment-wide; MusicKit personal-library accounts are separate. | Essential. Prove cache-hit and background-job isolation, not just secret storage. |
| Playlist discovery/import | Spotify personal playlists, Apple MusicKit library playlists, and provider catalog playlist reads. Snapshots, owner scope, artwork, and source updates exist. | Keep. Start with the providers actually qualified through the full journey. |
| One-time versus linked imports | Frozen snapshots and manual/scheduled refresh behavior exist; current working-tree changes strengthen reuse after account disablement. | Keep two clear choices: import once or keep linked. Test that one-time imports stop reading the source. |
| Keep every playlist song | Working-tree addition: `playlist.retain-track` jobs download and place resolved external tracks. Local/unresolved rows are skipped by the queue. No subsequent backend refresh is requested in this handler. | Core to library growth, but incomplete as an acquisition-to-local workflow. |
| Playlist projections | Virtual, materialized, hybrid; source/resolved/target views; reconcile/recreate; stale-entry and manual-entry policies. Backend materialization is limited to eligible local/native entries. | Keep simple defaults. Put advanced modes behind an explicit advanced choice; test the retained combinations. |
| Match review and authority | Canonical/provider identities, evidence scoring, accepted/tentative/ambiguous states, pins, rejections, manual provider routes, and history. | Essential. Preserve authority across all projections and retries. |
| Bulk/versioned rematching | Working-tree addition: gradual batches of 25, algorithm-version rollout, manual-authority guards, append-only history. | Keep after migration/restart and concurrency tests pass. Avoid deleting manual decisions during cleanup. |
| Lyrics and managed tags | Backend lyrics, LRCLib, optional Spotify and Apple lyrics; safe managed-file enrichment using provider/MusicBrainz facts. | Supporting feature. Failures must not hold up audio or acquiring the file. |
| Listening history and scrobbling | Scoped playback signals, durable delivery/checkpoints, opt-in history, live views, and Last.fm/ListenBrainz targets. | Retain core now-playing/scrobble delivery if qualified; defer the Intelligence history workspace. Preserve existing consent and data controls. |
| History imports | Spotify Extended History, Last.fm, ListenBrainz, Koito, and Maloja formats; preview, jobs, dedupe, retention, corrections, export/purge. | Deferred with Intelligence, not personal playlist imports. Preserve existing import data and recovery. |
| Recommendations | Local rules, Jellyfin InstantMix, MusicBrainz relationships, Last.fm, several ListenBrainz feeds, AudioMuse, feedback, generated sets, and schedules. | Deferred with Intelligence. Existing runtime work must be inventoried before release isolation; hiding navigation does not stop schedules. |
| AudioMuse workbench | Analysis, similarity, search, paths, blends, fingerprinting, clusters, maps, and generated sets have explicit surfaces. | Deferred with Intelligence, including its setup entry points. Not a core-release prerequisite. |
| Provider extension platform | Registry/package verification, permissions, account boundaries, staging, activation, rollback, JS runtime, hooks, and UI. No bundled third-party registry/packages. | Defer public SDK/store expansion. Keep any retained extension path permissioned. |
| Operational dashboard | Home, activity, durable jobs, health/connectivity, match review, cache diagnostics, settings, onboarding. | Keep the controls needed to connect, play, keep, and recover. Freeze cosmetic redesign. |
| Install/update/backup/restore | Compose, source/image modes, optional profiles, PostgreSQL/key-ring backups, full state transfer, restore, and separate media retention. | Essential. Fresh install and tested restore are release gates. |
| Selective state transfer | Category selection, dependencies, merge/replace, conflict validation, preview, and UI. | Advanced maintenance. Preserve full backup/restore; defer selective transfer as a selling point. |
| Legacy configuration and M3U pathways | Old Spotify configuration endpoints coexist with durable playlist links. A registered M3U sync service has no production callers found. | Explicit deprecation/deletion candidates; do not build new behavior on them. |

Primary owners: [architecture](architecture/overview.md), [protocol matrix](../allstarr.Tests/Fixtures/Protocols/protocol-support-matrix.json), [protocol gateway](../allstarr/Core/Protocols/ProtocolProviderGateway.cs), [playlist orchestration](../allstarr/Core/Playlists/PlaylistOrchestrationService.cs), [favorite actions](../allstarr/Core/Favorites/FavoriteActionPipeline.cs), [playback signals](../allstarr/Core/Playback/PlaybackSignalPipeline.cs), and [recommendation registration](../allstarr/Core/Intelligence/RecommendationSourceRegistration.cs).

### Provider capabilities are different products

| Source | What Allstarr actually uses it for | Qualification boundary |
| --- | --- | --- |
| Jellyfin / Subsonic backend | Owned library, native playback, native playlists, favorites, and reporting | First-class local source; qualify each supported protocol/version. |
| Deezer | Public metadata/playlist reads; account-bound streaming and downloading | Test metadata separately from authenticated audio and acquisition. |
| Qobuz | Metadata/playlist reads; account-bound streaming and downloading | Validate upstream ranges, token expiry, quality, and complete artifacts. |
| Apple gateway/GAMDL | Catalog metadata, single-track stream/download, lyrics, operator login | Optional wrapper/gateway dependency; a logged-in health check does not establish playback latency. |
| Apple MusicKit | Per-user personal-library playlists and metadata | Separate credentials/capability from Apple gateway audio. Do not imply this login controls GAMDL. |
| Spotify | Personal playlist discovery/import and optional lyrics | No generic Spotify audio streaming/download capability is advertised by the support catalog. Connection currently uses account cookies/token exchange, not a conventional end-user OAuth onboarding flow. |
| LRCLib | Lyrics | Optional public fallback; should not block playback. |
| MusicBrainz | Identity/tag enrichment and local recommendation evidence | Not an audio source or generic search provider here. |
| Last.fm / ListenBrainz | Scrobbling, history-related features, and recommendations | Optional, scoped external delivery; user data controls remain necessary. |
| AudioMuse | Optional self-hosted analysis/recommendation capability | Advanced, separately connected source; not an extension package. |
| Extensions | Only the capabilities implemented and authorized by a verified installed package | An SDK capability is not proof of an available, tested provider. |

Evidence: [current support catalog](../allstarr/Services/Common/CurrentProviderSupportCatalog.cs), [Spotify adapter](../allstarr/Core/Providers/Spotify/SpotifyPlaylistCapabilityAdapter.cs), [Spotify token exchange](../allstarr/Core/Providers/Spotify/SpotifyWebTokenExchange.cs), [AudioMuse registration](../allstarr/Core/Providers/AudioMuse/AudioMuseCapabilityRegistration.cs), and [Apple gateway contract](../sidecars/apple-gateway/README.md). The generic GAMDL URL-job interface includes broader media kinds than Allstarr's managed single-song capability; those should not become additional first-release promises.

## Size and churn baseline

Counts were taken before adding this assessment. These are physical lines, including whitespace and comments, not executable statement counts. They measure maintenance surface, not quality or waste.

| Measure | Observed value |
| --- | ---: |
| Tracked repository files | 913 |
| Changed tracked files relative to HEAD | 196: 192 modified, 4 deleted |
| Untracked files | 15, containing 3,974 lines |
| Tracked working diff | +6,626 / −6,398 lines |
| Same tracked diff ignoring whitespace | +6,540 / −6,312 lines |
| Current backend C# excluding migrations/generated EF | 110,688 lines across 402 files |
| Current WebUI production TS/Svelte/CSS | 22,056 lines across 79 files |
| Apple gateway production Python | 1,130 lines across 9 files |
| Combined production source, same scope | 133,874 lines; committed HEAD has 133,092 |
| .NET test source, excluding fixtures | 66,736 lines across 233 files |
| WebUI browser suite | 4,647 lines in `parity.e2e.ts` |
| EF generated designers/snapshot | 100,938 lines across 29 files |
| Other migration C# | 5,873 lines across 55 files |
| Commits since 2026-06-14 | 908: 641 in July, 259 in August, 8 in September |
| Production-source churn in that window | +141,905 / −56,780 lines, or 198,685 edited lines |

The working tree has **782 more production lines than HEAD**, despite substantial tracked deletions. That does not make the changes wrong; it means this batch cannot be described as a net code reduction. Including test/doc changes and untracked files, the whole working difference is +4,202 physical lines.

Churn uses `git log --since=2026-06-14 --numstat --no-renames`, restricted to C# under `allstarr/`, production TS/Svelte/CSS under `webui/src/`, and Python under `sidecars/apple-gateway/apple_gateway/`. It excludes migrations, EF generated files, and frontend unit tests. Additions, deletions, and moves can inflate churn; commits are not weighted by size. The unrestricted repository total is +579,896 / −269,451, but large API specification changes, generated migrations, and retired trees make that unsuitable as a waste estimate.

### Current hotspots

| File | Current lines | Commits touching it in the window | Interpretation |
| --- | ---: | ---: | --- |
| [`webui/src/app.css`](../webui/src/app.css) | 7,557 | 111 | Roughly 34% of WebUI production source in a global stylesheet; scattered repeated breakpoint blocks make changes hard to contain. |
| [`webui/src/lib/api.ts`](../webui/src/lib/api.ts) | 1,972 | 59 | Shared types and every feature's requests accumulate here. Much is necessary contract code; moving it alone saves nothing. |
| [`PlaylistController.cs`](../allstarr/Controllers/PlaylistController.cs) | 730 | 58 | Old settings-backed playlist entry points coexist with the newer link API. |
| [`AdminUiController.cs`](../allstarr/Controllers/AdminUiController.cs) | 1,543 | 49 | Configuration schema, support descriptions, dashboard projections, and activity shaping change together. |
| [`PlaylistLinksController.cs`](../allstarr/Controllers/PlaylistLinksController.cs) | 1,623 | 40 | Many product modes, commands, credentials, schedules, and projections share one HTTP surface. |
| [`PlaylistOrchestrationService.cs`](../allstarr/Core/Playlists/PlaylistOrchestrationService.cs) | 1,555 | 37 | Essential owner with substantial mode/state complexity; consolidate callers here rather than replace it. |
| [`ProtocolProviderGateway.cs`](../allstarr/Core/Protocols/ProtocolProviderGateway.cs) | 1,291 | 31 | Existing shared provider route owner; finish adoption instead of adding another router. |
| [`TrackMatchCommandService.cs`](../allstarr/Core/Matching/TrackMatchCommandService.cs) | 2,585 | — | Persistence, authority, review projection, and queries share a large owner. Contains bounded bulk reads that still need scale qualification. |
| [`ExtensionManager.cs`](../allstarr/Services/Common/ExtensionManager.cs) | 1,869 | 32 | Substantial JS execution/host bridge; extra support surface beyond the initial product loop. |
| [`parity.e2e.ts`](../webui/tests/parity.e2e.ts) | 4,647 | 174 | Valuable behavior coverage, but broad mocked fixtures and one large suite are a coordination hotspot. |

Some churn was productive: PostgreSQL consolidation, typed providers, native response preservation, and the move to Svelte removed older implementations. For example, the former `allstarr/wwwroot/js/webui.js` was touched 157 times and is no longer present. Do not count that already-removed code as a future saving.

Reproduce the scope with `git status --short`, `git diff HEAD --numstat`, `git ls-files --cached --others --exclude-standard`, and the filtered history command above. Count existing files only, deduplicate the path list, and include untracked source. Compare the same extensions/directories at HEAD and in the working tree. Report generated code and tests separately.

## Findings that should drive the release work

### R1 — The release candidate is not yet a reproducible source snapshot

**Confirmed.** The dirty tree contains 196 tracked changes and 15 untracked files. `ManagedTrackDownloadService`, playlist retention, versioned bulk rematching, and associated tests/UI include untracked dependencies. The last committed CI result cannot certify those changes.

**Action:** inventory the outstanding edits into coherent changes, retain or explicitly defer each, and validate a clean candidate. Record its commit, application image digest, optional sidecar versions, migrations, and support matrix. Do not use a checkout SHA as evidence that an older application container contains the same code. Source updates also require a clean tree in the documented updater.

### R2 — External cache hits bypass the account-aware provider route

**Confirmed control-flow and schema gap; no cross-user live exploit test was performed.** [`DownloadedSongMappingEntity`](../allstarr/Core/Downloads/DownloadedSongMappingPersistence.cs) has a unique `(ProviderId, ExternalId)` key without tenant, owner, library, or account. [`LocalLibraryService.GetLocalPathForExternalSongAsync`](../allstarr/Services/Local/LocalLibraryService.cs) resolves that key without a caller. Both [`JellyfinController.Audio`](../allstarr/Controllers/JellyfinController.Audio.cs) and [`SubsonicController`](../allstarr/Controllers/SubsonicController.cs) return the cached file before entering `OpenStreamAsync`. Native authentication still runs; the missing boundary is authorization to reuse that external artifact.

**Impact:** personal account policy and revocation cannot be assumed to cover warmed files. A shared physical cache can be valid, but it needs an explicit authorized-sharing policy and scoped references.

**Action:** authorize artifact access in the shared playback owner before opening a cached file. Test user A/user B, revoked access, different libraries, same external ID, and explicit shared-library policy on both protocols. Preserve legitimate playback of music already acquired into an authorized local library when the external account later disappears.

**Working-tree update:** external cache lookup now follows candidate authorization in the shared gateway, and fallback audio is published under its actual provider/track. Actor-scoped misses no longer fall through to legacy global playback. This fixes the two controller shortcuts described above; it does not replace the provider-keyed artifact schema or complete the scoped artifact-ownership and native-acquisition lifecycle qualification.

### R3 — “Kept” has conflicting roots, records, and mutation paths

**Confirmed.** [Compose](../docker-compose.yml) supplies `Library__KeptPath=/app/kept`. [`ProviderDownloadArtifactRegistration`](../allstarr/Core/Downloads/ProviderDownloadArtifactRegistration.cs) uses it for managed placement. [`DownloadsController.ResolveListRoots/Root`](../allstarr/Controllers/DownloadsController.cs) instead scans `DownloadPath/permanent` and `DownloadPath/kept`; it does not read the configured KeptPath. Managed-only files also do not satisfy that controller's `DownloadedSongMappings` requirement for removal/promotion.

The same controller's `PromoteCachedDownload` moves audio and its sidecar before updating the database, outside [`FilePlacementService`](../allstarr/Core/ManagedFiles/FilePlacementService.cs). A move/database or sidecar failure can therefore leave different intermediate state from the journaled managed path.

**Action:** one root definition, one scoped retained-file projection, and one recoverable placement/removal owner. Account for existing `permanent` and legacy `kept` files explicitly. Cover custom roots, managed-only records, sidecars, interrupted promotion, references, and original-library protection. Avoid moving users' existing media merely to simplify naming.

### R4 — Acquisition does not finish at a verified local item

**Confirmed missing orchestration in the keep-all handler.** [`PlaylistTrackRetentionJobHandler`](../allstarr/Core/Playlists/PlaylistTrackRetention.cs) ends after `MarkPlacedAsync` and its completion event. It does not request a scan, await indexing, or reconcile to a backend ID. [`FavoriteRefreshActionExecutor`](../allstarr/Core/Favorites/FavoriteRefreshActionExecutor.cs) can separately enqueue a scan, but [`BackendLibraryRefreshJobHandler`](../allstarr/Core/Enrichment/BackendLibraryRefresh.cs) marks success when the backend accepts the request.

The legacy downloader additionally calls `LocalLibraryService.TriggerLibraryScanAsync`: an unauthenticated Subsonic request, independent of the authenticated refresh owner. The Jellyfin refresh adapter still sets `X-Emby-Token` directly while modern proxy paths use `Authorization`; background scan authentication needs the same version qualification as interactive playback. This audit did not establish whether a particular live server accepts that header.

**Action:** extend the existing durable acquisition/refresh/indexing owners to complete the lifecycle. Coalesce scans per backend/library, retry observation with a bounded deadline, and show a useful pending/error state. Verify required tags and the mount visible to the backend, then persist the native identity and refresh affected playlist routes. Core regression: keep a song, index it, disable its external provider, then play the native item through Allstarr. Test the previously saved virtual/source reference too: it should resolve consistently to the accepted local item while preserving any authoritative user choice.

### R5 — Two playback/download implementations remain active

**Confirmed.** [`Program.cs`](../allstarr/Program.cs) registers the typed gateway and `IDownloadService`/concrete services. Both protocol controllers fall back to [`MultiProviderDownloadService`](../allstarr/Services/Common/MultiProviderDownloadService.cs). [`BaseDownloadService`](../allstarr/Services/Common/BaseDownloadService.cs) maintains its own active-download dictionaries, concurrency, complete-file preparation, album fan-out, and detached `Task.Run` work. This differs from the durable download/artifact path.

The legacy methods remain reachable fallbacks; they are not safe to delete wholesale. Concrete adapters may still supply useful provider-specific implementation. [`ManagedTrackCacheService`](../allstarr/Services/Common/ManagedTrackCacheService.cs) is another lifecycle participant and caches only in Cache mode; successful typed playback in Permanent mode does not itself publish a retained managed artifact.

**Action:** make playback, temporary caching, and explicit durable acquisition distinct operations behind the existing typed owners. Migrate reachable fallback callers, preserve byte/range/quality behavior, and then remove redundant orchestration. A dropped playback request need not become a durable job, but an explicitly requested acquisition must have durable ownership. Eliminate implicit whole-album fan-out from ordinary song playback unless deliberately retained as a documented option.

### R6 — Some code is maintained without a production entry point

**Strong deletion candidate from repository reference search.** [`PlaylistSyncService`](../allstarr/Services/Subsonic/PlaylistSyncService.cs) is 268 lines of provider download and M3U generation. References found are its registration, its own implementation, test setup, and ownership tests; no production call to `DownloadFullPlaylistAsync` or `AddTrackToM3UAsync` was found. A new working-tree test verifies its constructor can create a directory, not a user journey. `LocalLibraryService.GetDownloadDirectory` and `GetScanStatusAsync` similarly have no production callers found.

**Action:** confirm no dynamic consumer, then remove the orphan service/registration and obsolete tests rather than polishing it. Keep protocol playlist support and the live durable playlist implementation. This is a concrete small reduction opportunity; it is not a justification for deleting all Subsonic code.

### R7 — Playlist configuration and capability descriptions have multiple authorities

**Confirmed coexistence.** [`PlaylistController`](../allstarr/Controllers/PlaylistController.cs) still exposes Spotify-specific mutations to the durable `SpotifyImport:Playlists` setting and mutates runtime options. The current frontend's playlist actions use `/api/admin/playlist-links` and owner-scoped snapshots/jobs. Their coexistence creates two places to explain and maintain playlist behavior, even though the old endpoints' present use is unknown.

Similarly, [`CurrentProviderSupportCatalog`](../allstarr/Services/Common/CurrentProviderSupportCatalog.cs) manually describes support separately from typed registrations and runtime health. Its list omits AudioMuse even though AudioMuse is registered. Catalog tests assert the manually written declarations; they do not establish all runtime capabilities work.

**Action:** audit consumers, retire or translate the legacy playlist mutations through the canonical link owner, and remove the redundant runtime configuration after a documented migration/deprecation boundary. Derive capability availability from registrations plus health; keep human qualifications as annotations on those IDs. Do not remove the provider-specific HTTP adapters needed to implement those capabilities.

### R8 — Release verification is extensive but disconnected from some product claims

**Confirmed gaps, alongside useful existing coverage.** The .NET suite covers protocol fixtures, routing, persistence, file safety, and durable work. However:

- [`parity.e2e.ts`](../webui/tests/parity.e2e.ts) extensively intercepts `/api/admin/**`. It verifies browser behavior against fixtures, not a full dashboard → API → PostgreSQL → backend journey.
- [`playwright.config.ts`](../webui/playwright.config.ts) has viewport coverage but no explicit WebKit/Firefox projects; [CI](../.github/workflows/ci.yml) installs Chromium only. A narrow Chromium viewport does not establish mobile Safari compatibility.
- [`docker.yml`](../.github/workflows/docker.yml) independently builds/tests on tags/dispatch but omits Playwright. CI's browser gate is not an explicit dependency of image publication. Its container smoke establishes readiness, not authentication, media playback, or acquisition.
- [`tools/tests`](../tools/tests/README.md) has a substantial live Jellyfin kit; no equivalent checked-in live Subsonic journey runner was found.
- OpenAPI operation classification verifies policy/fixture coverage, not that every allowed operation works in every listed client. The client guide lists successful use without a versioned result for each promise.

**Action:** extend the existing test kit with shared scenarios/data and protocol adapters, connect a small real-API browser lane to disposable PostgreSQL/backends, and require the same candidate's complete gates before publishing it. Count executed tests and required scenarios; a missing database/runtime is “not qualified,” not a passing scenario.

### R9 — Large optional surfaces compete with core refinement

**Product-scope judgment, not proof of unused features.** The extension core, manager, controller, and main view alone span 6,134 lines. Selective state transfer's service is 1,799 lines. `Core/Intelligence`, its controllers, and recommendation services together span 7,762 lines before their frontend/tests. Legacy configuration code is 2,257 lines.

These figures are overlapping feature-area samples, not additive deletion estimates. There is no usage telemetry in this audit that establishes users do not need these features. History, local recommendations, permissions, and backup logic can be valuable. The problem is making their entire advanced scope a prerequisite for reliable local listening and acquisition.

**Action:** freeze advanced feature development; make experimental surfaces opt-in and exclude unqualified promises from release marketing. Keep data readable and recovery possible. Remove a whole optional subsystem only after identifying its consumers, persisted data, dependencies, and an export/migration path.

### R10 — Churn needs boundaries, not another whole-app rewrite

**Confirmed concentration; performance impact requires measurement.** Global styles, playlist controllers, the API module, and shared projections are frequent change targets. Match review loads up to 10,000 snapshots plus related decisions/identity/library rows before projection; the method is bounded, but that is not proof of an inexpensive page request. The download list recursively enumerates audio files. These are sensible scale-test targets, not established latency regressions.

**Action:** freeze the visual system and feature scope during stabilization. Consolidate repeated component rules and remove obsolete selectors against existing responsive tests. Measure representative libraries/history first, then page/filter at the authoritative store where needed. Split large owners only around real responsibilities; moving code into more files does not count as code reduction. Keep explanatory safety/protocol comments, delete redundant narration only while touching its owner.

## What to remove, simplify, or defer

| Category | Candidate | Required boundary |
| --- | --- | --- |
| Remove after final reference check | Orphan `PlaylistSyncService`, registration, obsolete constructor test; unused local scan/status convenience methods | Preserve the actual Subsonic playlist gateway and authenticated refresh service. |
| Consolidate, then delete old path | Typed versus legacy stream/download orchestration; direct cache promotion versus managed placement | Prove media bytes, ranges, quality, scope, restart behavior, and artifact references first. |
| Deprecate | Old `SpotifyImport:Playlists` mutation API and duplicated option state | Identify consumers; migrate or return an explicit supported replacement. |
| Simplify presentation | Playlist mode combinations, source/account terminology, raw matching diagnostics, advanced routing | Keep simple defaults plus advanced controls. Do not change stored meanings during a UI cleanup. |
| Defer the workspace | Intelligence history/imports/discovery/automation and AudioMuse workbench | Navigation hidden now; finish release route/bundle and entry-point isolation without losing existing data or consent controls. |
| Defer expansion | Extension marketplace/SDK ecosystem and cross-provider artist/album catalog merging | No new release dependency; qualify any retained extension path. |
| Keep as advanced maintenance | Selective transfer and legacy-env conversion | Full backup, restore, key handling, and migration integrity remain mandatory. |
| Keep and finish | Local/native parity, exact identity, manual decisions, personal accounts, explicit retention, local reconciliation | These directly serve the product contract. |
| Never count as expendable bulk | Isolation, cancellation, retries, ownership/path checks, migrations, tested compatibility behavior | Shorter code is not an improvement if these guarantees disappear. |

Do not set a whole-repository percentage deletion quota from these figures. Each consolidation change should state which implementation/route/setting was removed, its net production-line change, and which user guarantees still pass. File moves, compressed formatting, removed assertions, and deleted migration snapshots do not qualify as an optimization.

## WebUI fix log (2026-09-13)

**Implementation integrity verdict: not release-ready.** The shared components and visual tokens are worth retaining, but verified cascade and tab-sizing defects still undermine them. The Impeccable static detector returned no findings for `webui/src`; its source-pattern scan did **not** detect the runtime defects below. A green detector or no-document-overflow test is not sufficient visual qualification.

Review method: production build in the built-in browser, using the repository's existing read-only API fixtures. Inspected Home, playlists and details, mappings, mobile Settings, Services/Routing, Cached, and the More sheet. Browser-reported CSS widths included 390, 853, and 1280 pixels. Inspected dark and light states and keyboard skip/navigation behavior. The deployed admin endpoint returned 403 to a read-only check; no access policy was changed. This is not a live-database, real-iOS, or full accessibility certification.

### Provisional audit health

| Dimension | Score / 4 | Evidence and limit |
| --- | --- | --- |
| Accessibility | 3 | Named controls, working keyboard skip, shared keyboard tabs/dialogs; duplicate primary navigation and overlapping targets remain. Full contrast/assistive-technology coverage not performed. |
| Performance | 3 | Production budgets pass: 72.8 KiB initial JS and 25.0 KiB CSS compressed. Large-list refresh and real-network frame/interaction timing remain unqualified. |
| Responsive design | 2 | Mobile route tests pass, but desktop compact tab containers overlap and desktop navigation duplicates. Viewport width alone misses these states. |
| Theming | 3 | Reviewed light/dark use the incumbent token system; no palette replacement needed. Contrast across every state still needs measurement. |
| Implementation integrity | 2 | Shared owners exist, but conflicting global selectors and ambiguous storage/status copy weaken the product contract. |
| **Total** | **13 / 20** | **Acceptable foundation; significant work required before release.** |

Seven open findings: **0 P0, 3 P1, 4 P2, 0 P3**. P1 means fix before release; P2 is an actionable usability improvement, not a redesign request. UI-7 is a release gate rather than a newly introduced runtime defect.

| ID / priority | Verified problem and user impact | Owner / reproduction | Planned fix and passing regression |
| --- | --- | --- | --- |
| UI-1 **P1** | Desktop shows both primary navs, repeating Home/Library/Activity and pushing account controls down. | [`app.css`](../webui/src/app.css), `.sidebar nav` versus `.mobile-navigation`; both compute to `display:grid` at 853 and 1280 CSS px. | Scope layout rules to the intended navigation so the hidden variant stays hidden. At 760/761/900/901/1280, exactly one Primary nav is visible and keyboard-reachable, including expanded/slim sidebars. |
| UI-2 **P1** | Playlist-detail view tabs overlap at desktop width; labels and clickable rectangles intrude into neighbors despite no document overflow. | [`SegmentedNav`](../webui/src/lib/components/SegmentedNav.svelte), [`app.css`](../webui/src/app.css), `PlaylistsView` “What listeners see.” At 1280 px the first two tab rectangles were approximately 815–967 and 943–1139; second/third labels overlap too. Equal `minmax(0,1fr)` grid tracks conflict with `min-width:max-content` children. | Fix shared tab sizing based on the available container, not only the viewport breakpoint. Preserve scrolling/keyboard selection. Assert nonintersecting tab **and label** rectangles for long provider names/counts in narrow desktop dialogs and mobile tabs. |
| UI-3 **P2** | Home's individual activity rows all lead to the generic event page, losing the selected event/context. | [`HomeView`](../webui/src/lib/components/HomeView.svelte), `.activity-line` always links to `#/activity`; event view supports related-object links but not selection from Home. | Carry the event/correlation into Activity and reveal it, including older events or an explicit unavailable state. Keep “View all” generic. Regression clicks a specific row and finds that event, not merely the Activity heading. |
| UI-4 **P2** | Match filtering requires a raw “Library scope” string; ordinary users cannot select their library by name. Snapshot IDs and repeated confidence displays compete with the decision itself. | [`MappingView`](../webui/src/lib/components/MappingView.svelte), filter input, comparison metadata, confidence summary. Confirmed in the rendered fixture and source. | Reuse the authorized media-target picker with names and an All libraries choice; retain IDs in technical disclosure. Preserve precise confidence/evidence in one clear hierarchy. Test multiple allowed libraries, no allowed library, and long metadata. |
| UI-5 **P2** | Cached/Kept labels blur server retention, browser export, and native indexing. “Indexed” totals actually render `managedCount`, which includes every non-diagnostic entry, not observed native items. | [`DownloadsView`](../webui/src/lib/components/DownloadsView.svelte), totals/actions; [`DownloadsController`](../allstarr/Controllers/DownloadsController.cs), `GetDownloads`. | Call the current total Managed (or another accurate term); explain browser file export versus keeping on server. Add separate waiting-for-index/native-available state only after R3–R4 establish it. Regression proves a cached/unindexed file is never presented as available in the native library. |
| UI-6 **P2** | Global live status exposes “Stale”/“Reconnecting” without explaining which data is old, when it last updated, or what the user can do. | [`+page.svelte`](../webui/src/routes/%5B...path%5D/+page.svelte), `.live-state`. The no-SSE fixture exercises this UI; it is **not** evidence of a production stream outage. | Reuse shared live-update state for an accessible freshness explanation, last-success time, and appropriate recovery action. Preserve automatic backoff; test interruption/recovery without discarding drafts or duplicating refresh work. |
| UI-7 **P1** | Hidden Intelligence still has an importable route, an AudioMuse “Configure in Intelligence” link, and related UI/data-control dependencies. Hiding one tab alone is not the agreed release boundary. | Shell loader, [`SourcesView`](../webui/src/lib/components/SourcesView.svelte), Home aggregates, docs; see the scope decision above. | Complete one coherent release/development boundary and legacy-link behavior. Production build cannot load the deferred workspace or advertise its unavailable workflows; current users retain a documented data-management path. Core playlist import, playback and scrobbling keep working. |

**Preserve:** responsive tables, equal mobile navigation tracks, shared controls, explicit destructive confirmations, scoped account terminology, keyboard navigation, skeleton/error states, reduced-motion handling, both themes, and route-level lazy loading. The keyboard skip link correctly advances into main content; it was checked and is not a finding.

**Test limitations to address:** the fixture can deliberately report a Review count of zero while returning a tentative row, and different source versus client-projection route facts. Those contradictions were not logged as proven production bugs. Add faithful server-contract scenarios before judging auto-acceptance or native availability. Existing route geometry tests mostly measure viewport containment and touch sizes: add desktop hidden-variant and sibling-intersection assertions, then real-API journeys and WebKit. Separately measure long lists/live insertions, slow/error/empty states, 200% zoom, and text contrast; do not claim smoothness from compressed-byte budgets.

**Recommended passes:** `$impeccable adapt` for UI-1/2; `$impeccable harden` for UI-7 and freshness recovery; `$impeccable clarify` for UI-3/4/5/6; `$impeccable optimize` only against measured list/refresh bottlenecks; finish with `$impeccable polish`. Run these independently or together, then rerun `$impeccable audit`. No replacement palette or whole-app reskin is needed.

## Execution plan

The ordering below is intentional. Estimates should be made after the first clean candidate and core regressions exist; the exit conditions are more useful than a speculative release date.

| Order | Work package and owner | Concrete deliverable / exit condition |
| --- | --- | --- |
| 0 | Reconcile the candidate; repository/release owner | Classify the 211 outstanding paths, retain/defer coherently, commit a clean source candidate, and run all required gates. No hidden untracked implementation dependencies. Record application and sidecar image provenance. |
| 0a | Freeze release scope and fix shared shell/tab defects; WebUI/release owner | Intelligence navigation removal is done; complete UI-7 release isolation. Correct UI-1/2 once in shared owners, with desktop/mobile hidden-variant and nonoverlap regressions. No palette redesign. |
| 1 | Define and reproduce core failures; playback/files owners | Deterministic reproductions for cross-user warm-cache access policy, configured kept-root visibility, interrupted promotion, and acquisition becoming a native item. Keep them red until the shared cause is corrected. |
| 2 | Finish acquisition and local handoff; Downloads, ManagedFiles, Enrichment, Matching | One managed placement policy and projection; favorite/playlist/manual keep requests reuse it. Durable scan/index observation, identity reconciliation, and native replay work. Cache eviction and unfavorite cannot remove kept/native originals. |
| 3 | Consolidate playback and personal access; Protocols, Routing, provider adapters | Scoped cache authorization; one typed playback route with explicit pre-commit fallback outcomes and retained verified alternatives as specified above; durable acquisition separated from client streaming. Remove superseded legacy orchestration and orphan code after coverage passes. |
| 4 | Stabilize playlists and essential dashboard interactions; Playlists/WebUI | Import-once/linked and on-demand/keep-all work for two users. Names, art, order, unavailable tracks, manual authority, and rematching remain correct after restart. Kept status and local availability reflect authoritative records. Resolve UI-3–6; reduce duplicated presentation rules without redesigning the app. |
| 5 | Qualify and package; test/release owner | Same clean candidate passes deterministic, PostgreSQL, real-API browser, protocol/live provider, timing, install/update/restore, and soak gates. Publish versioned compatibility results and an immutable image. Start a controlled pilot. |

Work packages 2 and 3 must converge before multi-user distribution. Keep changes reviewable: one responsibility or retirement per commit, regression first for a known failure, no formatting sweep mixed into ownership changes. Preserve unrelated work during candidate reconciliation; do not reset the working tree to obtain “clean” status.

### Qualification matrix to extend in the existing kit

| Journey | Required variants | Pass condition |
| --- | --- | --- |
| Native listening | Jellyfin and Subsonic claims; desktop/mobile client; optional providers healthy/down | Authentication, art, browse, search, playback, seeking, and playlists preserve native behavior. Zero external media fetches for a known local recording. |
| External listening | Every provider advertised for audio; cold/warm, GET/HEAD, real ranges, lossy/original, cancellation, expired credentials, rate limit | Correct playable audio and content facts; bounded startup/failure; no guessed cross-provider identity or global credential fallback. |
| Explicit acquisition | Manual keep, favorite opt-in, playlist keep-all; duplicate requests; restart during download/place; configured custom root | Exactly one owned retained artifact per intended identity/scope, usable tags, visible status, native index reconciliation, replay without the external service. |
| Account isolation | Two users, separate/shared policy, same external ID, revoked account, background jobs, warm cache | Personal credentials/artifact access and work remain within the authorized scope; intentional shared local access remains usable. |
| Playlist import | Spotify and each advertised alternative; one-time/linked; art/name/order changes; unresolved rows; source account disabled | Frozen copies stay frozen, linked copies update as chosen, unavailable songs are explained, no silent source mutation or duplicate materialization. |
| Matching | Local/external competition, alternate recordings, manual pin/reject, algorithm rollout, newly indexed download | Correct recording, raw confidence preserved, manual authority retained, all read views agree on the effective route. |
| Playback observation / scrobbling | Native and external playback; start/progress/stop/repeat; opt-in/off; source outage | Retained core now-playing and scrobble behavior is correct and scoped; disabled collection remains off and optional failures cannot break audio. Intelligence charts/history/import qualification belongs to its deferred milestone. |
| Browser workflows | Real API plus existing mocked UI suite; 320–430 px and desktop; keyboard, dark/light, reduced motion; WebKit for an iOS claim | Connect, import, review, keep, inspect failure, retry, and sign out complete without overflow, inaccessible controls, or misleading success. |
| Recovery | Fresh install; restart; PostgreSQL interruption; full backup/key restore into disposable deployment; separately preserved media | Candidate recovers accounts/state and reconciles artifacts without duplicate destructive work. Original backend media remains unchanged. |

Use a provider-neutral scenario definition and protocol adapters within `tools/tests/`; reuse the existing .NET fixtures, fake HTTP providers, and isolated PostgreSQL harness. Do not introduce another testing framework. Live provider runs are an explicit qualification lane with operator-supplied secrets, never a requirement for normal CI.

For timing, record click/request → authentication → route/lease → first actual audio bytes → playable decode, with warm/cold state and provider noted. Include queue delay and p50/p95/max across repeated runs, not one successful curl. A proposed target for the reported three-second client limit is p95 below 2.5 seconds with margin; it is **not a measurement or a promise that cold Apple preparation meets it**. Retain a failing provider/client combination as experimental until the path meets the limit or compatibility is accurately documented. Headers/empty data alone do not prove the client received usable media.

### First release and pilot scope

Recommend a small private beta first: Jellyfin 12 with explicitly tested desktop/mobile clients, the providers that pass the full audio/acquisition matrix, and personal playlist import. Keep Subsonic available as a preview if its live qualification lags; qualify it equivalently before marketing both protocols as equally supported. Do not remove it to reduce the line count.

Use a clean install and a restored install, then a small group of non-admin users for a multi-day pilot. Require working native playback, successful external-to-local acquisition, correct personal-account isolation, actionable failures, and tested recovery. Any unexplained wrong-account access, native playback breakage, lost retained file, or corruption blocks wider distribution.

The maintainer's main decisions are the supported backend/client/provider matrix, whether acquiring music is an explicit keep action or an opt-in favorite policy, and which advanced surfaces remain experimental. Recommended default: **play on demand; retain only on explicit keep or an explicit playlist/favorite retention policy; prefer a verified local recording once available**. Acquisition policy must not be an accidental side effect of selecting an audio provider.

## Verification performed for this assessment

- Current working-tree Release build succeeded with warnings treated as errors: zero warnings/errors. The initial sandboxed multiprocess invocation ended after five minutes without a compiler diagnostic; the single-process build outside that sandbox completed successfully. This was not classified as an application build defect.
- 114 focused .NET tests passed, zero skipped: matching decisions, provider routing, protocol support policy, current support catalog, managed stream cache, and file placement.
- `npm run check`: zero errors/warnings. `npm test`: 47 tests passed across 11 files.
- GitHub reports [CI success for committed `adf3c585a`](https://github.com/SoPat712/allstarr/actions/runs/34603953662). That result applies to the committed source, not the dirty tree audited here.
- Full PostgreSQL lanes, current-tree production WebUI build/budgets/browser runs, live backend/provider tests, timing, and restore rehearsal were not rerun for this assessment. No production database, account, or media state was changed. Findings above distinguish code-confirmed gaps, untested runtime risks, and product-scope recommendations.

The next implementation step is to reconcile the candidate and reproduce R2–R4, then complete the shared acquisition and local-playback path. Advanced features and cosmetic work should remain frozen until that path is qualified.

### WebUI addendum validation

- Navigation change: one shared destination filter; content-sized equal mobile tracks. No backend, account, media, migration, or data changes.
- `npm run check`: zero errors/warnings. `npm test`: 47 passed. Production build and all bundle budgets passed.
- Focused production-build browser checks cover all 13 existing route entries at six viewports plus navigation, breakpoints, and keyboard/contextual-tab behavior. Light-theme run: 83 passed; dark-theme run: 83 passed. Deferred Intelligence direct-route tests are retained, not disabled to obtain a green result. Existing keyboard/breakpoint checks supplement the theme-specific route runs.
- The initial browser run found one remaining old destination-count assertion; it was updated to the intentional three mobile links/five desktop links. That was a test expectation change caused by this navigation removal, not a dismissed product defect.
- Runtime audit findings above remain **planned**, except the explicitly completed Intelligence navigation removal and adaptive mobile track sizing. No claim of full UI repair, live-backend qualification, or deployment is made.

### Account sharing and server qualification addendum

Owner-controlled sharing is implemented in the working tree, not deployed: listeners choose **Private** by default or explicitly confirm **Global**, and can later unshare, disable, replace credentials, or remove their own connection. Management stays owner-bound; consumption follows the shared provider policy. Private-to-global transitions retain the creator and transactionally rebind the encrypted secret. Administrator-assigned private connections cannot be reshared by their recipient or reclaimed by their original creator. No schema migration or parallel credential system was added.

The shared ownership query also covers Last.fm connection completion. Playlist discovery and routing now use the same global-personal-capability policy, preserving the creator's own personal access without opening it to peers. Non-admin audience and enable/disable routes pass through authentication middleware to scoped controllers. Operator-only provider probes remain restricted; listener saves no longer misreport a forbidden probe as a broken credential. Existing dialogs and theme tokens were reused, with explicit consent, quota/privacy copy, and responsive controls rather than a redesign.

Verification uses a source snapshot on the operator's server and a disposable PostgreSQL 18 database on a private Docker network, with no published database port or production Docker socket in the runner. It does not mutate the live database to exercise sharing. Results:

- Release build: zero warnings/errors. Focused account, identity, routing, secret-store, and scrobbling regressions: **108 passed, zero skipped**. These cover share/unshare, encrypted-secret rebinding, denied peer mutations and personal access, disabled accounts, operator policy, stale revisions, assigned ownership, and shared Last.fm authentication.
- WebUI check: zero errors/warnings; **47 unit tests passed**; production build and bundle budgets passed. Account creation, consent, sharing/unsharing, administrator dialogs, keyboard/reduced-motion behavior, and responsive account pages: **31 Chromium checks passed in each theme**, six viewports. Fixture-only screenshots were visually checked at narrow mobile and desktop sizes. These are not live API or mobile Safari qualification.
- Full ordinary .NET lane: **2,308 passed, three failed, zero skipped**. The failures are `DeezerMetadataServiceTests.MetadataSearch_PropagatesCallerCancellation` for songs, albums, and artists: each single-query search catches cancellation and returns an empty result. Preserve the tests; propagate caller cancellation while retaining bounded handling of genuine provider failures before release. Compose prerequisites were supplied to the runner and all Compose contract checks passed on rerun.
- Release-critical .NET lane: **105 passed, zero failed/skipped**, including PostgreSQL-native backup verification and restore into an isolated database after installing the version-matched client tools in the disposable runner. Across both full lanes: **2,413 passed, three failed, zero skipped**. No assertion or required test was disabled to obtain these results.
- Live native-protocol smoke succeeded with the designated test account: sign-in, three audio listings, item detail, album artwork, a 65,536-byte HTTP 206 stream, and logout. Server-local timings were approximately 219 ms sign-in, 290 ms median browse, 41 ms detail, 128 ms artwork, and 56 ms audio range. These are warm, server-origin measurements—not a WAN/mobile or cold-provider-start benchmark.
- Live dashboard loads through the LAN address but the test-account sign-in reports **“Failed to authenticate with Jellyfin”**, despite successful protocol sign-in. Root cause is not yet established; do not claim live dashboard/account qualification. Loopback dashboard access returns 403 under the existing network policy; no access rule was relaxed. The deployed app is not this working-tree snapshot.

Remaining account work, in release order:

1. Fix and retest the live dashboard sign-in path with the same candidate that will be released; complete a real UI → API → PostgreSQL two-user sharing round trip in staging.
2. Close **R2**: reauthorize warm/cached media and durable work after unsharing, disabling, or revocation. Passing account-resolution tests does not prove media-byte isolation.
3. Define account-owner disable/deletion behavior and expose account usage/audit records to its owner without leaking other listeners' private history.
4. Add scoped listener connection probes and clear readiness, usage limits, and provider concurrency guidance. Do not enable an operator-wide diagnostics endpoint as a shortcut.
5. Keep per-user provider preference, per-capability sharing, and share expiry as follow-up refinements after those safety and daily-use gates, not prerequisites for another routing rewrite.

### Provider-neutral playback implementation and qualification

Implemented in the working tree, not deployed. External titles now use `[A]` for Allstarr injection and `[A]/[E]` for explicit tracks; native titles and existing item IDs remain unchanged. The existing routing owner chooses authorized cached audio first, then configured streaming providers with verified alternatives. It honors manual pins, excludes tentative/released/replaced alternatives, and can advance on lease, HTTP, transport, empty-body, or incompatible-media failures before committing audio. A single bounded retry follows the lease policy; the protocol deadline and cancellation remain effective. Cache publication uses the serving identity, not the original catalog ID.

Playback source observations feed Home, per-user/device Jellyfin song details, and durable listening attribution. Artwork remains unchanged because a shared cached cover cannot reliably name a listener's current stream. [Client compatibility](operations/client-compatibility.md#external-song-labels-and-playback-sources) owns the detailed behavior and limitations.

Server qualification used a source snapshot and disposable PostgreSQL, never production database mutations or a deployment:

- Release build: zero warnings/errors.
- Full ordinary .NET lane: **2,342 passed, zero failed/skipped**. Full release-critical lane: **105 passed, zero failed/skipped**, including database backup/restore and Compose contracts. The three previously failing Deezer caller-cancellation cases now pass.
- Frontend: zero check errors/warnings; **47 unit tests passed**; production build and bundle budgets passed. **Seven focused browser tests passed**, covering confirmed/cached/unknown source labels in light/dark themes at mobile and desktop sizes, plus Home runtime/request budgets. Mobile screenshots were visually reviewed.
- Regression coverage includes exact alternative IDs and configured ordering, pre-response failures and response disposal, cancellation/deadlines, one-retry bounds, authentication stops, manual authority, warm-cache denial, serving-identity publication, source scope, range continuation, HEAD neutrality, consistent title markers, source details, and correction of provisional listening attribution.

Remaining boundaries: no canonical catalog-ID migration or cross-provider artist/album merging; no speculative matching on the stream critical path; no cross-provider failover for clients without a device identifier (standard Subsonic application-name-only requests retain exact-provider playback). In-memory seek selections expire and disappear on restart, requiring a fresh playback start. Live personal-provider failover and client-specific display behavior still require deployment qualification. R2's scoped artifact ownership and R3–R4's kept/native acquisition lifecycle remain release work.
