# Unified music service product and release plan

Status: accepted product direction and engineering plan; not a shipped-support contract.
Decision date: 2026-09-14.

## Product outcome

Allstarr should make a music library hosted by Jellyfin or a Subsonic/OpenSubsonic server such as Navidrome feel like one complete, self-hosted streaming service. A listener searches, browses, saves, and plays music without first choosing a provider. The deployment's selected native backend remains the library, authentication, transcoding, and client-compatibility foundation. Allstarr adds the catalog, identity, playlist, route, account, acquisition, and observation layers needed to fill gaps from authorized external providers. One deployment exposes one selected native protocol surface; the canonical core beneath both protocol adapters is shared.

The product must present **one musical entity with several playable routes**, not several provider-branded copies of the same song. Provider names are operational facts shown in route details, now playing, diagnostics, and account controls. They are not the primary identity of a recording.

The first public release is complete only when the following journeys work together:

1. A user signs in through the configured Jellyfin or Subsonic/OpenSubsonic backend and sees the native library unchanged.
2. Search returns one coherent artist, release, and recording view across local and external availability.
3. A user imports a playlist once or links it for refresh; every source row remains visible and in order.
4. Matching attaches accepted local and external routes to the same recording while preserving manual authority.
5. Playback uses an accepted local item first, then external routes that meet the requested quality tier in configured provider order, with bounded pre-response fallback and an explicit downgrade policy.
6. Keeping an external recording eventually produces an observed, reconciled native-backend item and changes future playback to local.
7. Settings and account scope apply consistently to UI requests, jobs, caches, and playback.
8. PostgreSQL preserves the catalog graph, ownership, decisions, jobs, and upgrade path across restarts and releases.

Intelligence and recommendations are separate milestones. Extension distribution and both native protocol surfaces are part of the release plan and must not make the primary journeys slower or less reliable.

## Primary release pillars

### 1. Canonical catalog and search

Search is a primary product feature, not a provider result aggregator. It needs enough canonical metadata to describe artists, releases, release tracks, and recordings, then overlays the routes the current user may use.

Canonical means that Allstarr owns the stable identity. It does not mean that MusicBrainz or BrainzMash already has the music. A native-backend or connected-provider result can create a provisional Allstarr artist, release, release track, or recording immediately. That entity is complete enough to search, browse, match, save, and play while later evidence improves it.

The current search implementation is a useful federation baseline: its protocol surfaces query the selected native backend and typed provider capabilities, preserve native data, and rank local results well. It does not yet form a unified catalog. Provider results keep provider-shaped IDs, identical recordings are only deduplicated within a provider identity, and external artist browsing remains tied to the provider that produced the result.

Required behavior:

- Return one stable Allstarr identity for an artist, release, release track, or recording.
- Emit provider-neutral canonical artist and release names. Transitional suffixes such as `[AM]`, `[D]`, and `[Q]` must disappear from artist/album identity and navigation once canonical relationships are available.
- Keep `[A]` and `[A]/[E]` only as protocol presentation markers for an Allstarr-injected track when a client needs that distinction; they are not stored title text or identity. Show available routes and the actual serving provider in song information and now-playing diagnostics.
- Merge provider and native-backend facts without changing an original Jellyfin object or Subsonic/OpenSubsonic identity and field contract when it is passed through natively.
- Show availability separately from identity: local, Apple Music, Deezer, Qobuz, or another qualified provider.
- Resolve aliases from existing `ext-{provider}-...` IDs during migration so saved clients and playlists do not break.
- Search a local PostgreSQL projection first. Refresh catalog facts asynchronously; do not place a public metadata service or fuzzy provider search in the Play request path.
- Fan out each listener search to the native backend and every authorized metadata provider within a bounded search deadline. Merge successful provider results into the response even when the selected MusicBrainz-compatible source has no result or is unavailable.
- Materialize every previously unknown provider result as a provisional Allstarr entity before returning it. Persist the provider namespace and external ID as an alias, retain the provider payload as source-stamped facts, and attach an authorized playable route when one exists.
- Never suppress a provider result merely because it lacks an MBID or ISRC. Exact aliases, compatible identifiers, and accepted match evidence may merge results; uncertain candidates remain separate and reviewable instead of being hidden by an unsafe deduplication.
- Continue serving cached catalog facts when the catalog source is stale or unavailable, with observable freshness and refresh failures.
- Treat provider catalog search as discovery and route availability, not as canonical truth.

#### Provider-only identity and reconciliation

Search follows one identity pipeline for native, MusicBrainz-backed, and provider-only music:

1. Resolve an exact provider alias to its existing Allstarr entity when one exists.
2. Otherwise, use a compatible recording identifier such as ISRC only after version, artist, duration, and explicitness safeguards pass.
3. Otherwise, attach the route to an already accepted match when the normal matching policy proves that it is the same recording.
4. If none of those checks succeeds, create a new provisional Allstarr entity. The result remains visible and playable; it does not wait for MusicBrainz.
5. Store each provider observation as evidence with its source, account/catalog scope, observed time, and refresh state. Provider facts can improve display metadata without becoming identity authority by themselves.
6. Reconciliation runs outside playback. A later MusicBrainz result or another provider route enriches the existing Allstarr entity when the evidence is safe. It must retain the Allstarr ID, saved state, playlist membership, manual decisions, and route history.
7. A conflict creates a reviewable merge candidate. It must not silently replace or remove either entity.

A provisional entity is a supported catalog state, not an error or a second-class search result. The app may show that metadata is provider-sourced or still being reconciled, but this state cannot disable normal playback from an authorized route.

Protocol title markers describe presentation, not identity or routing:

- `[A]` means the client is seeing an Allstarr-injected object rather than an unchanged native object.
- `[A]/[E]` adds the explicit-content marker.
- Do not use `[AM]`, `[D]`, `[Q]`, or another provider code in the title. One Allstarr recording may have several routes, and the serving provider can change during fallback.
- Show route icons or names in Allstarr song information. Now-playing and playback diagnostics show the provider that actually served the current stream.

Regression coverage must prove that a provider-only recording with no MBID or ISRC appears in search, receives the same Allstarr ID on repeated searches, opens and plays through its authorized route, survives a MusicBrainz miss or outage, and retains its ID when later reconciled. Cross-provider tests must also prove that exact accepted matches gain multiple routes, ambiguous versions remain separate, unauthorized accounts do not leak availability, and one provider's timeout does not erase successful results from another provider.

### 2. Playlist ingestion and matching

Playlist import is a primary feature because it converts a user's intent into a durable, ordered set of recordings. The existing snapshot, source-entry, sync-run, result, membership, one-time import, and linked-refresh model is worth keeping.

Playlist matching uses metadata, but it must not depend on live global-catalog availability. A source row already supplies provider identity and descriptive evidence. The matcher should identify or create the canonical recording, reuse known routes, and schedule bounded discovery only for missing routes.

Required behavior:

- Preserve every source row and source order, including unresolved rows.
- Attach a source row to one canonical recording and zero or more accepted routes.
- Reuse a verified route across playlists without repeating fuzzy discovery.
- Preserve manual pins and rejections across refresh, rematch, algorithm upgrades, and provider outages.
- Allow one-time imports to stop consulting the source after their frozen snapshot.
- Allow linked imports to refresh without replacing user authority or silently dropping rows.
- Project the full mapped playlist to listeners. An unresolved row stays visible but unplayable; it is not removed to make completion statistics look better.
- Keep source playlist identity separate from the provider ultimately used for playback.

### 3. Streaming and route selection

Streaming must consume an already established identity and route set. It must never run fuzzy matching or remote metadata discovery after the listener presses Play.

Required route order:

1. An explicit manual route, when its authority policy requires it.
2. Any accepted, authorized local native-backend route.
3. Determine the best allowed representation each accepted, authorized external route can actually provide for the current account.
4. Among routes that satisfy the requested normalized quality tier, follow the user's effective provider order.
5. Within the chosen provider, use the closest allowed representation for the target without exceeding a request or bandwidth ceiling.
6. Before response bytes are committed, fall through to the next eligible route on bounded availability or media failures.
7. After every route satisfying the target tier is exhausted, descend through lower tiers only when `Allow quality downgrade` is enabled, preserving provider order within each tier.

Confidence answers “is this the same recording?” It does not answer “which provider should play it?” Provider priority and quality never inflate confidence. Once routes are accepted as the same recording, local wins; external quality eligibility forms the candidate tier, and configured provider order decides among candidates in that tier.

Do not rank providers using one raw bitrate-distance score. Codec, lossless status, bit depth, sample rate, channel layout, and bitrate are normalized into named quality tiers. The target describes the desired tier, while request/client constraints may impose a lower ceiling. Provider/account capability is an eligibility fact, not a preference bonus.

The existing typed provider router, protocol gateway, streaming leases, response validation, and serving-provider observations are the correct foundation. Complete the range, startup-deadline, cache-ownership, and client-qualification work in those owners instead of adding another router.

### 4. Settings, accounts, and policy

Settings are part of the playback contract. A policy is not complete until foreground requests, background jobs, caches, projections, and diagnostics all resolve the same effective value.

Keep three categories distinct:

| Category | Examples | Owner |
| --- | --- | --- |
| Deployment-owned | backend kind and URL, PostgreSQL, bind/security, managed paths, encryption key ring | Compose/environment and operator documentation |
| Runtime policy | provider order, quality target, quality-downgrade permission, matching thresholds, retention, schedules | typed durable settings in PostgreSQL |
| User/account choice | private or shared provider account, personal playlist source, per-user routing override where allowed | scoped provider accounts and user preferences |

Required changes:

- Keep the typed setting catalog, validation, revision checks, transactional writes, audit events, and change notifications.
- Replace mutable `IOptions`/configuration projection with immutable effective-policy snapshots consumed by the owning services.
- Derive provider choices and UI schema from the typed capability registry and setting catalog; remove the manually duplicated support catalog.
- Invalidate only state affected by a setting change instead of purging the complete playback cache for most changes.
- Make effective precedence explicit: deployment constraint, tenant default, optional user override, then request constraint.
- Resolve and expose an account-scoped quality envelope for every streaming route. A provider name alone must not imply a tier because free, trial, premium, regional, and gateway accounts may return different representations.
- Prove that private accounts never leak through a cache hit, job, playlist projection, or fallback. A shared/global account remains an explicit owner-authorized choice.
- Keep secret material encrypted and out of settings responses, logs, exports, fixtures, and activity details.

### 5. PostgreSQL and durable work

PostgreSQL is the sole durable database and is a release pillar, not an implementation detail. The current schema has strong foundations: tenants and backend identities, scoped encrypted accounts, jobs and outbox, canonical recordings, provider identities, playlist snapshots, match history, audits, health state, managed files, backups, and migrations.

The catalog graph must expand through the existing storage and identity owners:

```text
Canonical artist
  └─ release group
      └─ release/edition
          └─ release track ──> canonical recording
                                  ├─ local native-backend item route(s)
                                  ├─ Apple Music route(s)
                                  ├─ Deezer route(s)
                                  ├─ Qobuz route(s)
                                  └─ acquired/kept lifecycle
```

Definitions matter:

- A **recording** is the audio performance identity. Live, studio, remix, instrumental, clean, and explicit recordings remain distinct.
- A **release track** places a recording on a specific edition and position. One recording may occur on several releases.
- A **route** is an authorized playable representation. It can appear, disappear, or change health without changing recording identity.
- A **catalog fact** has source, freshness, and confidence/provenance. A provider payload is not silently copied into permanent truth.

Required changes:

- Extend the current canonical-recording/provider-identity graph; do not create a parallel catalog or matching database.
- Add canonical artist, release-group, release, and release-track ownership plus aliases for old protocol/provider IDs.
- Attach multiple local library items and external identities to a recording, then select among accepted routes at playback time.
- Scope downloaded mappings and cached artifacts by the same tenant, user/account, library, and capability rules used at authorization time.
- Complete keep/download reconciliation: verified placement, backend scan request, observed native item, canonical attachment, and local-first transition.
- Add indexes and paging based on measured search, playlist, and review queries on real PostgreSQL data.
- Preserve forward-only migrations and tested backup/restore. Do not squash published migrations merely to reduce file count.
- Keep shelved-feature tables intact unless a separately reviewed migration proves safe deletion; shelving UI and runtime registration must not destroy user data.

### 6. Acquisition and local transition

Growing the user's local library is a primary product outcome, not an optional download utility. “Keep” is complete only when an external recording becomes an observed native-backend item and the same canonical identity starts routing locally.

Required behavior:

- Use one durable workflow for an explicit keep, playlist-retention policy, or opt-in favorite action.
- Authorize the exact user/account route, download to a bounded workspace, verify media, and place it only in an explicitly owned managed root.
- Enrich and tag opportunistically; a metadata outage may delay enrichment but must not corrupt or discard verified media.
- Request the selected backend's scan/rescan, observe the resulting native item, attach its local identity, and preserve playlist/search references.
- Treat backend refresh as a capability. If a Jellyfin or Subsonic/OpenSubsonic server does not expose an authorized refresh operation, keep the acquisition in an honest `awaiting backend scan` state, show the operator action required, and reconcile by bounded polling after the external/manual scan occurs.
- Show queued, downloading, verifying, awaiting scan, available locally, retryable failure, and permanent failure honestly.
- Never modify an original backend library file or treat a successful copy/scan request as proof that the selected backend indexed the recording.

## Cross-cutting release contracts

The six pillars are not sufficient unless the following contracts hold across all of them.

### Native-backend identity and client compatibility

- The configured Jellyfin or Subsonic/OpenSubsonic server remains the authentication and native-library authority. Allstarr maps each authenticated backend identity to an exact tenant/user scope and never substitutes an administrator account for ordinary playback.
- Internally, a native item participates in canonical identity and is an accepted local route. At the protocol boundary, a native representative remains preferred: preserve Jellyfin objects and IDs or Subsonic/OpenSubsonic IDs and field semantics when Allstarr has no reason to inject or translate the item.
- If a canonical recording has a native representative, project that native item without `[A]`. If it has no native representative, synthesize a stable Allstarr item and use `[A]` or `[A]/[E]` where the client needs a virtual-track marker. Do not append `[J/A]`, `[S/A]`, or provider suffixes to canonical artist and album names.
- Define and fixture the virtual-item contract separately for Jellyfin JSON and Subsonic/OpenSubsonic XML/JSON: stable IDs, parent/artist/release navigation, playback URLs, ranges, transcoding parameters, favorites/stars, playlists, scrobbling/now-playing reports, and error shapes.
- Maintain an explicit backend/client matrix. A generic HTTP success is not qualification for Musiver, official Jellyfin clients, Navidrome-connected Subsonic clients, or another named client.

### Artwork and presentation

- Canonical artists and releases need stable artwork identities independent of the provider currently serving the image.
- Prefer an accepted local native-backend image, then cached canonical artwork, then configured external sources. Record provenance and avoid embedding expiring provider URLs in durable objects.
- Cache images with bounded size, content-type validation, negative-cache expiry, and tenant/account rules where the source is not public.
- Missing artwork degrades to a consistent placeholder and never breaks search, browse, playlist projection, or playback.

### Favorite, Keep, and playlist membership semantics

- **Favorite** is a user's preference signal and, only when explicitly configured, may enqueue a Keep action.
- **Keep** is an acquisition request with a durable lifecycle; it is not complete until the recording is observed in the selected native backend.
- **Playlist membership** expresses ordering/collection intent. Importing or linking a playlist does not silently favorite or download every entry unless the user selects a retention policy.
- Removing a favorite, playlist membership, or source link does not delete a managed file implicitly. Unkeep/removal requires an ownership/reference check and an explicit confirmation path.

### Provider and extension onboarding

- A user can understand which capability an account supplies: playlist source, metadata, streaming, download, or lyrics. One login must not imply capabilities supplied by a different gateway.
- Private versus shared/global scope is selected explicitly, is change-audited, and is testable before saving.
- Keep the extension runtime as a release feature for provider capabilities: manifest compatibility, permission declarations, scoped accounts/secrets, package verification, health, disable, and rollback.
- Third-party extensions use the same capability registry, durable jobs, routing, cache, and account rules as built-ins. They cannot replace reserved providers or introduce parallel matching/storage owners.
- Ship a curated marketplace/control plane where users can browse and search extensions, inspect authorship, source, license, version compatibility, capabilities, permissions, verification status, release notes, and available updates before installation.
- Support explicit installation from the marketplace, a package/file, or an approved source URL; manual update with permission/version review; disable; rollback; and uninstall with owned-state checks.
- Update discovery is automatic, but installing an update requires user approval. Unattended automatic installation may be considered later only with signed packages, permission-diff blocking, health verification, and automatic rollback.
- Provider-specific onboarding uses declarative account/settings fields and bounded authentication callbacks. Arbitrary extension UI injection, extension-owned schedulers outside the durable job system, and unrestricted filesystem/process access remain out of scope.

### Failure, observability, and recovery

- Every primary operation reports a stable correlation/job ID, current state, last actionable failure, retry policy, and safe recovery action without exposing secrets or raw provider payloads.
- Activity is an operational projection of authoritative events, not a second state store. Its labels must reflect the persisted decision and actual serving route.
- Provider health, account eligibility, catalog freshness, route rejection, cache result, fallback reason, and first-byte timing are inspectable by an administrator and appropriately redacted for users.
- Playback diagnostics record the requested tier, account-scoped advertised ceiling, actual opened codec/container/bitrate/sample properties when measurable, downgrade decision, and serving route. Never record the credential itself.
- Rate limits, backpressure, concurrency, retry budgets, and circuit state are owned centrally per capability/account; a provider outage cannot create unbounded work.
- Cancellation and restart are safe at every durable stage. Recovery never turns an unresolved or tentative identity into an accepted route.

### Upgrade and compatibility

- Publish the supported upgrade path, required backups, rollback limits, and whether the release accepts an existing database or requires a fresh baseline.
- Migrate legacy provider-shaped IDs, playlist links, runtime settings, match authority, and managed-file records transactionally or through resumable durable jobs.
- Keep old IDs as aliases for the documented compatibility window; measure alias use before removal.
- Test upgrade, interrupted migration, backup, restore, and rollback against a realistic copy of PostgreSQL and managed-file metadata.

## What is solid, what changes, and what leaves the release

| Area | Current assessment | Disposition | Required release action |
| --- | --- | --- | --- |
| Native Jellyfin proxy | Broad auth, browse, art, playlist, favorite, stream, and session coverage | **Keep and qualify** | Qualify Jellyfin 12 and named clients; preserve native objects unchanged |
| Native Subsonic/OpenSubsonic proxy | Query/form authentication, XML/JSON, browse/search, art, lyrics, playlists, stars, streaming, and observations are present | **Keep and qualify** | Qualify the supported OpenSubsonic contract against Navidrome and named clients; preserve native identities and semantics |
| Canonical recording/provider identity | Already models one recording to many scoped provider identities | **Keep and extend** | Add artist/release structure, aliases, and stable protocol projection |
| Typed capability registry and router | Correct capability/account/priority boundary | **Keep** | Make it the only provider selection owner |
| Match decision engine | Strong evidence, version safety, manual pins/rejections, versioned rematch | **Keep** | Freeze semantics, add catalog reuse, split orchestration only after regressions exist |
| Playlist snapshots and projections | Durable source intent, ordering, refresh, and route overlay | **Keep** | Consolidate legacy imports and finish listener projection/reconciliation |
| Protocol provider gateway | Correct home for leases, pre-response fallback, and serving-provider observation | **Keep** | Finish stable IDs, ranges, cache scope, and startup timing |
| Durable jobs, leases, retries, and outbox | Correct home for retryable state changes | **Keep** | Move any remaining detached or controller-owned long work here |
| Durable settings catalog | Typed, validated, audited, transactional | **Keep** | Replace mutable runtime projection and duplicated schema/support lists |
| Provider account and secret scope | Strong storage model and encrypted references | **Keep** | Prove two-user foreground, job, cache, and playlist isolation |
| Managed-file safety and full backup/restore | Clear owned-path and recovery boundaries | **Keep** | Route every keep/acquisition entry point through the same owner |
| Federated search | Useful concurrent local/external baseline | **Modify substantially** | Canonical merge, stable IDs, catalog projection, unified browse |
| `TrackMatchCommandService` | Valuable behavior concentrated in an oversized owner | **Clean after behavior freeze** | Separate commands, queries/projections, and discovery without duplicating rules |
| Settings/admin controllers | Functional but mix schema, transfer, restart, cache, diagnostics, and policy | **Split and reduce** | Keep normal policy/account flows; move operator recovery out of ordinary UI |
| Legacy metadata aggregation | Coexists with typed metadata capability gateway | **Consolidate/delete** | Move remaining callers, add parity tests, remove duplicate owner |
| Legacy download-and-stream path | Coexists with typed leases and durable downloads | **Consolidate/delete** | Preserve range/failure fixtures, migrate callers, remove old fallback |
| Old Spotify playlist setting API | Coexists with durable playlist links | **Delete after migration** | Migrate active state and keep one playlist-link API |
| Registered legacy M3U playlist sync service | No production caller found in the prior audit; it is separate from the active Subsonic/OpenSubsonic protocol surface | **Delete if final reference check agrees** | Remove only the orphan registration, code, and obsolete tests/docs without reducing protocol playlist support |
| Manual provider support catalog | Duplicates the typed capability registry | **Delete** | Generate support/settings presentation from registry descriptors |
| Docker-socket restart and environment export UI | Deployment-specific and broadens the admin controller | **Shelve from product UI** | Keep documented operator CLI/Compose procedures |
| Selective state-transfer UI | Large advanced maintenance surface | **Shelve** | Retain and qualify full backup/restore first |
| Intelligence, history imports, recommendations, and AudioMuse workbench | Large, high-churn feature family outside the primary journey | **Shelve from release composition** | Stop routes/jobs/entry points in release builds while preserving data and explicit development access |
| Extension marketplace and control plane | Sophisticated runtime exists, but distribution, manual updates, and a qualified package journey are incomplete | **Keep and finish** | Ship curated discovery, permission review, install, update detection, manual update, health, disable, rollback, and uninstall |
| Lyrics and scrobble delivery | Useful supporting features that can degrade independently | **Keep if isolated** | Do not let failures block browse, playlist import, or playback |
| Lidarr integration | Useful possible acquisition/organization adapter, not identity authority | **Shelve** | Reconsider after direct keep-to-native-backend reconciliation is complete |

## Canonical metadata dependency

Search and playlist matching both benefit from canonical metadata, but they use it differently:

| Consumer | Needs canonical metadata for | Must still work when metadata refresh is unavailable? |
| --- | --- | --- |
| Search/browse | merged artists, release hierarchy, deduplication, credits, editions, stable IDs | Yes, from the last local catalog projection; freshness may be shown |
| Playlist matching | identity evidence and reuse across imports | Yes; source metadata and known identities can still resolve or remain reviewable |
| Playback | nothing beyond the persisted canonical ID and accepted routes | Yes; playback must never depend on live metadata lookup |
| Acquisition | tags and post-download reconciliation evidence | Yes; enrichment may retry without losing the acquired artifact |

**Catalog-source decision:** public MusicBrainz and BrainzMash are equivalent `/ws/2` catalog sources behind `MusicBrainzService`. Both feed the same validation, caching, provenance, ingestion, matching, and reconciliation path. A deployment selects one endpoint; changing it does not change identity or matching rules. Source IDs and revisions keep observations and caches separate.

This is not a Lidarr integration. BrainzMash also exposes a Lidarr-shaped API at `https://lidarrapi.brainzmash.cc`, but Allstarr uses only the read-only MusicBrainz-compatible `https://api.brainzmash.cc/ws/2` endpoint. The shared client reads only the artist, release-group, release, recording, search, relationship, image, and identifier data needed for Allstarr's local projection.

Spotify remains a source of user intent and current source metadata. The selected native backend and external streaming providers remain sources of playable candidates. Allstarr resolves the Spotify row to a canonical recording, independently scores local and provider candidates, persists every accepted route, and selects local first followed by the quality/provider policy. BrainzMash neither decides matches nor selects playback routes.

Normal requests identify themselves as `Allstarr/{AppVersion.Version} (+https://github.com/SoPat712/allstarr)`, using the application's authoritative version. Public MusicBrainz uses its published rate limit. BrainzMash use requires operator approval for the Allstarr identity. The operator-authorized `DroppedNeedleApp/backend-test` identity is limited to development qualification and must be removed once Allstarr enters the pool.

All catalog responses are cached in PostgreSQL with source provenance and freshness. Durable jobs refresh them outside request paths. If a source lacks a recording, Allstarr keeps a provisional identity from Spotify or provider evidence and reconciles it later. Search remains available from the last local projection during an outage, and playback never waits for a catalog source.

## Implementation sequence

### Stage 0: Freeze the release boundary

- Define one release composition containing both native protocol adapters, catalog/search, playlists/matching, streaming/routes, accounts/settings, activity, jobs, and recovery. A deployment activates exactly one configured native backend.
- Disable deferred routes, navigation, background registrations, and marketing copy together; do not merely hide tabs.
- Record production line count and owner count for every stage. Each consolidation stage must remove a duplicate owner and should reduce net production code.

Exit: a release build cannot start Intelligence schedules or expose its API/UI accidentally, and no primary flow links to a shelved surface.

Working-tree checkpoint (2026-09-14): Stage 0 is implemented but not deployed. `ReleaseComposition` is the single release-inclusion authority. The default `core` profile excludes the Intelligence and ListenBrainz-intake controllers, recommendation/import/enrichment handlers, AudioMuse capability, recommendation schedules, and direct Intelligence WebUI loading. The explicit `development` profile restores them without deleting persisted data. Playback observations, external scrobbling, playlists, both protocol implementations, and extensions remain composed.

| Stage 0 measure | Start | Current | Interpretation |
| --- | ---: | ---: | --- |
| Production source in the established C#/WebUI/Python scope | 135,298 | 135,417 | +119 lines establish and test one enforceable release boundary; this is infrastructure addition, not a claimed consolidation saving |
| Release-inclusion decision owners | scattered conditionals | 1 | `ReleaseComposition` owns inclusion; host, scheduler, session contract, and WebUI consume it |

The source count excludes tests, migrations/generated EF, documentation, and build output. Subsequent consolidation stages must report against the 135,417-line checkpoint and remove duplicate production ownership rather than merely moving code.

### Stage 1: Make settings and ownership deterministic

- Implement immutable effective-policy snapshots.
- Remove duplicated provider/settings declarations.
- Correct cache and downloaded-mapping scope.
- Prove private/shared accounts with two users across requests, jobs, playlists, and cached playback.

Exit: one diagnostic explains the effective provider order, quality, account scope, and reason for every selected route without exposing secrets.

Working-tree checkpoint (2026-09-14, complete): `EffectiveProviderPolicyResolver` now creates one immutable, tenant-scoped snapshot containing capability order, disabled providers, audio quality, and local-preference policy. Authenticated provider search, playlist discovery, matching, playlist projection, lyrics, activity presentation, protocol playback, managed downloads, click-to-stream diagnostics, and admin schema presentation consume that snapshot instead of the default tenant's mutable process configuration. Matching derives an independent engine from the snapshot, so playlist jobs or rematches for one tenant cannot rewrite another tenant's local-preference window. A client-supplied bandwidth cap may lower playback quality; otherwise the tenant's shared quality policy is passed through the provider contract. The legacy projector may migrate old provider-specific quality ceilings into the shared setting once, but tenant changes no longer mutate singleton Apple, Deezer, or Qobuz options or write provider policy back into global configuration. The admin schema falls back to bootstrap policy only when durable storage is unavailable, preserving the recovery surface. `ProviderOrderPolicyCatalog` is the single owner of provider-order keys and defaults. The administrator-only `GET /api/admin/config/effective-provider-policy` diagnostic reports effective orders, disabled providers, audio quality, account-scope counts, and the local/external selection rules without returning account IDs or secrets. Warmed-file mappings are scoped by tenant, authorized provider account, library, and effective quality; legacy permanent-download mappings remain explicitly separate. Provider priority is evaluated before cache preference. External playback observations retain a safe priority/fallback, account-class, and cache/remote reason; native playback reports `native-local-library`. The remaining `ProviderStatusManager` order readers serve actorless compatibility fallbacks and bootstrap capability readiness only; authenticated protocol routes do not use them as tenant policy. The isolated PostgreSQL matrix proves tenant/account/library/quality cache separation, private/shared account behavior, runtime-setting persistence, migrations, and native transactions. Stage 2 may proceed without another policy owner or credential store.

| Stage 1 checkpoint measure | Stage start | Current | Interpretation |
| --- | ---: | ---: | --- |
| Production source in the established C#/WebUI/Python scope | 135,417 | 135,882 | +465 lines introduce the tenant snapshot, migrate its consumers, scope warmed-file references, and expose safe route diagnostics; no net reduction is claimed yet |
| Provider-order key/default owners | 3+ | 1 canonical definition | `ProviderOrderPolicyCatalog` replaces repeated operational defaults across routing, matching, projection, discovery, status, and metadata readers |

### Stage 2: Expand the canonical schema and ingest catalog facts

- Add artist, release-group, release, release-track, alias, provenance, and freshness records around the existing recording graph.
- Build idempotent ingest and refresh jobs with bounded rate/concurrency policies.
- Migrate existing source snapshots, provider identities, library tracks, and protocol IDs without discarding manual decisions.
- Qualify the shared `/ws/2` adapter against public MusicBrainz and operator-approved BrainzMash access, including missing, stale, duplicate, edition, featured-artist, and explicit/version cases.
- Merge Spotify/provider evidence into provisional records when the selected catalog source has no usable entity, then reconcile those records idempotently when canonical data appears.

Exit: representative Jellyfin, Navidrome/OpenSubsonic, and provider entities converge on one graph; rerunning ingest is safe; the app remains usable from cached data during an upstream outage.

#### Stage 2 status

Working tree as of 2026-09-14; not deployed:

- **Storage:** The tenant-scoped recording stores provider-neutral title, version/disambiguation, duration, explicitness, and provisional status. The same graph owns artists, ordered credits, release groups, editions, release tracks, compatibility aliases, and append-only source facts. Composite tenant foreign keys block cross-tenant edges. A forward migration preserves existing IDs and routes.
- **Evidence:** `CanonicalCatalogEvidenceStore` is the only writer for aliases and source facts. It validates actor scope, normalizes source IDs and JSON, rejects remapping and hash collisions, treats repeated payloads as idempotent, and supersedes changed facts without erasing provenance. PostgreSQL qualification round-tripped the complete artist → release group → edition → release track → recording graph.
- **Catalog client:** The bounded `/ws/2` client supports source-scoped caches and artist, release-group, release, recording, ISRC, media, and release-track reads. Fixtures cover response hierarchy, cache isolation, size limits, rate limiting, cancellation, negative caching, and 401/403 handling. On 2026-09-15, eight operator-authorized BrainzMash requests passed with responses below 78 KB and latency from 656 ms to 11.129 seconds. A literal `Sunroof` search ranked acoustic/remix editions ahead of the base recording, confirming that Allstarr must rank normalized title, artist credit, duration, and version evidence itself.
- **Atomic ingestion:** `MusicBrainzCatalogIngestService` validates the full payload before a serializable transaction, preserves separate editions, replaces provisional fields with canonical facts, and writes provenance through the shared evidence owner. Repeated input preserves IDs and creates no duplicate facts. A disposable PostgreSQL run passed 54 catalog, client, environment, migration-snapshot, and storage tests, including rollback of malformed media.
- **Discovery and refresh:** `MusicBrainzCatalogRefreshQueue` creates seven-day idempotency generations scoped by tenant, user, source, and revision. Recording discovery accepts at most 50 distinct editions. Release refresh accepts one release hierarchy and at most 64 credited artists before atomic ingestion. Both jobs preserve upstream retry delays, separate permanent hierarchy failures from transient failures, and stay outside search and playback requests. The focused lane passes 62/62 tests; a separate PostgreSQL run passes all 21 selected identity, discovery, and ingestion tests.
- **Remaining:** Project legacy identities into the catalog, add the relationship and image request shapes needed by Stage 3, and reconcile provisional records that begin without an MBID.

### Stage 3: Replace provider-shaped search with catalog search

- Query canonical projections and overlay user-authorized route availability.
- Return stable Allstarr IDs and resolve legacy aliases.
- Implement coherent artist, release, track, and discography browse.
- Keep native Jellyfin and Subsonic/OpenSubsonic passthrough behavior and response fixtures unchanged where no Allstarr injection is required.

Exit: the same recording discovered through either native backend and multiple providers appears once, opens one details view, and remains addressable after a route disappears.

### Stage 4: Finish playlist-to-canonical matching

- Resolve source entries to canonical recordings, then discover and persist all accepted routes.
- Use catalog facts as evidence while retaining deterministic offline/source-only behavior.
- Consolidate the old Spotify import/settings and orphan sync paths.
- Complete bulk rematch, manual authority, unresolved visibility, and stable listener projection.

Exit: one-time and linked imports preserve all rows and order; refresh/rematch changes routes without changing playlist identity; accepted local always wins playback.

### Stage 5: Harden the playback path

- Resolve a stable recording ID to accepted routes without remote search.
- Enforce local first; for external routes, enforce target-quality eligibility, configured provider order within the tier, then explicit lower-tier fallback when allowed.
- Normalize each provider/account representation into shared quality tiers and validate the opened response instead of trusting the requested format alone.
- Unify typed streaming/download ownership and delete the legacy fallback path after parity coverage.
- Qualify ranges, seeks, cancellation, authorization failures, provider outage fallthrough, and serving-provider reporting.

Exit: Musiver and the supported Jellyfin and Subsonic/OpenSubsonic clients receive playable bytes inside their agreed startup budgets, and route failures have deterministic bounded behavior.

### Stage 6: Complete external-to-local acquisition

- Unify keep, playlist retention, and favorite-triggered acquisition through durable jobs and managed-file ownership.
- Verify, tag, place, request the selected backend's scan/rescan, observe the new native item, attach it to the canonical recording, and transition future playback to local.
- Present each lifecycle state and recovery action honestly.

Exit: a kept recording survives external-provider removal and remains in the same playlist/search identity through its transition into Jellyfin or Navidrome/OpenSubsonic.

### Stage 7: Qualify extensions and marketplace distribution

- Define the versioned marketplace index, trust/verification metadata, availability behavior, and compatibility rules.
- Complete browse/search, permission inspection, install from curated and explicit sources, update discovery, manual update, disable, rollback, and uninstall journeys.
- Route extension authentication, settings, secrets, networking, background work, cache, matching, and playback through the same bounded owners as built-ins.
- Qualify at least two real provider extensions with different capability combinations so the SDK is proven by use rather than declared stable from abstractions alone.

Exit: a non-developer can discover, inspect, install, configure, update, recover, and remove a provider extension without shell or database access, and a broken extension cannot prevent native-backend use or core startup.

### Stage 8: Reduce and refine the WebUI

- Design around listener/operator tasks rather than internal subsystems.
- Use one responsive shell, scrollable tabs, dialog geometry, table primitives, empty/loading/error states, and theme tokens.
- Remove controls with no valid target and prevent duplicate navigation/configuration owners.
- Keep dense comparison where matching needs it; keep normal listener search and playlists simple.

Exit: desktop and mobile browser suites cover the full real-API journeys with keyboard, touch dragging where used, reduced motion, light/dark themes, and no overflow.

### Stage 9: Release qualification

- Run deterministic unit, PostgreSQL integration, protocol fixture, migration, backup/restore, browser, and provider-failure suites.
- Make code scanning a release gate. Re-run CodeQL on the release commit, fix findings in the shared trust-boundary owner, and close alerts whose reported paths no longer exist only by proving the scanned commit contains their removal. Do not dismiss a live finding merely because it is inconvenient.
- Start with GitHub's current baseline of 525 open findings on `main` commit `9887fd69` (29 high-severity path-injection, cross-site-scripting, or user-controlled-bypass findings; 494 medium-severity log-forging findings; and two medium workflow-permission findings). That scan predates the current `dev` tree and reports removed controllers plus obsolete line locations, so run CodeQL on the release candidate before claiming any C# finding still exists or is fixed. Resolve surviving high-severity boundary findings first, then replace repeated log sanitization with one tested structured-log boundary rather than controller-specific copies. Set explicit least-privilege workflow permissions immediately because that finding is independent of the code rewrite.
- Require zero open critical/high findings for the RC. Every remaining medium/low finding needs either a code fix or a documented, line-specific false-positive decision reviewed against the release commit.
- Add regressions for owned-path containment, traversal and symlink handling, proxy route/auth boundaries, output encoding/content types, and CR/LF-neutralized diagnostic fields. Security fixes must preserve native Jellyfin/Subsonic compatibility and managed-file recovery.
- Run the live Jellyfin 12 smoke and timing suite with the actual configured providers and at least two user scopes.
- Run an equivalent live Subsonic/OpenSubsonic suite against the qualified Navidrome version: authentication modes, ping/capabilities, search/browse, canonical virtual IDs, cover art, playlists, stars, scrobble/now-playing, stream/ranges/transcoding, errors, and at least two user scopes.
- Require the same canonical matching, provider fallback, quality, account-isolation, and acquisition-to-native reconciliation fixtures through both protocol surfaces; protocol adapters may translate shapes but cannot implement separate business rules.
- For every configured streaming account, test representative tracks at every supported requested tier and record whether playback is full-length, the actual returned format/quality, range behavior, first-byte timing, and downgrade/fallback outcome. Treat credential type, observed account tier, region, and provider response as part of the qualification identity.
- Qualify Deezer ARL behavior by probing the connected account rather than assigning a fixed ARL quality. An ARL may yield full-length low-quality playback or a higher tier depending on that account and region. Subscription metadata is only a hint: clamp the stored account-scoped quality envelope to opened, validated media and refresh the observation after authentication or capability changes.
- Cover low- and high-quality account envelopes with deterministic provider fixtures even when live CI has only one account tier. A live run reports exactly what its credential proved; it does not define universal provider capability.
- Record exact commit, image digest, Compose config, backend/client versions, catalog freshness, provider eligibility, timing percentiles, and failures.
- Pilot upgrade and rollback on a copy of real data before the public image is promoted.

Exit: every primary journey has a reproducible automated regression and a recorded live qualification, and the release commit meets the code-scanning gate. Missing credentials or skipped providers are reported as unqualified, not passed.

## Code-reduction rules

Code reduction is a release objective, but deleting safety and observability is not simplification.

1. Keep one owner each for catalog identity, matching decisions, route selection, playlist state, settings, secrets, files, and durable work.
2. Do not introduce compatibility abstractions until there are two active consumers. Delete an old owner after its callers and regressions move to the retained owner.
3. Prefer shared typed policies and projections over controller-local switches and provider-specific copies.
4. Measure production lines removed and registrations eliminated; test growth is acceptable when it protects a consolidation.
5. Split large files at ownership boundaries, not into pass-through classes that preserve the same complexity.
6. Remove redundant comments that narrate syntax. Keep comments that explain protocol constraints, external quirks, invariants, or non-obvious safety decisions.
7. Profile search, matching, and first-byte latency before adding caches or concurrency. An optimization needs a measured bottleneck and an invalidation/ownership model.

## Explicit non-goals for the first public release

- Personalized recommendations, generated mixes, listening-history imports, and the Intelligence workbench.
- A mandatory Lidarr installation or Lidarr as the canonical database.
- Provider-branded duplicate artist/discography experiences.
- Protocol/provider support claims without equivalent qualification evidence.
- Fuzzy discovery, catalog refresh, transcoding changes, or provider splicing after Play has begun.
- Unattended extension updates, ratings/reviews, arbitrary UI injection, or speculative capability types without a qualified provider need.
- Advanced selective state transfer, in-app Docker control, or environment-file editing as ordinary user settings.

## Plan closure and change control

This plan is complete enough to execute without another architecture decision. Remaining unknowns are qualification evidence or external access dependencies, not invitations to create parallel catalog, matching, routing, account, cache, playlist, or job systems.

The following decisions are locked for the release candidate:

1. One canonical Allstarr entity owns many independently accepted routes. Provider identities never become the song, artist, or release identity.
2. Local Jellyfin or Subsonic/OpenSubsonic playback wins whenever it is an accepted match. External quality eligibility is evaluated next, then the effective provider order; confidence is never used as a provider preference bonus.
3. Public MusicBrainz and BrainzMash are equivalent `/ws/2` sources behind one catalog client. A deployment selects either endpoint without changing identity or matching semantics. Caches and provenance remain source-scoped. The separate Lidarr/Aurral API is not implemented. Metadata is projected into PostgreSQL and never enters the Play critical path.
4. Playlist source rows, order, unresolved visibility, and manual authority survive refreshes and algorithm revisions. One-time and linked imports remain distinct.
5. Keep is not successful until the selected native backend indexes the file and Allstarr reconciles its local route.
6. Accounts, caches, jobs, routes, and artifacts use the same tenant/user/library/account boundaries. Sharing is explicit and audited.
7. Extensions and a curated marketplace ship through the existing capability and permission boundaries; extension-owned schedulers and arbitrary UI/process access do not.
8. Intelligence and recommendations remain outside the core release composition while now-playing observation and independently configured scrobbling remain available.
9. Refactoring must retire duplicate owners and report net production-line and registration changes. Test or migration growth is not counted as application bloat, and safety is not deleted to improve a line count.
10. The release candidate must pass the deterministic, PostgreSQL, browser, protocol, live-provider, performance, recovery, and fresh CodeQL gates in Stage 9 before deployment to users.

Normal deployments identify requests as `Allstarr/{AppVersion.Version} (+https://github.com/SoPat712/allstarr)` and may use public MusicBrainz immediately under its published limits. BrainzMash requires operator approval for that identity. The temporary `DroppedNeedleApp/backend-test` override is limited to the operator-authorized development qualification and must be removed once Allstarr is approved. The live qualifier records endpoint, source revision, status, latency, byte count, and response shape without storing query text or payloads.

Any proposed change to a locked decision must update this document, the owning architecture/protocol document, and the affected acceptance fixtures in one review. Passing a narrower happy path does not amend the release contract.

This plan supersedes the earlier decision to exclude a unified catalog from the eventual first public release. The narrower [Jellyfin/provider routing plan](jellyfin-provider-routing-plan.md) remains the Jellyfin component-level route contract and an intermediate qualification milestone; the same core rules require an equivalent Subsonic/OpenSubsonic protocol qualification before the public release claims both backends.
