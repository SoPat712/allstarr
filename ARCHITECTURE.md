# Architecture

Allstarr is an ASP.NET Core control plane and protocol gateway. It sits between a music client and one configured backend, then routes optional work through provider capabilities without taking ownership away from the backend or hiding media inside a database.

```text
Jellyfin or Subsonic client
           |
           v
  selected proxy surface (5274)      admin UI/API (5275)
           |                                  |
           +------------ Allstarr core -------+
                    /        |        \
             providers    durable jobs   matching/policy
                  |            |              |
          external services  Postgres      managed roots
                  |            |              |
                  +------ Valkey cache     audio files
           |
           v
 Jellyfin or Subsonic-compatible backend (for example Navidrome)
```

## Boundaries That Matter

Only one proxy protocol is active in a deployment. `BACKEND_TYPE=Jellyfin` exposes Jellyfin-compatible routes. `BACKEND_TYPE=Subsonic` exposes Subsonic/OpenSubsonic routes and relays to a compatible backend such as Navidrome. Both controllers have catch-all routes, so registering both is not supported.

The backend remains authoritative for its library and client authentication. Verified backend principals can be linked to stable Allstarr users. A user ID copied from a path, query string, or payload is never enough to authorize a provider account or user-owned side effect.

The core owns capability routing, policy, durable state, matching, and workflows. Protocol adapters own client-visible response shapes. Providers own their own external HTTP details. This keeps Jellyfin quirks out of provider code and provider credentials out of protocol payloads.

## Storage Model

Postgres stores control-plane state only. This includes identities, provider accounts, encrypted secret versions, jobs and attempts, outbox events, provider health, canonical recordings, provider identities, playlist links, favorite workflows, generated sets, playback checkpoints, and audits.

It does not store song bytes or media blobs. Downloaded, cached, kept, and placed tracks remain filesystem files under configured, operator-accessible roots. Database rows may point to those files and record checksums and ownership.

Valkey is an accelerator for rebuildable cache data. Losing Valkey may make requests slower while caches rebuild, but it must not erase durable jobs, identities, mappings, or playlist state.

Standard Compose selects Postgres explicitly. A custom local deployment may select SQLite explicitly. Allstarr never falls back from an unavailable selected database to a new database of another type. Readiness and state-changing work stop until the selected database and expected schema return.

## Provider Capabilities And Routing

Providers declare capabilities such as metadata, streaming, download, playlist, lyrics, health, enrichment, and recommendations. An authenticated execution context narrows candidate accounts by tenant, user, library, permission, policy, sidecar readiness, and provider health. Streaming and downloading are separate routes and can choose different providers.

One canonical recording can have multiple local-library and provider identities. Exact IDs such as backend item ID, MusicBrainz recording ID, ISRC, and verified provider ID are preferred. Decisions retain scope, verification state, version, and explanation. An accepted exact mapping is not silently overwritten by a later fuzzy guess.

Built-in and packaged providers meet at the same internal capability contracts. Provider packages declare hooks, network access, secrets, and scope in a manifest, then pass checksum, permission, activation, rollback, and compatibility checks. Third-party registries are opt-in.

## Durable Work

Downloads, playlist materialization, favorite actions, playback delivery, recommendations, enrichment, and backend refresh use database-backed jobs. Jobs have idempotency keys, leases, attempts, retry/backoff, cancellation, and operator-visible outcomes. Transactional outbox records keep durable state changes and follow-up delivery in step.

Optional sidecars report capability readiness. An unavailable optional sidecar defers or disables only the dependent work. It does not make unrelated proxy routes unavailable.

Apple downloads use a separately deployed compatible provider gateway. Standard and AIO do not bundle GAMDL or
wrapper-v2. Allstarr owns the configured URL, API compatibility check, health state, and routed capability; the
operator owns the gateway stack, session data, upgrades, and rollback. A raw wrapper-v2 URL is not a compatible
search/download gateway.

## Playlists And Favorites

Provider playlists can stay virtual or be materialized into Jellyfin or a Subsonic-compatible backend. A link records its exact tenant, user, protocol, backend, library, provider account, source, mode, and schedule. Reconcile mode is non-destructive. Recreate mode is explicit. Both preserve source order, reuse existing exact matches, and leave unmatched entries explained without downloading them.

A normal backend favorite or star completes first. If an exact-scope policy opts in, Allstarr records a favorite event and queues ordered actions such as matching, downloading, managed placement, enrichment, and backend refresh. Original libraries remain read-only inputs. Unfavorite may cancel pending optional work but never deletes source or managed audio.

## Managed Files

Allstarr may modify only files it owns and tracks. Placement stages and verifies output, rejects traversal and symlink escapes, then uses an appropriate hardlink, reflink, or copy. Metadata changes cannot rewrite a source-library inode through a hardlink. Removal is a separate confirmed action with scope, ownership, and reference checks.

## Intelligence And Playback

Listening intelligence is opt-in at an exact tenant, user, protocol, backend, and library scope. Retention and purge are explicit. Durable playback observations build habit profiles and can seed Jellyfin InstantMix, Last.fm similar tracks, ListenBrainz collaborative filtering, MusicBrainz-enriched local relationships, local rules, and optional AudioMuse-AI similarity.

Generated sets include explanations and reconcile only local matches into a target playlist. They never turn a recommendation into an implicit download. Scrobble delivery to Last.fm and ListenBrainz uses per-target checkpoints so retries do not duplicate completed work.

## Security And Operations

Provider records hold secret references, not plaintext credentials. Secret versions are protected with AES-GCM using an external key ring. Values are resolved just in time and redacted from logs, job payloads, state-transfer archives, and diagnostics.

The proxy and admin listeners are separate. Admin network access is opt-in and CIDR restricted. Liveness, readiness, structured redacted logs, metrics, diagnostics, provider health, job history, and audit events expose failures without leaking account names, credentials, or media URLs.

Backups cover the selected durable database and carry a strict manifest and checksum. They do not include media folders, caches, or the encryption key ring. Restore is verified into an isolated target before cutover. See [docs/operations/storage.md](docs/operations/storage.md).

## Code Map

- `allstarr/Core/Storage`: database selection, migrations, locks, backup, restore, and state transfer
- `allstarr/Core/Identity` and `Core/Secrets`: users, accounts, scope policy, and encrypted values
- `allstarr/Core/Jobs`: durable queue, workers, attempts, leases, cancellation, and outbox
- `allstarr/Core/Capabilities`, `Core/Routing`, `Core/Providers`: capability contracts and route selection
- `allstarr/Core/Matching`: canonical recordings and provider/local identities
- `allstarr/Core/Protocols`: Jellyfin and Subsonic compatibility adapters
- `allstarr/Core/Playlists`, `Core/Favorites`, `Core/ManagedFiles`: user workflows and filesystem ownership
- `allstarr/Core/Playback` and `Core/Intelligence`: signals, scrobbling, profiles, recommendations, and generated sets
- `allstarr/Controllers`: proxy and admin HTTP surfaces
- `allstarr/Services`: backend adapters, current provider implementations, lyrics, caches, and compatibility services
- `first-party`: SDK-conformant first-party provider packages and reproducible bundle metadata

The implementation charter and phase-level invariants are in [OVERHAUL.md](OVERHAUL.md). Detailed maintainer steering lives under [`docs/steering`](docs/steering/INTRODUCTION.md).
