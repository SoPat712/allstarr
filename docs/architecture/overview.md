# Architecture overview

Allstarr is a music middleware service. It presents a Jellyfin or Subsonic-compatible surface, resolves tracks through a provider-neutral capability core, and keeps operational state in PostgreSQL. It is not a general media-server replacement or a local-library organizer.

## Runtime invariants

- Exactly one backend protocol is selected for a deployment: Jellyfin or Subsonic/OpenSubsonic.
- PostgreSQL is the only durable database and is required before state-changing workers run.
- Media bytes live on mounted filesystems. PostgreSQL stores identity, ownership, lifecycle, and cache metadata.
- Provider credentials are encrypted before persistence. The encryption key ring is a separate deployment secret.
- Redis, Valkey, SQLite, mapping JSON files, and cache files are not authorities for runtime state.
- The default Compose stack contains PostgreSQL and Allstarr. Optional upstream services are enabled explicitly through `allstarr.sh`.
- Extensions are installed from administrator-approved registries. Allstarr does not ship a bundled extension registry or third-party extension packages.

## State-ownership matrix

| Owner | Authoritative state | Allowed payloads and limits | Never owns |
| --- | --- | --- | --- |
| PostgreSQL | Accounts and encrypted secret references; tenant runtime settings; admin sessions; playlist links, snapshots, source entries, sync runs and memberships; canonical identities, matches, overrides and provider routes; jobs, schedules, attempts and outbox; health, circuits and audit events; extension registries, packages and permission state; playback, favorites, intelligence, managed-file and cache metadata | Durable business and lifecycle records with tenant/user scope, revisions, constraints and migrations | Audio/artwork bytes, extension package bytes, backup archives or encryption key material |
| Filesystem | Managed audio and artwork; target playlist files; kept lyrics sidecars; installed extension package payloads; the encryption key ring; verified backup artifacts and bounded temporary transfer archives | Rebuildable media cache with bounded size/TTL; atomic staging files beside an allowed final payload | Accounts, sessions, settings, mappings, accepted decisions, playlist membership/order, sync timestamps, health, jobs or events |
| Environment / deployment secrets | Process-start bootstrap, security policy and deployment topology: database connection/password-file location, backend selection/endpoints, mounted paths, bind/trust policy, optional service profiles and initial defaults | Read once into startup configuration; secret values may come from mounted secret files | WebUI mutations, per-user credentials, live playlist configuration or any restart-reconciled business state |

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
             +--> PostgreSQL --> durable state, jobs, accounts, mappings, events
             +--> filesystem --> cache, downloads, kept files
```

The public protocol controllers preserve client compatibility. New application behavior belongs in the typed core, not in protocol-specific controller branches.

## Code ownership

| Concern | Current owner |
| --- | --- |
| Composition and middleware | `allstarr/Program.cs` |
| Provider contracts and registration | `allstarr/Core/Capabilities` |
| Provider selection and persisted routes | `allstarr/Core/Routing` |
| Canonical track identity and matching | `allstarr/Core/Matching` |
| Playlist ownership and synchronization | `allstarr/Core/Playlists` |
| Durable jobs, schedules, and outbox | `allstarr/Core/Jobs` |
| PostgreSQL model and migrations | `allstarr/Core/Storage` |
| Runtime settings and legacy import | `allstarr/Core/Settings`, `allstarr/Core/Configuration` |
| Provider accounts and encrypted secrets | `allstarr/Core/Identity`, `allstarr/Core/Secrets` |
| Extension control plane and SDK | `allstarr/Core/Extensions` |
| Playback and listening signals | `allstarr/Core/Playback` |
| Intelligence and generated sets | `allstarr/Core/Intelligence` |
| Managed media lifecycle | `allstarr/Core/ManagedFiles`, `allstarr/Core/Downloads` |
| Admin and protocol HTTP surfaces | `allstarr/Controllers` |
| Current WebUI | `allstarr/wwwroot` |

## Sources, accounts, and capabilities

The product term **Source** covers anything that can supply music data or an action. A source can expose one or more typed capabilities: metadata, playlist discovery, streaming, download, lyrics, health, or scrobbling.

A **provider account** is an encrypted credential and access policy for a source. It can be user-owned or shared according to explicit administrator policy. A source can exist without an account when its capability is public. Routing always considers capability, tenant, user, library, account scope, permission, readiness, and configured priority.

Built-in and extension capabilities meet at `ProviderRegistry`. Extension IDs may not replace reserved built-in provider IDs.

## Track identity and matching

`TrackIdentityService`, backend library indexing, persisted provider routes, and the playlist orchestration layer are the shared path. Accepted decisions are reusable by automatic matching, interactive matching, synchronization, playback, and event projections. Playlist refresh and materialization run through durable playlist links and the `playlist.materialize` job; there is no provider-specific matching coordinator.

## Durable work

State-changing background work uses the durable job queue, schedules, outbox, leases, retries, cancellation, and owner authorization under `Core/Jobs`. A process-local task is not an acceptable owner for matching, downloads, playlist synchronization, scrobbling, or extension lifecycle work.

PostgreSQL readiness is a mutation boundary. Read-only protocol proxying may remain available during a database incident, but jobs and state changes pause rather than inventing fallback state.

## Cache and media

The application cache combines PostgreSQL metadata, a bounded in-process hot tier, and filesystem media/artwork storage. Cache entries are disposable; durable mappings, accounts, jobs, events, and managed-file ownership are not.

Media assets should be resolved through shared cache policy and key namespaces. Provider tokens, credentials, and signed URLs must not appear in keys, logs, or diagnostics.

## WebUI

The shipped WebUI is a static Lit application in `allstarr/wwwroot/js/webui.js` with shared CSS in `allstarr/wwwroot/css`.

A framework and visual redesign is unfinished work, not current architecture. The replacement defines its own component foundation from current product requirements rather than preserving the Lit-era design. Until cutover, fixes must not create a second source of truth or bypass the existing HTTP contracts.

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
