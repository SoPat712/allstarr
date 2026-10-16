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
| SQLite | Users, accounts and encrypted secret references; household and personal settings; admin sessions; playlist links, snapshots, source entries, sync runs and memberships; canonical identities, matches and overrides; jobs and schedules; audit events and attempt history; extension registries, packages and permission state; playback, favorites, intelligence and managed-file metadata | Durable business and lifecycle records with explicit ownership, revisions, constraints and migrations | Audio/artwork bytes, extension package bytes, backup archives or encryption key material |
| Filesystem | Managed audio and artwork; target playlist files; kept lyrics sidecars; installed extension package payloads; the encryption key ring; the data-folder identity marker; verified backup artifacts | Rebuildable media cache with bounded size/TTL; atomic staging files beside an allowed final payload | Accounts, sessions, settings, mappings, accepted decisions, playlist membership/order, sync timestamps, health, jobs or events |
| In-process provider health | Latest provider/account/revision/capability observations, cooldowns and sidecar readiness | One volatile owner; normal activity and explicit tests supply observations; cheap sidecar readiness refreshes within a minute | Credentials, media bytes, historical health samples or business records |
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
    +--> shared music core: search, provider streams, mixes, catalog merges
    |
    +--> playlist, matching, playback, lyrics, and artwork orchestration
             |
             +--> provider router --> built-in or extension capability
             +--> SQLite --> durable state, jobs, accounts, mappings, events
             +--> filesystem --> cache, downloads, kept files
```

The public protocol controllers preserve client compatibility. Provider metadata and streams pass through the required `IProtocolProviderGateway`; controllers and playback metadata do not select legacy provider services. Public activity metadata and unlinked-principal catalog reads use the same typed router with a public-read actor and cannot resolve account credentials. Only executable typed streaming routes make a provider eligible for playable search. New application behavior belongs in the typed core, not in protocol-specific controller branches.

## Shared music core

`allstarr/Core/Music` holds the music behavior both protocols offer, written once against the provider gateway:

| Owner | Responsibility |
| --- | --- |
| `MusicSearchService` | Integrated search: backend and provider rows are fetched together, provider songs the listener already owns are replaced by the native copy, and each kind is ranked by relevance with native rows winning ties. Jellyfin search hints rank all kinds together before applying their combined limit, with native rows winning relevance ties. Each protocol supplies its backend half through an `INativeMusicSearch` adapter under `Core/Protocols`, which returns native rows exactly as the backend sent them. A failed backend search contributes no rows instead of failing the search. |
| `ProviderStreamOpener` | Opens a provider song once through the routed source, wraps the managed cache, and returns a range- and HEAD-aware response. |
| `MusicMixService` | Similar songs and instant mixes seeded from provider songs, albums, or artists. Song seeds use a provider's similar-tracks capability first when it offers one (Deezer uses the seed artist's radio), then optional Last.fm and ListenBrainz suggestions, then the seed artist's albums and songs. Order is stable for the same seed. |
| `MusicCatalogMerge`, `MusicTopSongs` | Provider albums missing from a library artist, provider songs missing from a library album, and provider songs that extend an artist's top songs. |

Each protocol dialect keeps what is specific to it: authentication, request parsing, paging and per-type result windows, native backend transport and relays, response rendering, and how failures are described to its clients. Native playback and mix seeds keep the backend's own answer. Provider mixes reuse search's known-recording lookup to prefer accessible native songs with their complete payloads; unreadable native copies retain the provider row. Verified aliases collapse while unverified lookalikes remain distinct. Unfiltered library artist and album pages use the shared catalog merger to append missing provider children after owned rows; Jellyfin album-parent interception uses a short-lived type hint from prior authenticated reads, scoped to the backend and principal, so ordinary folder and playlist browses do not cause metadata probes; native fields remain intact, and an empty or failed provider lookup preserves the original response bytes.

## Code ownership

| Concern | Current owner |
| --- | --- |
| Composition and middleware | `allstarr/Program.cs` |
| Provider contracts and registration | `allstarr/Providers/Contracts` |
| Provider selection and routing policy | `allstarr/Providers/Contracts` |
| Shared music behavior across protocols | `allstarr/Core/Music` |
| Protocol gateway, search adapters, and relay shaping | `allstarr/Core/Protocols` |
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

Original-media import uses that same managed-file owner. A read-only preview seals the mounted file inventory into the import receipt, and a durable administrator-bound job hashes originals outside per-file writer transactions. Imported provenance supplies the recording/file association independently of provider download artifacts. Exact filesystem identities, protected permanent references and SQLite immutability guards retain the original bytes in place. `RecordingKeptFiles` reads both publication provenances through its existing household checks, including merged recording IDs; imported originals never receive sidecar writes or cleanup deletion.

Household kept publication belongs to `RecordingKeptFiles` and the existing managed-file owner. A canonical recording route retains its initiating user, source job and verified artifact. The personal source artifact keeps its ownership; the published immutable file has a household scope and no personal source-job link. Publication checks the current job execution, user, exact provider track, source account revision and verified canonical identity before placement and again before acquiring its lasting reference. Temporary placement references permit restart recovery and safe cleanup on rejection. SQLite guards preserve the publication lineage. Household reads verify file ownership and bytes independently of the original provider account; public and unknown principals cannot read them. Legacy artifacts without a recorded track binding remain personal and cannot establish a household route.

Favorite acceptance stages existing household references in the same writer transaction as its state, event, actions and durable job. The ownership store stages reference changes without saving or clearing unrelated tracked rows; SQLite triggers retain reference counts. Each favorite state has a stable heart reference. Final release queues a cleanup for twenty-four hours later, and reheart acquires its reference and cancels cleanup before the download worker runs. Each release lifecycle has a separate cleanup key. The worker rechecks its current execution, actual references and latest release time, then atomically removes zero-interest routes and revokes ownership. Physical deletion follows the committed transaction through the strict removal owner. Cleanup retains the initiating user for history and uses a policy bound only to household kept cleanup, with no provider account or capability; disabling that user does not block housekeeping or authorize ordinary user work.

Kept lyrics reads use the canonical recording or its published source route and the existing household file proof, independently of download mappings and the original source account. Only a regular, exact sibling sidecar is eligible. The durable publication and reuse paths await missing-sidecar generation after committing their references. The writer resolves household sources in a new scope, avoiding a construction cycle with the reader and holding no SQLite writer during provider calls. Cancellation leaves a published copy protected, and a retry can supply its missing sidecar.

A download retry after an account edit uses a new workspace and artifact for the current account revision. The artifact store retains the previous immutable source binding for published copies and reservation recovery. Only the current artifact participates in job reuse. Superseded workspace bytes are discarded when no published route, managed file or placement reservation depends on them; reservation cleanup retries that discard after its grace period. Stale account or job executions cannot supersede a newer artifact.

Before dispatching new claims, the durable worker recovers interrupted job states and awaits resource recovery. The kept owner scans temporary placement reservations in bounded pages. It releases only strictly identified terminal-job reservations whose household scope and original verified artifact still match; pending, retrying and running work stays protected. Release and the resulting grace job commit together. Abandoned unpublished copies require that saved reservation proof again before removal. Recovery does not reactivate source accounts or read credentials, and a source job made active again postpones its cleanup. Once a job outcome is saved, resource recovery also runs for that exact job, so a failed retry cannot strand its earlier reservation. Recovery failures retry without rerunning the handler or writing completion again. The kept owner checks the completed user, type and execution generation and restricts its scan to that job; ordinary playback jobs do not trigger a household scan. Shutdown leaves the saved outcome available to startup recovery.

The managed-file removal owner can resume an interrupted household deletion after ownership revocation has committed. Recovery requires an unpublished household file with no source-job link, actual active references or kept route. An existing file must still match its recorded root, scope, length, checksum and filesystem identity; private files, replacements and symbolic links are refused. A missing path completes without deleting a later appearance. Placement treats every recorded path, including removed files, as reserved; a later copy receives a new path and ownership identity so old cleanup cannot target it. Caller-owned database transactions cannot authorize physical deletion. Removing a kept hardlink leaves its original source intact.

The product term **Source** covers anything that can supply music data or an action. A source can expose one or more typed capabilities: metadata, playlist discovery, streaming, download, lyrics, health, or scrobbling.

A **provider account** is an encrypted credential, any non-secret source settings, and an access policy for a source. It can be Personal or Shared. A source can exist without an account when its capability is public. Routing considers the initiating user, capability, account ownership, permission, readiness, and configured priority. Backend library permissions govern local copies separately.

Public external artwork uses an identity-free `PublicRead` context limited to account-free metadata or a built-in metadata/playlist capability that explicitly supports anonymous reads. It cannot resolve credentials or carry user, account, or job identity. Authenticated artwork uses the viewer's normal provider context; private and virtual playlist artwork remains subject to authorization.

One deployment serves one household. The `users` table has a stable internal ID and a unique backend type/instance/principal tuple. Successful authentication creates the user; verified backend role and name updates refresh that same row. A protocol observation without role information preserves the last verified role. Disabled users cannot start new requests or execute queued work. WebUI sessions recheck the enabled user and exact backend identity and role; a role change invalidates an older session.

Built-in and extension capabilities meet at `ProviderRegistry`. Extension IDs may not replace reserved built-in provider IDs.

Deezer's catalog implements the typed metadata contract directly. Catalog, streaming,
and managed-download capabilities share bounded Deezer HTTP handling; streaming and
downloads share media preparation and decryption. An unreadable track can use a
verified provider alternative while retaining its requested public identity. The
alternative's own ID determines its token and decryption key. No default credential
or shared cookie state participates in an account-scoped request.

Qobuz's typed catalog and playlist reads use the selected account credential only
for that operation; public reads carry no user token. Playlist pages preserve source
order, repeated recordings, and global positions. Playlist and album paging are bounded and
rejects inconsistent offsets. Streaming and downloads share signed media resolution,
with quality fallback limited to the requested ceiling. Public bundle discovery
publishes the app ID and signing keys together and coalesces refreshes.

Account ownership is authoritative: a non-null `OwnerUserId` means Personal; null means Shared. The public scope is derived rather than stored independently. Creator identity is audit provenance and never grants access. Administrators manage account records; routing still selects only the requesting user’s Personal account or a Shared account. Eligible Personal accounts precede Shared accounts, with deterministic ordering within each audience. Library IDs do not scope accounts.

Audience changes rebind the encrypted secret in the same database transaction and invalidate account discovery caches. Credential leases verify the current account owner, provider, revision, enabled state, secret reference and purpose before decrypting. The household connection toggle controls listener account creation without disabling existing owner management or use.

## Native-server compatibility

Jellyfin and Subsonic/OpenSubsonic remain the behavioral and data authorities for native objects. Allstarr relays native authentication, browse, item detail, artwork, playlists, playback metadata, streams, user data, and session traffic without reshaping their responses. Unknown upstream fields are part of this contract and pass through unchanged.

Allstarr changes a native response only when a documented feature requires it: external search results, provider-neutral matching and routes, virtual or linked playlists, external playback, lyrics fallback, provider additions to Subsonic artist, album, and top-song answers, music-only surface enforcement, or the proxy address used during server discovery. Both protocols relay only music methods: Jellyfin through its music-surface policy, Subsonic through an allowlist that refuses every other method with error 70. Each changed surface must preserve native rows and identities, declare its added or filtered fields, and have a direct-server-versus-Allstarr parity test. A new interception without that contract is a regression.

## Track identity and matching

`TrackIdentityService`, backend library indexing, persisted provider routes, and the playlist orchestration layer are the shared path. Manual match authority is stored separately from automatic catalog evidence, keyed by source provider and source track hash. A nullable owner distinguishes personal and household choices; reads select personal, then household, then automatic. Provider pins store a target route without rewriting canonical identities. Every projection applies the current viewer’s library access and account-aware provider order. Accepted decisions are reusable by automatic matching, interactive matching, synchronization, playback, and event projections. Candidates that satisfy confidence, artist-evidence, duration, and recording-version requirements are selected by local-first/configured streaming priority, not relative confidence windows. Only tentative selection retains preference windows. Cached provider reuse passes through the same decision engine with current local candidates and rejections; it does not force acceptance or overwrite confidence. All rematching runs through one durable `track-match.rematch` job with a scope: one song, one playlist, everything, or an algorithm change. A matching-algorithm change enqueues one owner-scoped job per version (restarts don't queue it again); it replaces stale automatic decisions in bounded batches while preserving manual authority and append-only history. Playlist refresh and materialization run through durable playlist links and the `playlist.materialize` job; there is no provider-specific matching coordinator. A one-time import reuses its first published source snapshot and no longer reads the source provider for later projection or rebuilds; account-bound sources still require their exact source account row for ownership checks, although it may be disabled. Public Spotify playlist and user-profile reads use anonymous public-read contexts with no account, while links and published snapshots retain their user owner. Public source metadata does not grant playback access to any account. Spotify operational access tokens stay in memory and are reused until expiry with a 30-second margin. The cache separates account IDs, revisions, secret references and compatibility settings from a shared anonymous token. Concurrent exchanges are coordinated per token scope; an exchange for one account does not block another account or an anonymous cache hit. TOTP secrets are fetched under a separate short lock and cached for five minutes. A rejected token is evicted only from its own scope. Tokens without an expiry are not reused, and explicit credential probes exchange the supplied cookie afresh. Missing-song exports read that same published projection and preserve the viewer’s access scope. Keep-all retention fans resolved external routes into idempotent `playlist.retain-track` jobs, reauthorizes download accounts for the exact owner and capability at execution time, and publishes verified files through the managed-file owner.

Jellyfin search fetches native results with the current viewer’s credentials on every request. The merged native/provider response is not cached, so changing viewers or revoking backend access cannot replay another authorized response. Provider-owned metadata caches remain independent of native authorization. Both protocols run integrated search through `MusicSearchService` and rank native and provider results with the same relevance score, computed before display labels are added. Stronger matches lead regardless of source; native results win equal scores, with each source’s original order retained within ties.

Library indexing discovers all music libraries, optionally restricted by the deployment's selected library IDs. Local-copy selection uses the current viewer's backend library permissions rather than the identity that indexed the item. Permission results are isolated by backend, viewer and authentication context, expire after 30 seconds, and are invalidated on sign-in. Lookup failures deny indexed local routes; independently authorized provider routes remain eligible. Native playback metadata and artwork require a fresh viewer-filtered item lookup.

Authenticated search keeps successful tracks, albums, and artists when another provider or search category fails. A batched, account-aware identity lookup collapses external track hits only when accepted links identify the same recording; tentative, released, replaced, pinned, and unknown links remain separate. The representative follows configured streaming order. Known recordings retain their stable public ID and are projected as native items where the current viewer has access.

Playlist imports use a resolved hybrid view and reconcile one persisted backend destination. New targets are created explicitly, without name-based adoption. The destination choice is stored independently of the assigned target ID, so repeating an import cannot silently switch between new and existing destinations. Destination edits retire all active or pending memberships before the new target is reconciled, so ownership cannot cross playlists. A completed backend write conflicts if the link revision changed while it was running, preserving the newer destination and its ownership. Existing stored workflow modes remain readable; current create and settings writes use the fixed workflow.

Playlist snapshot persistence serializes writes for each provider account with a shared process-local lock. Source collection finishes before that lock is acquired, and the database transaction starts only after acquisition. Run one Allstarr process for a deployment; the lock does not coordinate separate processes. Database conflict classification lives in `Core/Storage/DbErrors`, while each operation retains its own bounded retry policy.

## Recording identity

`TrackIdentityService` owns canonical recordings. Each recording has a stable
`PublicId` assigned at creation from the first known source alias, with
`ext-allstarr-song-{guid}` as the fallback when no source ID is available, optional
`MergedIntoId`, and zero or more `recording_identifiers` rows (ISRC and
MusicBrainz recording IDs today). Identifier writes go through that owner;
exact signals find or enrich one recording. When a used source's existing public ID
and verified ISRC resolve to separate compatible recordings, the identity owner
merges them with an audit entry. A conflicting MusicBrainz identity, or a provider
reporting a different ISRC for a track it served before, leaves that playlist
entry's canonical identity unset while collection continues.
Explicit merges retain the earliest-created recording and its public ID, preserve
the losing public IDs as redirects, and move identifiers and durable recording
references to the survivor without changing provider-account scopes.
Protocol search, track details, and track lists publish the known public ID while
retaining the actual provider ID for metadata and route selection. Identity lookup
and same-recording search grouping are read-only, including Personal account
scope checks. Successful non-HEAD playback creates a missing identity with the
literal requested track ID, including legacy IDs. Favorite jobs retain the same literal ID;
complete imported playlist snapshots retain each source track ID before publication.
Creation and source linking commit together, and repeated uses leave existing manual
and suggested associations unchanged. Generated and merged recording
IDs resolve through the same authorized provider selector; they do not grant
access to another user's Personal account. The library index resolves proven
recordings and exact ISRCs against the viewer's currently accessible libraries,
with personal overrides ahead of household overrides. Search returns the complete
native backend item after a successful backend read; a denied read retains the
external result. Known local recordings use the normal authenticated native
metadata and streaming routes. Provider track links remain on `provider_track_identities`. Allstarr does not
mirror a MusicBrainz catalog graph. `MusicBrainzService` is the bounded `/ws/2` client for optional background
ISRC lookups. When enabled, hourly discovery queues low-priority durable jobs
for recordings with an ISRC and no MBID. Only one exact, unambiguous recording
response may add identifiers or merge recordings through the identity owner.
Conflicting MBIDs leave all recordings unchanged. A source outage never invalidates
an already accepted route; search, playback, scans and match decisions make no
MusicBrainz request. Optional Odesli jobs run after a saved match, cache and rate-limit
public cross-links, and store exact catalog aliases without replacing existing
associations. The shared decision engine reads those aliases as local evidence;
new evidence queues an owner-scoped rematch. Foreground lyrics read only cached
Odesli cross-links and never wait for its network or rate limiter. An Odesli
401, 403, or 410 suppresses uncached requests for one hour; cached links remain usable.
Neither upstream is called by the decision engine. Library scan jobs write one `scan.completed` audit row
plus one row per created or changed track.

## External playback selection

Favorite mutations save the listener's current intent and enqueue their work in one SQLite transaction. The state points to the accepted event, so delayed workers and clock changes cannot restore an older heart. A newer opposite request cancels queued work and requests cancellation of a running download. Workers recheck the current intent, enabled backend principal and job attempt around awaited work; retries do not apply the saved state twice. Existing accepted events are reconciled into that state during upgrade without rewriting job or event history. An external heart reuses an accessible local recording or a household kept copy before downloading. Its lasting managed-file reference uses the favorite state's stable ID. New publication and existing-copy retention both require the exact current accepted event and executing job attempt. The download executor receives the claimed attempt directly rather than looking up a newer generation after an await.

Missing favorite display snapshots are recovered through low-priority durable jobs. Discovery admits at most 100 outstanding requests, and the singleton handler spaces lookups at least one second apart. It reuses published kept display metadata before provider lookups, performs those lookups outside SQLite writer transactions, and rechecks the listener, accepted favorite event and job attempt before filling an empty snapshot. Requests have three attempts with fifteen-minute retry delays; missing snapshots become eligible again the following week. Favorite list reads never contact providers.

Kept routes retain a bounded display snapshot containing title, artists, album, precise duration and explicit-content information. They never store a complete provider or backend song object, artwork URLs, credentials or source paths in that snapshot. Both protocols read this household display information before consulting provider metadata, including after source-account retirement. Legacy routes without a snapshot preserve provider metadata fallback. Upgrade and rollback leave owned bytes, references and SQL ownership guards intact.

`ProtocolProviderGateway.OpenStreamAsync` owns both protocol surfaces' external selection. Both protocols reach it through `ProviderStreamOpener`; each still parses its own quality request and describes failures in its own format. Authorized household kept files are checked before provider planning and cached route misses. Current canonical identity takes precedence; a unique verified publication source can also resolve a legacy provider ID after its personal source account is retired. Ambiguous published sources are declined. Identified clients retain the exact selected managed-file ID across nonzero ranges; an unavailable or replaced file requires playback restart. Direct provider requests without a device identity retain their direct provider route across ranges, while canonical recording seeks retain the requirement for an identified playback selection. The existing `ProviderRouter` authorizes remaining candidates and translates only verified canonical identities. Authorized managed-cache hits are checked before remote opens; `ManagedTrackCacheService` publishes remote bytes under the actual serving provider/track. Controllers no longer open a warm external file before routing, nor fall through to legacy global credentials after an actor-scoped route miss.

`EffectiveProviderPolicyResolver` reads household provider order, disabled providers, audio quality, and local-preference policy and returns an immutable snapshot. Personal explicit-content and display preferences override their household defaults for the requesting user. Authenticated protocol search, playlist discovery, matching, projection, lyrics, activity, playback selection, and dashboard presentation use that snapshot. `DurableRuntimeSettingsService` validates and commits settings and projects live options through one implementation. Process configuration supplies bootstrap defaults. Provider-order keys and defaults are defined once in `ProviderOrderPolicyCatalog`. A disabled provider cannot be restored merely because it was the catalog source of the requested track.

Protocol playback and managed downloads pass the household quality ceiling through the provider contract. A protocol client's explicit bandwidth or codec constraint may lower that request, but an unspecified client quality does not bypass that ceiling.

Completed streaming-cache references are keyed by resolved provider account, effective quality, provider, and track. Active transfers additionally separate account revisions. The shared gateway authorizes the route before consulting either reference. Physical audio bytes may occupy the same managed cache root, but a mapping from one Personal account cannot authorize another listener.

`ManagedTrackCacheService` owns one ephemeral read-ahead transfer per active scope, its incoming file, independent readers and shutdown cleanup. The response body starts headers before releasing its producer; reading continues after the client stops requesting bytes. Flushed bytes are available to concurrent readers. A declared encoded length supports exact ranges and a three-second stall timeout for a seek beyond the current prefix, resetting whenever more bytes become available. Unknown-length responses retain full HTTP 200 delivery from byte zero without range headers until EOF verifies the complete length. Initial audio preparation may wait up to 30 seconds after headers. Closing the last reader allows a ten-second seek reattachment window, then cancels the producer. Host shutdown cancels and awaits owned transfers. Short, oversized, failed and abandoned bodies never publish completed mappings. Publication preserves the bytes delivered on the first play.

Provider priority is evaluated one candidate at a time: an available cache entry for a lower-priority provider cannot jump ahead of a higher-priority remote route. The opened stream carries a redacted reason code describing priority or fallback, account-resolution class, and cache or remote delivery. That reason is retained in the scoped playback observation and exposed to administrators without account identifiers.

Remote opening honors the protocol deadline and the lease's single-retry policy. The gateway verifies media headers and prefetches one byte before returning a response, allowing bounded pre-response failover without buffering the song. Cancellation and authorization failures stop selection. Native backend streaming does not enter this path. Sequential FLAC, MP3 and MP4 bodies pass through bounded shared duration-header guidance lazily, after response headers start and before delivery and cache writes. Only unknown, structurally verified duration fields are filled; existing metadata, sample tables and encoded positions are preserved. Unsupported, malformed, edited or fragmented containers retain their original bytes. An immediate Apple gateway or legacy ID3 prelude on sequential FLAC is removed before both delivery and cache publication, keeping later cached offsets consistent. This guidance supplies a timeline and does not advertise byte ranges.

The bounded `PlaybackDeliveryActivityStore` retains disposable, one-hour stream-open observations, keyed by actor, device and requested item, with exact backend/quality keys for byte-range continuation. These are not durable sessions or match decisions. Durable playback signals capture the observation at ingestion; listening events can replace provisional catalog attribution with confirmed source attribution. The observation retains the serving account revision and selected seek behavior. Lazy duration evidence is bound to the originating stream object and cannot replace a newer request or stronger byte-access evidence. Range continuation rejects an account revision change before looking up cached media. No credential or media URL is stored in the observation. See [client compatibility](../operations/client-compatibility.md#external-song-labels-and-playback-sources) for presentation and device/seek limitations.

Shared lyrics resolution follows the saved lyrics order and the exact listener's effective availability policy. Native backend results enter that same selection loop through request-scoped, authorized callbacks. Media-server lyrics are movable rather than fixed ahead of providers, and the selected native response retains its original dialect fields. Native song metadata must identify the requested item before it can be sent to another source. The catalog provider does not jump ahead of the saved order. The first timed result wins; otherwise the first plain-text result is retained while later sources are tried for timing. A source failure permits fallback, while cancellation stops the lookup. Foreground cross-provider identifiers come only from verified saved recording aliases or existing cached translations. Native canonical identity comes from the authorized library index. A missing exact source ID can use the same account-aware identity router for Apple and extension lyrics, without starting new matching work. The host stamps the selected source ID; provider metadata cannot impersonate a native backend result. Recording-specific source preferences are durable rows keyed by the real canonical recording, with personal choices taking precedence over household choices. An available explicit choice may select plain text; an unavailable or disabled choice falls back to automatic selection without being deleted. Writes compare both layers' revisions and require a fresh full-content preview revision. Recording merges preserve compatible choices and reject conflicting choices in the same scope. Household previews and kept-sidecar publication use household order and only shared or anonymous sources; they never publish a listener's personal source content. The former artist/title LRCLIB override is no longer read, and its cached automatic results use a new namespace.

Provider lyric results may retain immutable timed lines and source segments alongside rendered LRC. Times use .NET tick precision; optional source durations, background flags and synthetic-alignment flags stay distinct from unknown values. Segments may represent syllables or chunks, so the host does not infer whole-word boundaries or missing durations. Timing is bounded and stored with the full response in the existing file cache. Availability-only projections omit both text and timing. Jellyfin and Subsonic continue to receive their normal line lyric responses; auxiliary timing does not change those wire contracts.

Before protocol composition, `StartupConfiguration` runs the existing storage/restore initializer and projects saved household settings through the existing settings catalog. The main host reuses that readiness state. A fresh host serves health and the first backend step without a configured URL; playlist targets report unavailable without contacting a server. `BackendSetupService` verifies a native administrator outside the writer, then saves a create-only connection and optional purpose-bound encrypted service token atomically. Its response completes before the process restarts to compose the selected dialect.

Setup and upgrade share `OnboardingStateService` and the server-owned `SetupGuide` checklist. Per-user reviewed/skipped steps, completion and optimistic revisions use the existing onboarding row. `LegacyConfigurationController` owns bounded uploaded/mounted previews and explicit import application; import receipts complete only their guide step. Product settings remain with their existing owners rather than being copied into wizard-specific configuration.

## Durable work

State-changing background work uses the durable job queue, schedules, restart recovery, retries, cancellation, and owner authorization under `Core/Jobs`. A process-local task is not an acceptable owner for matching, downloads, playlist synchronization, scrobbling, or extension lifecycle work.

One worker owns all in-process executions. Once storage is ready, it immediately requeues work interrupted by the previous process; elapsed time cannot steal a running job. Each claim increments an execution generation, so a stale handler cannot report progress or complete newer work. Interruptions do not spend the provider failure budget. A persisted cancellation remains effective across restart. Temporary storage failures retry recovery or persistence of the produced result without rerunning a finished handler.

The job row holds current state and the latest attempt's timestamps and outcome. Completed attempts are appended transactionally to the existing audit ledger with an exact job subject and initiating user; there is no separate attempt table or heartbeat write. History follows event retention, while the job row retains its latest state and error. The upgrade preserves completed attempts and interrupted work; rollback requeues running work and restores retained attempt history.

Activity Queue and History read that same job row and audit ledger with owner-scoped queries, stable time-and-ID cursors, server-side state/type filters and bounded attempt pages. Deferred work is a scheduled retry whose latest outcome is deferred. Read projections sanitize older errors and permit only known progress fields; job payloads and credential configuration never enter the view. The Tasks read model uses existing playlist schedules and sync runs. Automatic maintenance loops retain their own scheduling and record completed passes in the audit ledger through `BackgroundTaskActivity`; its in-memory running state is disposable, and its next-check estimate does not control execution. The existing update stream and reconnect refresh serve all three views.

Every queued job has an explicit initiating user, including scheduled work using a Shared account. Enqueue and each execution attempt verify that the user is active and the exact saved account remains enabled, owned or Shared, at the saved revision, registered for the requested capability, and bound to a valid credential purpose. Retries never substitute another account. The encrypted secret store rechecks the account and credential again when opening a lease. Listener activity, progress, and event feeds use the recorded user; a shared correlation ID grants no access. Revoked sessions close their live event stream. Each playback report is its own job; finished playback jobs and their ledger history expire with the event-log retention period (see [storage](../operations/storage.md#event-log-and-job-retention)).

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

Playlist source entries, order, matches, decisions, sync timestamps and sessions never use cache keys. Their read models are rebuilt from SQLite. Provider health is volatile: restarting clears observations and cooldowns; normal activity and explicit tests rebuild account status without background account or media probes.

## WebUI

The WebUI is a Svelte 5 and SvelteKit static SPA built from `webui/`. ASP.NET
serves its hashed assets on the administration port and remains the sole owner
of API, authentication, and authorization behavior.

## Optional upstream services

- Spotify lyrics use the native account-bound capability and the existing Spotify token exchange. Its bounded transport and parser are shared with explicit account probes; no lyrics container is required. Playback prefetch uses the same listener-scoped lyrics resolver as protocol requests. Kept-sidecar generation uses its household scope.
- Apple download uses a legally obtained Apple package, the upstream provider/wrapper, and Allstarr's thin compatibility layer through the native `apple` profile.

Allstarr distributes only its own integration layer. Optional upstream code and artifacts remain owned and distributed by their original projects.

## Related documents

- [Configuration](../operations/configuration.md)
- [Deployment profiles](../operations/deployment-profiles.md)
- [Storage](../operations/storage.md)
- [Extension SDK v1](../extensions/sdk-v1.md)
- [Client compatibility](../operations/client-compatibility.md)
