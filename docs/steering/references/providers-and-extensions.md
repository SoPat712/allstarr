# Providers And Extensions

Use this file for provider routing, extension registry work, source menus, provider logos, Apple MusicKit, gamdl/wrapper, and capability design. The root plan is [OVERHAUL.md](../../../OVERHAUL.md).

The root plan owns product decisions, non-goals, and migration phases. This reference specifies the provider and extension contracts needed to implement those decisions; it should not restate a competing roadmap. When a contract change affects the root plan, update both documents in the same change.

## Current Code

- [allstarr/Services/Common/ExtensionManager.cs](../../../allstarr/Services/Common/ExtensionManager.cs)
- [allstarr/Services/Common/MultiProviderMetadataService.cs](../../../allstarr/Services/Common/MultiProviderMetadataService.cs)
- [allstarr/Services/Common/MultiProviderDownloadService.cs](../../../allstarr/Services/Common/MultiProviderDownloadService.cs)
- [allstarr/Services/Common/ProviderStatusManager.cs](../../../allstarr/Services/Common/ProviderStatusManager.cs)
- [docs/steering/PROVIDERS.md](../PROVIDERS.md)
- [docs/steering/DOWNLOADS.md](../DOWNLOADS.md)

### Current Support Summary

The current visible source of truth is
[CurrentProviderSupportCatalog.cs](../../../allstarr/Services/Common/CurrentProviderSupportCatalog.cs).
`AdminUiController` includes it as `providerSupportMatrix`, and
`CurrentProviderSupportCatalogTests` prevents unavailable capabilities from being advertised.
These states describe checked-in Allstarr adapters, not everything an upstream service can do:

- `supported`: the current adapter implements the stated read/operation with focused coverage;
- `partial`: a usable path exists, but its protocol, account, lifecycle, paging, range, artifact, or contract coverage is incomplete;
- `policy_blocked`: code may remain, but current routing deliberately excludes the capability; and
- `unavailable`: no current Allstarr adapter is registered for that capability.

| Provider | Current Allstarr capabilities | Current account/config boundary | Current limits |
| --- | --- | --- | --- |
| `apple-download` (legacy service alias `applemusic`) | Partial song metadata, download-backed streaming, single-song download, and compatible-gateway health/status. | One deployment-global external provider gateway URL and its account/session flow. | No verified provider range lease, album/playlist/library job, music-video route, synced-lyrics artifact route, or broad managed-artifact contract. MusicKit is a separate provider account. |
| `apple-musickit` | Supported account-bound personal-library song, album, and artist search/lookups plus playlist reads. | Per-user developer token and Music User Token in an encrypted provider-account secret. | This is the user's library lane. Catalog-wide metadata and ISRC lookup are not inferred from personal-library access. |
| `deezer` | Supported public metadata and playlist reads; partial download-backed streaming and track download. | Public metadata needs no ARL. Streaming/download use the deployment-global ARL. | No typed stream lease or complete durable progress/cancellation/artifact contract. |
| `qobuz` | Partial catalog/playlist reads, download-backed streaming, and track download. | Metadata/playlist reads do not require the download account; streaming/download use the deployment-global token/user ID plus discovered app credentials. | Artist-track, paging, typed stream lease, progress/cancellation, and signed-URL failures are not fully characterized. |
| `squidwtf` | Partial metadata only. Streaming, download, and playlist are policy-blocked. | Discovered public metadata endpoint; no user account. Optional uptime discovery is not capability health. | Do not route media until a working endpoint and contract fixture exist. |
| `spotify` | Partial specialized playlist/import/matching and optional lyrics-sidecar paths. | Legacy global `sp_dc` plus specialized user-cookie mappings in some playlist flows. | No generic `IConcreteMetadataService`; do not advertise Spotify as a current metadata provider. |
| `musicbrainz` | Partial enrichment/identity assistance. | No provider account; a meaningful User-Agent/contact and responsible rate limit are required. | It is not registered as current general search/playback metadata, streaming, or download. |
| `lastfm` / `listenbrainz` | Durable scoped playback delivery and recommendation sources. | Exact tenant/user targets and encrypted credentials; optional targets skip independently. | Provider availability and source-specific candidate quality remain visible rather than being treated as universal readiness. |
| `lyricsplus` / `lrclib` | Partial lyrics-orchestrator sources. | Optional sidecar URL for LyricsPlus; public API for LRCLib. | Provider/account capability contracts and complete protocol fixtures are not present. |
| SDK v1 extensions | Typed metadata, streaming, download, playlist, lyrics, and health hooks through the provider registry. | Every route selects a tenant-authorized provider account whose scope the manifest allows. Raw account secrets remain behind the host broker. | Jint is constrained in-process isolation, not an operating-system process boundary. Packages still need checksum, content-hash, permission, hook, account, health, and routing policy checks. |

Legacy provider settings still exist for compatibility paths. Typed routes use durable provider accounts,
encrypted credential references, backend identity resolution, and explicit global/user/library scope. Do not
borrow a deployment-global credential when an exact account-bound route requires one.

### Runtime Status And Probes

[ProviderStatusManager.cs](../../../allstarr/Services/Common/ProviderStatusManager.cs) remains a compatibility
projection for legacy paths. Typed routes additionally persist capability/account health samples and route decisions:

- Configuration (`NotRequired`, `NeedsConfiguration`, or `Configured`) is separate from observed
  health (`Unknown`, `Testing`, `Healthy`, or `Degraded`). A configured credential is not proof of health.
- Status reads are side-effect free. An untested capability remains `Unknown`, has no fabricated test
  time, and is never reported `Ready`. The compatibility router may still attempt an enabled, configured
  `Unknown` capability only on explicitly retained compatibility paths. Typed routes use durable health and routing.
- Only an explicit capability probe records `Testing` and then `Healthy` or `Degraded`. Observations are
  isolated by provider, capability, and account key. Typed health samples are durable; compatibility observations remain in memory.
- Disabled providers are excluded from every current lane. A failed download probe does not degrade a
  provider's metadata capability. Missing Deezer ARL or Qobuz download credentials do not disable their
  public metadata paths.
- Optional provider startup probes are opt-in through
  `StartupValidation:ProbeOptionalProviders` and default to `false`. The `Testing` environment never
  registers live provider validation as hosted startup work and uses no live SquidWTF discovery.

Do not infer readiness from container health, configuration presence, or a provider-wide compatibility flag.
New typed routes must use the durable account/capability health boundary and redacted route decisions rather
than extending the legacy in-memory projection.

## Contract Boundaries

- A **provider** is one stable capability implementation, identified by an immutable lowercase kebab-case `providerId`. In SDK v1, one extension package exposes one provider and its package `id` is its `providerId`.
- A **provider account** is a configured credential and policy binding for a provider. It is distinct from the provider package, so one provider can have multiple accounts.
- A **provider instance** is an enabled provider/account pairing available to the router. Disabling an instance removes its capabilities from routing without deleting its settings.
- A **backend principal** is the identity supplied by Jellyfin, Subsonic, or another protocol client. A `BackendIdentityResolver` must map it to an Allstarr actor before account selection or a provider call.
- An Allstarr actor may be a user, an administrator acting for a user, or a narrowly scoped system job. Anonymous or unresolved backend principals must not inherit a shared account by accident.

Provider account scope is explicit:

- **Global**: an admin-managed account, optionally allowed for selected capabilities such as shared downloading.
- **User**: an account owned by one Allstarr user; use it for personal playlists, library state, likes, and scrobbling unless an explicit delegation policy says otherwise.
- **Library**: an account tied to one managed library where that distinction is needed.

The router selects only enabled, policy-authorized accounts in the request scope. A shared account must never make one user's personal provider data visible to another user.

## Extension References

- [SpotiFLAC extension repository](https://github.com/spotiflacapp/SpotiFLAC-Extension)
- [SpotiFLAC registry](https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/main/registry.json)
- [SpotiFLAC docs](https://spotiflac.zarz.moe/docs)

SpotiFLAC is a useful registry and capability reference. Allstarr should not auto-add that registry. An administrator can add it manually after reviewing it.

Use SpotiFLAC to test whether the Allstarr registry and capability model is adaptable. Do not treat it as the default Allstarr extension source.

## SDK v1 Scope And Manifest

SDK v1 is provider-only. It supports `metadata`, `streaming`, `download`, `playlist`, `lyrics`, and `health`. The platform may implement enrichment, recommendations, automation, rules, or UI panels internally, but third-party manifests must not declare or execute those hooks in v1. Reserve versioned extension points for them rather than introducing ad hoc hooks early.

Built-ins and first-party packages register through the same capability descriptors and routing contracts. Extractable providers live under `first-party/providers`, and the verified first-party bundle uses the same package lifecycle as reviewed third-party installs.

An SDK v1 manifest is declarative: it names the provider, its supported capability hooks, narrowly scoped permissions, and user-configurable settings. It must not contain credentials, executable URLs, unrestricted file paths, or an unbounded network permission.

```json
{
  "id": "example-provider",
  "displayName": "Example Provider",
  "sdkVersion": "1",
  "entry": "index.js",
  "capabilities": ["metadata", "streaming", "download", "playlist", "lyrics", "health"],
  "permissions": {
    "networkOrigins": ["https://api.example.invalid"],
    "cache": true,
    "secrets": ["apiToken"]
  },
  "settings": [
    {
      "key": "apiToken",
      "type": "secret",
      "scope": "provider-account",
      "label": "API token"
    }
  ],
  "healthProbe": true
}
```

Manifest rules:

- `id`, `sdkVersion`, `entry`, and capability names are validated before install. `entry` must resolve inside the package.
- `id` is immutable after first publication. Use semantic versions for releases and version the manifest/setting schema before making an incompatible change.
- A declared capability is not routable until its required hooks validate and an enabled provider account exists.
- Secret settings are references managed by Allstarr's secret store. They are not values in the manifest, registry, package metadata, logs, route decisions, or WebUI responses.

Registry entry:

```json
{
  "id": "example-provider",
  "displayName": "Example Provider",
  "version": "1.0.0",
  "downloadUrl": "https://example.invalid/example-provider.zip",
  "checksum": "sha256:<64-lowercase-hex-digest>",
  "iconUrl": "https://example.invalid/example-provider.svg",
  "tags": ["metadata", "streaming"],
  "minAllstarrVersion": "1.0.0"
}
```

Every registry release must include the exact SHA-256 content checksum for its package. A checksum gives integrity for a known artifact; it is not publisher identity or permission to execute code. Package signatures can be added in a later SDK version without weakening the checksum requirement.

## Execution Context, Capability, And Route Semantics

The host, not an extension, resolves the actor, provider account, policy, and route before invoking a hook. Every hook receives a typed `ProviderExecutionContext` rather than a raw HTTP request or ambient host state.

| Context field | Required behavior |
| --- | --- |
| Actor and backend principal | Identifies the resolved Allstarr user or scoped system job and supports audit attribution. |
| Provider/account reference | Names the provider instance selected by the router; an extension cannot substitute another user's or global account. |
| Library and route policy | Carries only the applicable library, quality, source, and fallback policy. |
| Operation ID, deadline, cancellation | Correlates logs and lets the host stop expired, canceled, or disabled work. |
| Idempotency key | Is required for stateful or costly work, especially downloads and future playlist mutations. |

Use typed IDs and outcomes across the bridge:

- An external ID contains `providerId`, resource kind, and an opaque provider-native value. Do not infer a resource type from an unstructured string or send an ID to a different provider without an explicit identity mapping.
- Provider identities attach to a provider-neutral canonical recording. One recording may have verified IDs on several providers and several local-library renditions. The old Spotify-specific mapping flow is a compatibility input, not the target source of truth.
- An identity link permits translation only. The router may use a linked ID for streaming or download only when that provider and account were selected, enabled, authorized, healthy enough, and eligible for the requested capability and policy.
- List and search operations use typed query objects and opaque cursors. Results include their source provider and enough identity/match information for the host to explain a route or match.
- Hooks return a typed result, not an exception-shaped string: `Success`, `NotFound`, `NotSupported`, `AccountNeedsConfig`, `Unauthorized`, `Forbidden`, `RateLimited` (with retry timing), `TransientFailure`, `PermanentFailure`, or `Canceled`. Raw provider bodies, tokens, signed URLs, and authorization headers are never surfaced in that result.
- The router records a redacted `RouteDecision`: eligible candidates, selected account, capability, policy reason, health state, and fallback result. It must not fall back across account or provider boundaries in a way that bypasses authorization or exposes personal data.

Capability-specific requirements:

| Capability | Contract requirements |
| --- | --- |
| Metadata | Search may combine providers; direct lookup stays with the ID owner unless the host supplies an explicit mapping. Preserve provider provenance and pagination. |
| Streaming | `getStreamLease` returns an expiry, range/seek support, content metadata, and a safe refresh path. Lease credentials stay inside the proxy boundary and are redacted from logs. |
| Download | `checkAvailability` is side-effect free. `download` is a host-managed, idempotent job with structured progress. The `artifacts.download` broker accepts only an approved HTTPS origin during the exact invocation, streams into the bound managed workspace, enforces path/size/cancellation rules, and returns host-derived checksum and length facts. Extensions never receive a filesystem path. |
| Playlist | Both `getUserPlaylists` and `getPlaylistTracks` require provider-account context, paging, and cancellation. Playlist snapshots include source revision, ordered tracks, name, description, and artwork reference when available. Do not assume a playlist ID alone identifies its owner. |
| Lyrics | Requests use canonical track identity and return source, timing/format, and availability state without treating lyric availability as full lyric retrieval. |
| Health | Probes are capability- and account-specific, non-destructive where possible, bounded by a deadline, and reported separately from ordinary route failures. |

The SDK v1 hook names remain:

- Metadata: `searchTracks`, `getTrack`, `lookupByIsrc`, `searchAlbums`, `getAlbum`, `searchArtists`, `getArtist`.
- Streaming: `getStreamLease`, `probeStream`.
- Download: `checkAvailability`, `download`.
- Playlist: `getUserPlaylists`, `getPlaylistTracks`, `searchPlaylists`.
- Lyrics: `fetchLyrics`.
- Health: `probeMetadata`, `probePlaylist`, `probeStreaming`, `probeDownload`.

## Extension Runtime And Security Model

The SDK bridge is a capability boundary, not an implicit trust boundary. The current in-process JavaScript runtime must not be treated as a sandbox for arbitrary third-party code. Until an isolated worker or sidecar runtime is implemented, third-party packages are trusted-code installs and the WebUI must say so clearly.

The target runtime model is:

- Run each extension in a bounded execution context with no ambient host filesystem, process, environment-variable, or network access.
- Expose only a versioned host bridge. The bridge supplies typed requests, a provider-scoped cache namespace, approved egress origins, and declared secret access; it never supplies a raw `HttpContext`, unrestricted service provider, or arbitrary path.
- Resolve secret settings through a broker only for the declared provider account and permission. Do not serialize secret values into execution context, state, exceptions, telemetry, or UI responses. Mask secrets and signed URLs before persistence.
- Enforce per-extension timeouts, cancellation, concurrency, response-size, storage, and resource limits. Repeated failures should trip a capability/account circuit breaker rather than block unrelated providers.
- Persist redacted extension logs, lifecycle events, permission grants, and route/audit decisions with extension, version, provider account, and operation IDs.

Only platform administrators can add registries, install packages, approve permissions, update packages, or uninstall a globally available extension. Users may configure only the provider accounts and settings they are authorized to own.

### Current SDK v1 Install Boundary

- No registry, including SpotiFLAC, is added automatically. Registries are explicit durable HTTPS records.
- Remote staging remains default-deny through `Extensions:AllowRemoteInstall=false`. Enabling it allows an
  administrator to stage a registry or direct HTTPS package, but never to omit its SHA-256 checksum or review.
- Arbitrary local extension folders do not boot and the legacy enable route cannot activate them. Every active
  package comes from the durable verified lifecycle.
- Archive, expanded size, file count, layout, manifest, SDK, capability, hook, account-scope, and permission
  limits run before activation. A deterministic extracted-content hash is checked again when activating or
  restoring a package.
- Each requested permission receives an explicit decision. Required denials fail the version. Approved network,
  cache, and secret access is enforced by the runtime bridge; denied optional access stays unavailable.
- Activation registers typed capabilities atomically with built-ins. Disable removes them from new route plans.
  Updates are staged with an explicit rollback target. Uninstall protects rollback targets and retains provider
  account and encrypted-secret records.

The author-facing package and bridge guide is [Extension SDK v1](../../../docs/extensions/sdk-v1.md).

## Registry, Direct Install, And Lifecycle

Install flow:

- A registry is never auto-added or auto-installed. An administrator explicitly adds and enables its HTTPS URL after review; SpotiFLAC remains a compatibility reference, not a default source.
- For a registry package, download into an isolated temporary location, calculate the SHA-256 of the exact bytes, and reject it if it differs from the required registry checksum or if the checksum is absent.
- Before extraction, enforce package-size and file-count limits. During extraction, reject path traversal, absolute paths, symlinks, and files outside the package root.
- Parse and validate the manifest, SDK and host compatibility, immutable provider ID, capability/hook agreement, setting schema, entry path, and requested permissions before persisting an install.
- Persist the content hash, registry URL, release version, manifest, approval, and install audit record. Install a verified release as `InstalledDisabled`; enable it only after explicit permission approval and any required account configuration.
- On enable or disable, atomically update the provider registry and source menus. Disable immediately stops new route selection, cancels safe in-flight work, and retains settings unless an administrator chooses to delete them.

Direct URL installation is not a normal registry flow. It is available only to an administrator in an explicitly enabled trusted/development mode, with a visible warning and recorded source, approver, reason, package hash, and trust level. A pinned SHA-256 is still required for trusted installs; an unhashed local-development package may be allowed only behind a separate local-development setting and must remain visibly non-production. Do not silently convert a direct install into a trusted registry package.

Updates are staged, not in-place:

1. Download and verify the new release while the active release continues to serve requests.
2. Validate its manifest, permissions, schema migration, and capability health in isolation. A new or expanded permission always needs a fresh administrator approval.
3. Snapshot the prior active package reference and any reversible settings migration, then atomically switch the provider instance.
4. Keep the prior verified artifact until the new release passes its health window. On startup, health failure, or explicit administrator action, roll back the package reference, settings migration, and route state together.

Uninstall requires explicit confirmation. It disables the extension first, removes it from routing, and separately asks whether to retain or delete each provider account's settings and encrypted secret material. Never delete user-owned account data as a side effect of a package update or uninstall.

## Apple Split

Apple is represented by two separate provider instances:

- `apple-download`: download and download-backed stream behavior exposed by the optional repository gateway. The
  typed lane currently accepts verified song audio only; broader GAMDL outputs remain unavailable until their
  managed-artifact contracts and tests exist.
- `apple-musickit`: per-user MusicKit API access to personal-library playlists and playlist items, library songs/albums/artists, and documented library or favorite-state actions.

References:

- [Apple Music API](https://developer.apple.com/documentation/applemusicapi/)
- [MusicKit](https://developer.apple.com/documentation/MusicKit/)
- [MusicKit Song.hasLyrics](https://developer.apple.com/documentation/musickit/song/haslyrics)
- [gamdl](https://github.com/glomatico/gamdl)

Implementation notes:

- MusicKit user-library APIs require a Music User Token together with the developer-token flow. `mediaUserToken` is not playlist-only; it authorizes the user's personal-library and playlist API operations.
- `mediaUserToken` belongs to the per-user MusicKit/provider-account flow, not the download gateway. A
  GAMDL-backed gateway uses its own browser cookies or wrapper account/session for download, playback, and
  decryption work. Never assume one credential substitutes for the other or pass either raw secret across the
  provider boundary.
- `apple-download` can be a global shared downloader account when admin config chooses that.
- `apple-musickit` is normally per-user because playlists, library songs, and liked/library state belong to the signed-in Apple Music user.
- gamdl supports catalog and library song, album, playlist, artist, and music-video URLs. Its download provider should preserve the media kind, chosen codec/quality, source URL identity, and output artifact metadata in the managed-download record.
- gamdl can output synced lyrics and rich tags with a managed download. Ingest that output after validation; expose it through the lyrics lane only when format, ownership, and routing policy permit. This is not a claim of generic on-demand lyric availability for every Apple track.
- The optional profile ships the HTTP gateway but not Apple libraries. Its configured URL points to that gateway,
  never directly to wrapper-v2. wrapper-v2 has account, playback, and decryption endpoints but is not a GAMDL
  search and download gateway by itself.
- The gateway login UI must call its status endpoint before showing configured and must surface pending 2FA.
- If the gateway has session files but no usable token, show Needs Config.
- Apple lyric availability exists in public APIs, but full lyric retrieval should be optional sidecar or extension behavior.

### Apple Feature Coverage And Upstream Compatibility

Allstarr should expose every gateway feature that maps safely to a declared capability, but it must distinguish
upstream GAMDL support from a gateway route that Allstarr has actually implemented and tested.

| Upstream feature | Allstarr capability target | Current gateway status | Required before advertising |
| --- | --- | --- | --- |
| Catalog and library song, album, playlist, and artist downloads | `apple-download` download and playlist-import inputs | Audio song route exists; broader URL handling must be exposed deliberately. | Provider request model, managed-artifact record, and fake-gateway contract tests. |
| Rich tags, artwork, and downloaded-file metadata | `apple-download` artifact metadata plus enrichment | Upstream capability; preserve only verified output fields. | Artifact parser/validation and metadata-merge tests. |
| Synced LRC/SRT/TTML lyrics | Download artifact, then optional `ILyricsProvider` source | Not advertised without a compatible artifact route. | Explicit gateway output, format/ownership validation, and lyrics response tests. |
| Music videos up to available upstream quality | Download media of kind `music-video`; protocol exposure only where the selected backend/client supports it | No verified gateway video contract yet. | Media-kind contract, video route/job, codec/container validation, and protocol fixtures. |
| Wrapper-backed playback/decryption and high-quality codecs | `apple-download` stream/download policy | Included gateway uses the raw TCP wrapper protocol and advertises verified codecs. | Live account checks remain manual, opt-in, and redacted. |

Every provider descriptor must maintain the same kind of feature-coverage record: upstream capability, Allstarr capability mapping, current implementation state, configuration/account prerequisites, protocol limitations, and contract-test location. A feature may be marked deferred or unsupported only with a visible reason; it must never be silently omitted or advertised before it works.

GAMDL 3.8.2 introduced compatibility with wrapper-v2 0.0.2 and its raw TCP decrypt protocol. The optional profile
locks that exact pair and passes wrapper HTTP and TCP endpoints separately. Treat them as one compatibility pair.
The repository never contains or downloads Apple's native libraries.

### Apple Profile Update Policy

The source lock owns upstream selection. An update changes GAMDL and wrapper-v2 together, then passes gateway,
Compose, architecture, and fake-account contract tests before release. The persistent wrapper session volume is
not replaced during a normal update.

- Never discover or pull provider code during Allstarr startup. Preparation is an explicit operator command.
- Probe the API version, authentication state, health, and capability manifest before activation and after a URL
  change.
- Use fake-gateway contract tests in CI. Automated tests must not require a live Apple account.
- Replace or remove a gateway without deleting provider records, Allstarr-managed media, or MusicKit accounts.
- Follow [the Apple download runbook](../../operations/apple-download-provider.md) for setup and cutover.

## Other Built-In Providers

Deezer:

- Metadata and configured streaming/download behavior.
- Keep streaming and download capabilities separate.

Qobuz:

- Metadata and configured download behavior.
- Must have enable/disable and configure controls.

SquidWTF:

- Metadata-only until working stream/download endpoints exist.
- Uptime feed should use `https://tidal-uptime.props-76styles.workers.dev/` when needed.
- Uptime feed is optional.

Spotify:

- Playlist and liked-song source.
- Matching seed in specialized playlist flows; no current generic metadata-provider registration.
- New playlist and mapping work uses provider-neutral playlist links, canonical recordings, and provider identity links. Spotify-only import remains a compatibility path.
- Can feed virtual playlists or durable manual/scheduled materialization into Jellyfin or a Subsonic-compatible backend. Backend writes stay in the target adapter, not the Spotify provider.

MusicBrainz:

- Enrichment, identity, matching, tagging assistance.
- Not a streaming or download provider.

Last.fm and ListenBrainz:

- Scrobbling and listening profile.
- Recommendation signals.
- Test endpoints must return WebUI-visible errors.

## Provider Priority Rules

Priorities are per capability. Do not use one global primary music service.

Default download priority:

1. Apple download
2. Deezer
3. Qobuz
4. SquidWTF only if download capability exists again

Streaming priority should favor time to first byte and stability. Download priority should favor quality and reliability. Metadata priority should favor completeness and user preference.

Priority is a tie-breaker, not an authorization rule. For every request, the router first filters candidates by enabled state, declared capability, resolved account scope, user policy, and health/circuit state; it then applies the capability priority and records the redacted route decision. In hybrid mode, an administrator can authorize a shared downloader while users retain personal playlist and scrobbling accounts.
