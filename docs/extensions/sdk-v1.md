# Extension SDK v1

SDK v1 lets an administrator install provider modules without giving them control of the host, database, network policy, or another user's account. Built-ins and extensions expose the same typed capability contracts and meet at `ProviderRegistry`.

## Package boundary

An extension package declares a stable ID, version, runtime, entry point, capabilities, permissions, settings, and content hashes. The current compatibility layer also accepts SpotiFLAC registry metadata and `.sflx` packages, then normalizes them into this contract.

```json
{
  "id": "example-provider",
  "version": "1.2.3",
  "runtime": "javascript",
  "entryPoint": "index.js",
  "capabilities": ["metadata", "playlist", "lyrics"],
  "permissions": ["network"],
  "settings": []
}
```

The manifest and archive are data from an untrusted publisher. Installation validates registry origin, package and content hashes, archive bounds, paths, compatibility, manifest shape, permissions, and reserved IDs before activation.

## Capability contracts

SDK v1 recognizes typed metadata, streaming, download, playlist, lyrics, intelligence, and health hooks. Declare only hooks the package implements.

- Metadata returns normalized song, album, artist, identifiers, and artwork facts.
- Streaming returns an expiring playable response for the requested external track.
- Download writes through the managed download lifecycle and reports media facts.
- Playlist lists user-visible playlists and ordered tracks for the selected account.
- Lyrics returns normalized timed or plain lyrics.
- Intelligence starts and observes analysis jobs, returns clusters and recommendations, searches
  text or lyrics, finds ordered song paths, blends positive/negative song seeds, pages map points,
  and disconnects a remote service.
- Health tests one account and capability without changing unrelated state.

Metadata extensions may also expose `getArtistAlbums` and `getArtistTracks`. Both receive the
provider artist `id`, a `{ limit, cursor }` page, and an optional `expectedSnapshotVersion`, and
return the same typed page shape as `searchAlbums` or `searchTracks`. This lets Jellyfin clients
open an extension-backed artist and browse its complete virtual discography.

Allstarr selects the tenant, user, library, provider account, capability, deadline, and policy before invocation. Extension code cannot select a different account or impersonate another user.

## Accounts and settings

Extension credentials live in provider accounts under **Integrations > Services**. Account fields declared as secrets are encrypted before persistence. Source health, routing, and capability readiness live there too; they are not duplicate credential stores.

Extensions may expose the intelligence contract. AudioMuse-AI is different: its Allstarr adapter is
built in and its self-hosted server URL and optional token are configured from Intelligence.
Allstarr stores the encrypted connection metadata and normalized results; models, workers,
indexes, and the AudioMuse-AI service remain outside Allstarr.
External intelligence service implementations remain outside the Allstarr package.

The intelligence capability requires `recommend`; the remaining hooks are optional:

| Hook | Request | Result |
| --- | --- | --- |
| `startAnalysis` | `rebuild`, `idempotencyKey` | job ID, state, completed/total |
| `getAnalysisProgress` | job ID | state, completed/total, safe code |
| `getClusters` | limit | named clusters with normalized tracks |
| `recommend` | seed track IDs, limit | normalized tracks with score and explanation |
| `search` | query, include lyrics, limit | normalized tracks |
| `findPath` | distinct start/end track IDs, limit | ordered normalized tracks and total distance |
| `blend` | positive/negative track IDs, limit | normalized tracks with score and explanation |
| `getMap` | limit and cursor | normalized 2D track points, projection, and next cursor |
| `disconnect` | idempotency key | disconnected flag |

Normalized intelligence tracks may include a cluster ID, but never a service filesystem path.
Extensions return the selected catalog's stable track IDs. Allstarr owns generated playlists and
media-server writes; intelligence extensions do not write playlists through this capability.

For the SpotiFLAC Apple Music package, catalog metadata can work without a subscription token. Subscription lyrics require the package's `mediaUserToken`. This is separate from Allstarr's built-in Apple MusicKit playlist account and the optional Apple download gateway.

## Runtime and permissions

JavaScript packages run in the constrained Jint compatibility runtime with bounded bridges. A package receives only the APIs granted by its reviewed permissions. Network access is allowlisted and request-bounded. File access is package-scoped. Host process, Docker socket, arbitrary filesystem, and raw secret-store access are not capabilities.

Permission changes require administrator review before an update can activate. Disable, rollback, uninstall, and registry removal are control-plane operations with durable state and audit events.

## Registries and ownership

Allstarr ships the SDK and control plane, not a bundled registry or third-party extension packages. Administrators add registries explicitly. A registry cannot be removed while installed packages still depend on it; remove those packages first.

Reserved built-in provider IDs cannot be replaced by a registry package. Package icons and descriptions come from reviewed registry/package metadata and are cached through the shared media/cache layer.

## Lifecycle

1. Add or refresh a registry.
2. Review package identity, publisher, version, capabilities, permissions, and settings.
3. Install the package into a staged state.
4. Configure an account if required.
5. Test capability health.
6. Enable the package.
7. Review permission changes before an update.
8. Disable, roll back, or uninstall without deleting unrelated provider state.

## Owning code

- `allstarr/Core/Extensions/ExtensionSdkV1.cs`
- `allstarr/Core/Extensions/ExtensionControlPlaneService.cs`
- `allstarr/Core/Extensions/ExtensionRuntimeCoordinator.cs`
- `allstarr/Core/Extensions/SpotiFlacExtensionCompatibility.cs`
- `allstarr/Core/Capabilities/ProviderRegistry.cs`
- `allstarr/Controllers/ExtensionController.cs`
