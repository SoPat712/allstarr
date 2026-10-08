# Architecture overview

Allstarr is a music middleware service. It presents a Jellyfin or Subsonic-compatible surface, resolves tracks through a provider-neutral capability core, and keeps operational state in SQLite. It is not a general media-server replacement or a local-library organizer.

## Runtime invariants

- Exactly one backend protocol is selected for a deployment: Jellyfin or Subsonic/OpenSubsonic.
- SQLite is the only durable database and is required before state-changing workers run.
- Media bytes live on mounted filesystems. SQLite stores identity, ownership, and durable lifecycle records.
- Provider credentials are encrypted before persistence. The encryption key ring is a separate deployment secret.
- Redis, Valkey, mapping JSON files, and cache files are not authorities for runtime state.
- One Allstarr process owns the SQLite file. Optional upstream services are enabled explicitly through `allstarr.sh`.
- Extensions are installed from administrator-approved registries. Allstarr does not ship a bundled extension registry or third-party extension packages.

## State-ownership matrix

| Owner | Authoritative state | Allowed payloads and limits | Never owns |
| --- | --- | --- | --- |
| SQLite | Accounts and encrypted secret references; tenant runtime settings; admin sessions; playlist links, snapshots, source entries, sync runs and memberships; canonical identities, matches and overrides; jobs, schedules and attempts; health, circuits and audit events; extension registries, packages and permission state; playback, favorites, intelligence and managed-file metadata | Durable business and lifecycle records with tenant/user scope, revisions, constraints and migrations | Audio/artwork bytes, extension package bytes, backup archives or encryption key material |
| Filesystem | Managed audio and artwork; target playlist files; kept lyrics sidecars; installed extension package payloads; the encryption key ring; verified backup artifacts | Rebuildable media cache with bounded size/TTL; atomic staging files beside an allowed final payload | Accounts, sessions, settings, mappings, accepted decisions, playlist membership/order, sync timestamps, health, jobs or events |
| Environment / deployment secrets | Process-start bootstrap, security policy and deployment topology: data-directory location, backend selection/endpoints, mounted paths, bind/trust policy, optional service profiles and initial defaults | Read once into startup configuration; secret values may come from mounted secret files | WebUI mutations, per-user credentials, live playlist configuration or any restart-reconciled business state |

The database row is authoritative whenever a filesystem payload has lifecycle metadata. Deleting a cache payload may cause a rebuild; deleting a durable row may not be repaired from cache. Legacy `.env` input is accepted only through the explicit preview/apply migration boundary and is never reread as live application state.

## Process layout

```text
music client
    |
    v
Jellyfin or Subsonic protocol controller
    |
    +--> local backend proxy
    |
    +--> playlist, matching, playback, lyrics, and artwork orchestration
             |
             +--> provider router --> built-in or extension capability
             +--> SQLite --> durable state, jobs, accounts, mappings, events
             +--> filesystem --> cache, downloads, kept files
```

The public protocol controllers preserve client compatibility. New application behavior belongs in the typed core, not in protocol-specific controller branches.

## Code ownership

| Concern | Current owner |
| --- | --- |
| Composition and middleware | `allstarr/Program.cs` |
| Provider contracts and registration | `allstarr/Providers/Contracts` |
| Provider selection and routing policy | `allstarr/Providers/Contracts` |
| Canonical track identity and matching | `allstarr/Core/Matching` |
| Playlist ownership and synchronization | `allstarr/Core/Playlists` |
| Durable jobs and schedules | `allstarr/Core/Jobs` |
| SQLite model and migrations | `allstarr/Core/Storage` |
| Runtime settings and legacy import | `allstarr/Core/Settings`, `allstarr/Core/Configuration` |
| Provider accounts and encrypted secrets | `allstarr/Core/Identity`, `allstarr/Core/Secrets` |
| Extension control plane and SDK | `allstarr/Providers/Extensions` |
| Playback and listening signals | `allstarr/Core/Playback` |
| Intelligence and generated sets | `allstarr/Shelved/Intelligence` |
| Managed media lifecycle | `allstarr/Core/ManagedFiles`, `allstarr/Core/Downloads` |
| Admin and protocol HTTP surfaces | `allstarr/Controllers` |
| WebUI source and static assets | `webui/`, `allstarr/wwwroot` |

## Sources, accounts, and capabilities

The product term **Source** covers anything that can supply music data or an action. A source can expose one or more typed capabilities: metadata, playlist discovery, streaming, download, lyrics, health, or scrobbling.

A **provider account** is an encrypted credential and access policy for a source. It can be user-owned or shared according to explicit administrator policy. A source can exist without an account when its capability is public. Routing always considers capability, tenant, user, library, account scope, permission, readiness, and configured priority.

Built-in and extension capabilities meet at `ProviderRegistry`. Extension IDs may not replace reserved built-in provider IDs.

Account ownership is authoritative: a non-null `OwnerUserId` means Personal; null means Shared. The public scope is derived rather than stored independently. Creator identity is audit provenance and never grants access. Administrators manage account records; routing still selects only the requesting user’s Personal account or a Shared account. Eligible Personal accounts precede Shared accounts, with deterministic ordering within each audience. Library IDs do not scope accounts.

Audience changes rebind the encrypted secret in the same database transaction and invalidate account discovery caches. Credential leases verify the current account owner, provider, revision, enabled state, secret reference and purpose before decrypting. The household connection toggle controls listener account creation without disabling existing owner management or use.

## Native-server compatibility

Jellyfin and Subsonic/OpenSubsonic remain the behavioral and data authorities for native objects. Allstarr relays native authentication, browse, item detail, artwork, playlists, playback metadata, streams, user data, and session traffic without reshaping their responses. Unknown upstream fields are part of this contract and pass through unchanged.

Allstarr changes a native response only when a documented feature requires it: external search results, provider-neutral matching and routes, virtual or linked playlists, external playback, lyrics fallback, music-only surface enforcement, or the proxy address used during server discovery. Each changed surface must preserve native rows and identities, declare its added or filtered fields, and have a direct-server-versus-Allstarr parity test. A new interception without that contract is a regression.

## Track identity and matching

`TrackIdentityService`, backend library indexing, persisted provider routes, and the playlist orchestration layer are the shared path. Manual match authority is stored separately from automatic catalog evidence, keyed by source provider and source track hash. A nullable owner distinguishes personal and household choices; reads select personal, then household, then automatic. Provider pins store a target route without rewriting canonical identities. Every projection applies the current viewer’s library access and account-aware provider order. Accepted decisions are reusable by automatic matching, interactive matching, synchronization, playback, and event projections. Candidates that satisfy confidence and artist-evidence requirements are selected by local-first/configured streaming priority, not relative confidence windows. Only tentative selection retains preference windows. Cached provider reuse passes through the same decision engine with current local candidates and rejections; it does not force acceptance or overwrite confidence. Matching-algorithm changes enqueue owner-scoped `track-match.rematch-all` jobs that replace stale automatic decisions in bounded batches while preserving manual authority and append-only history. Playlist refresh and materialization run through durable playlist links and the `playlist.materialize` job; there is no provider-specific matching coordinator. A one-time import reuses its first published source snapshot and no longer requires the source account for later projection or rebuilds. Keep-all retention fans resolved external routes into idempotent `playlist.retain-track` jobs, reauthorizes download accounts for the exact owner and library at execution time, and publishes verified files through the managed-file owner.

Jellyfin search fetches native results with the current viewer’s credentials on every request. The merged native/provider response is not cached, so changing viewers or revoking backend access cannot replay another authorized response. Provider-owned metadata caches remain independent of native authorization.

Library indexing discovers all music libraries, optionally restricted by the deployment's selected library IDs. Local-copy selection uses the current viewer's backend library permissions rather than the identity that indexed the item. Permission results are isolated by backend, viewer and authentication context, expire after 30 seconds, and are invalidated on sign-in. Lookup failures deny indexed local routes; independently authorized provider routes remain eligible. Native playback metadata and artwork require a fresh viewer-filtered item lookup.

Authenticated search keeps successful tracks, albums, and artists when another provider or search category fails. A batched, account-aware identity lookup collapses external track hits only when accepted links identify the same recording; tentative, released, replaced, pinned, and unknown links remain separate. The representative follows configured streaming order. This is still a provider-shaped search result, not the stable canonical protocol ID or native-item merge needed for unified music results.

Playlist snapshot persistence serializes writes for each provider account with a shared process-local lock. Source collection finishes before that lock is acquired, and the database transaction starts only after acquisition. Run one Allstarr process for a deployment; the lock does not coordinate separate processes. Database conflict classification lives in `Core/Storage/DbErrors`, while each operation retains its own bounded retry policy.

## Canonical catalog ingestion

`MusicBrainzService` is the single bounded client for MusicBrainz-compatible
catalog sources. Public MusicBrainz and BrainzMash use the same `/ws/2`
contract and feed the same validation, ingestion, matching, and reconciliation
path. A deployment selects one endpoint without changing catalog semantics;
source IDs and revisions keep caches and provenance separate.
`MusicBrainzCatalogIngestService` turns a validated source hierarchy into
tenant-scoped artists, release groups, editions, release tracks, recordings,
and ordered credits in one serializable SQLite transaction.
`CanonicalCatalogEvidenceStore` remains the only writer for external aliases and
source-stamped facts, whether called independently or inside graph ingestion.
Repeated source payloads preserve IDs and do not create duplicate facts.
`MusicBrainzCatalogDiscoveryJobHandler` expands one known recording into a
validated, deduplicated set of no more than 50 editions. It enqueues one
idempotent release-refresh job per edition; each refresh fetches a bounded
hierarchy and passes it to the ingester through the existing durable worker.
Metadata discovery, refresh, and reconciliation run outside playback; a source
outage never invalidates an already accepted route.

## External playback selection

`ProtocolProviderGateway.OpenStreamAsync` owns both protocol surfaces' external selection. The existing `ProviderRouter` authorizes candidates and translates only verified canonical identities. Authorized managed-cache hits are checked before remote opens; `ManagedTrackCacheService` publishes remote bytes under the actual serving provider/track. Controllers no longer open a warm external file before routing, nor fall through to legacy global credentials after an actor-scoped route miss.

`EffectiveProviderPolicyResolver` reads provider order, disabled providers, audio quality, and local-preference policy for one tenant in one durable-settings query and returns an immutable snapshot. Authenticated protocol search, playlist discovery, matching, projection, lyrics, activity, playback selection, and admin presentation use that snapshot. Process configuration is only the bootstrap fallback and is never rewritten from tenant policy. Provider-order keys and defaults are defined once in `ProviderOrderPolicyCatalog`. A provider disabled in the tenant snapshot cannot be restored merely because it was the catalog source of the requested track.

Protocol playback and managed downloads pass the shared tenant quality ceiling through the provider contract. A protocol client's explicit bandwidth or codec constraint may lower that request, but an unspecified client quality no longer falls back to a process-wide provider setting.

Completed streaming-cache references are keyed by tenant, resolved provider account, library, effective quality, provider, and track. The shared gateway authorizes the route before consulting that reference. Physical audio bytes may occupy the same managed cache root, but a mapping from one private account or tenant cannot authorize another listener.

Provider priority is evaluated one candidate at a time: an available cache entry for a lower-priority provider cannot jump ahead of a higher-priority remote route. The opened stream carries a redacted reason code describing priority or fallback, account-resolution class, and cache or remote delivery. That reason is retained in the scoped playback observation and exposed to administrators without account identifiers.

Remote opening honors the protocol deadline and the lease's single-retry policy. The gateway verifies media headers and prefetches one byte before returning a response, allowing bounded pre-response failover without buffering the song. Cancellation and authorization failures stop selection. Native backend streaming does not enter this path.

The bounded `PlaybackDeliveryActivityStore` retains disposable, one-hour stream-open observations, keyed by actor, device and requested item, with exact backend/library/quality keys for byte-range continuation. These are not durable sessions or match decisions. Durable playback signals capture the observation at ingestion; listening events can replace provisional catalog attribution with confirmed source attribution. No credential or media URL is stored in the observation. See [client compatibility](../operations/client-compatibility.md#external-song-labels-and-playback-sources) for presentation and device/seek limitations.

## Durable work

State-changing background work uses the durable job queue, schedules, leases, retries, cancellation, and owner authorization under `Core/Jobs`. A process-local task is not an acceptable owner for matching, downloads, playlist synchronization, scrobbling, or extension lifecycle work.

Startup opens the SQLite file on local disk, enables WAL, applies the baseline migration, and checks schema compatibility and integrity before marking storage ready. Every connection enables foreign keys, normal synchronization, and a five-second busy timeout. Durable workers wait for readiness. Run one application process per deployment; network filesystems are unsupported.

## Cache and media

Application cache metadata lives only in bounded process memory (16 MiB, 10,000 entries, 1 MiB per entry). Artwork, its descriptors, and lyrics use the bounded file cache. Restarting clears memory entries; file entries retain their absolute expiry. Neither cache tier owns durable records. Settings reports memory and disk usage and supports category-specific cleanup.

Media assets should be resolved through shared cache policy and key namespaces. Provider tokens, credentials, and signed URLs must not appear in keys, logs, or diagnostics.

The complete application-cache key inventory is:

| Key namespace | Rebuildable value | Invalidation |
| --- | --- | --- |
| `search:*` | Provider search response | Short TTL and provider/account revision |
| `metadata:album:*`, `metadata:artist:*`, `musicbrainz:*`, `odesli:*` | Provider metadata or translation response | Bounded TTL and provider/account revision |
| `playback:metadata:*` | External provider playback metadata | Bounded TTL |
| `jellyfin:item-type:*` | Native music-surface type classification | Bounded TTL; never grants item access |
| `backend:libraries:v1:*` | Viewer-authorized backend music libraries | 30-second TTL, sign-in invalidation and authentication/selection identity |
| `lyrics:*` | Provider lyrics response | Bounded TTL and provider/track revision |
| `media:descriptor:*`, `playlist:artwork-descriptor:*`, `artwork:payload:*` | Artwork bytes or descriptor | Bounded media size/TTL and resource revision |
| `playback:signal:dedupe:*` | Short-lived duplicate-signal marker | Five-minute maximum TTL |

Playlist source entries, order, matches, decisions, sync timestamps, sessions, and health never use cache keys. Their read models are rebuilt from SQLite.

## WebUI

The WebUI is a Svelte 5 and SvelteKit static SPA built from `webui/`. ASP.NET
serves its hashed assets on the administration port and remains the sole owner
of API, authentication, and authorization behavior.

## Optional upstream services

- Spotify lyrics uses the pinned upstream `akashrchandran/spotify-lyrics-api` image through the native `spotify-lyrics` Compose profile.
- Apple download uses a legally obtained Apple package, the upstream provider/wrapper, and Allstarr's thin compatibility layer through the native `apple` profile.

Allstarr distributes only its own integration layer. Optional upstream code and artifacts remain owned and distributed by their original projects.

## Related documents

- [Configuration](../operations/configuration.md)
- [Deployment profiles](../operations/deployment-profiles.md)
- [Storage](../operations/storage.md)
- [Extension SDK v1](../extensions/sdk-v1.md)
- [Client compatibility](../operations/client-compatibility.md)
