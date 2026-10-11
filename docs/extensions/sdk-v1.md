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

Search invokes only the requested resource kinds. Track results should include
their artist and album IDs; an audio-only search does not fetch album or artist
search results to fill missing relationships. Hooks remain serialized within a
package, while waiting host calls allow other providers to make progress.

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

Allstarr selects the initiating user, Personal or Shared provider account, capability, deadline, and effective policy before invocation. Credential access is bound to that account's current owner, revision, and purpose. Extension code cannot select a different account or impersonate another user. Backend library permissions are checked separately when resolving local items.

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

Permission changes require administrator review before an update can activate. Disable, uninstall, and registry removal are control-plane operations with durable state and audit events.

Activating an update uninstalls the previous active or disabled version and removes
its package files. Historical records, permission reviews and Activity remain
available for later update comparisons. Extension sign-ins and provider accounts
are retained. Startup also removes historical rollback-version files when the
same extension has a newer active or disabled version. Interrupted file cleanup
is retried at startup and reported in Activity; it never removes runtime sign-in state.

### Download compatibility

The runtime supports `preparedContext@1`, `patternedFileTransform@1` and `downloadSegments@1`.
Download hooks have a five-minute cancellation budget; other hooks have twelve seconds.
`utils.getResolutionRemainingMs()` reports the remaining invocation budget.

`file.transformPatternedBlocks` supports Blowfish or AES with CBC or CTR, no padding, and
hex, base64 or UTF-8 keys and IVs. Each selected segment starts with the supplied IV.
`file.downloadSegments` downloads approved HTTPS URLs with up to eight parallel requests,
preserves segment order, and reports progress on the script thread. Common headers may be
overridden per segment. Downloads and transforms use invocation-owned temporary files,
enforce the managed artifact size limit, and publish completed output atomically.
Persistent segment checkpoints are accepted but are not retained between invocations.

`ffmpeg.convert` supports audio remuxing with `codec: "copy"` using the FFmpeg included in
the container. Local installations need FFmpeg on their executable path. Input and output
remain inside the invocation workspace; raw FFmpeg commands are disabled.
The `matching` helpers expose string similarity, duration comparison and string normalization.
The legacy `gobackend` global is `null`.

### Signed sessions

A package whose service needs a verified installation declares `signedSession` in its manifest: a `namespace`, an HTTPS `baseUrl`, and optionally `appVersion`, `platform`, `callbackUrl`, `schemeLabel`, `headerPrefix`, `timeWindowSeconds` (30–3600, default 300) and `endpoints` (`bootstrap`, `challenge`, `exchange`, and an optional `refresh`). The base URL's origin must also be an approved network permission.

The host performs bootstrap, challenge, grant exchange and request signing. Session secrets are encrypted on disk and never reach the script, the administrator API or logs; scripts see only session status and the responses to signed requests. Session operations for a package run one at a time, and each signed request has a 30-second limit.

Active packages with a refresh endpoint renew signed-in sessions automatically. Every
15 minutes the host checks for sessions with at most one hour remaining and renews
them through the same gate used by signed requests. It never starts verification
from this background check. Expired, signed-out, disabled and uninstalled packages
are skipped. Failed renewal retains the existing session, records an extension
Activity entry and retries on the next tick while the session remains valid.
The Sign-in section shows automatic renewal, the current expiry and the last
renewal failure observed by this runtime. The upstream service controls the new
expiry and may limit how long a session can keep renewing.

The host supports `signedSession@1`, `signedSession@2` and `signedSession@3`. Explicit
provider-unavailable responses may retry twice with cancellable delays capped at ten seconds.
Provider authentication errors retain the session; gateway session-invalid or verification-required
responses start verification. Error-contract fields are returned to the script with the response.

A sign-in belongs to the extension ID and the registry the package came from (or local upload). Updates from the same registry keep it; a package with the same ID from another source starts signed out. Uninstalling the last package of that source deletes its session, and uninstalling the last package of the extension deletes all of its runtime state.

Administrators sign in from **Integrations → Extensions**. An active package with a signed session shows a **Sign-in** section: start verification, open the verification page, paste the link or code it shows, and complete sign-in. The same section offers **Reconnect** after expiry and **Sign out**. Each action applies to the package version on screen; if the package changes meanwhile, the action is refused and sign-in starts again. Extension activity records each step without codes or tokens.

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
8. Disable or uninstall without deleting unrelated provider account records.

Failed packages offer **Retry**. The administrator-only `POST packages/{id}/retry`
action requires `expectedRevision` and records the retry in extension activity.
It validates the package and required runtime features again. Missing files are
retrieved from the original enabled registry only when that version is still
available, using the original archive and content checksums. A local package with
missing files must be uploaded again. Retry retains existing permission decisions;
pending or denied required permissions return to review, and activation remains
explicit. It also retains the extension's sign-in for that source.

A disabled package with permissions offers **Review and enable**: revoke its old
grants, review access again, then enable it. A package without permissions can be
enabled directly.

## Owning code

- `allstarr/Providers/Extensions/ExtensionSdkV1.cs`
- `allstarr/Providers/Extensions/ExtensionControlPlaneService.cs`
- `allstarr/Providers/Extensions/ExtensionRuntimeCoordinator.cs`
- `allstarr/Providers/Extensions/SpotiFlacExtensionCompatibility.cs`
- `allstarr/Providers/Contracts/ProviderRegistry.cs`
- `allstarr/Controllers/ExtensionController.cs`

### Account audiences

Capability `accountScopes` accepts `personal` and `shared`. Legacy `user` and `global` declarations are translated to those audiences at manifest validation. Library-scoped accounts are unsupported and rejected during validation. Built-in and extension capabilities use the same owner checks, account ordering and encrypted credential leases.
