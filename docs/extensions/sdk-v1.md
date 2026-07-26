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

SDK v1 recognizes typed metadata, streaming, download, playlist, lyrics, and health hooks. Declare only hooks the package implements.

- Metadata returns normalized song, album, artist, identifiers, and artwork facts.
- Streaming returns an expiring playable response for the requested external track.
- Download writes through the managed download lifecycle and reports media facts.
- Playlist lists user-visible playlists and ordered tracks for the selected account.
- Lyrics returns normalized timed or plain lyrics.
- Health tests one account and capability without changing unrelated state.

Allstarr selects the tenant, user, library, provider account, capability, deadline, and policy before invocation. Extension code cannot select a different account or impersonate another user.

## Accounts and settings

Extension credentials live in provider accounts under **Settings > Accounts**. Account fields declared as secrets are encrypted before persistence. Source health, routing, and capability readiness remain under **Sources**; they are not duplicate credential stores.

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
