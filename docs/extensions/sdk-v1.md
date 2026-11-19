# Extension SDK v1

SDK v1 lets an administrator install provider modules without giving them control of the host, database, network policy, or another user's account. Built-ins and extensions expose the same typed capability contracts and meet at `ProviderRegistry`.

## Package boundary

An extension package is a ZIP archive with `manifest.json` and `index.js` at its root. Optional root files are `README.md`, `LICENSE`, `LICENSE.md`, `COPYING`, `NOTICE`, `icon.png`, `icon.jpg`, `icon.jpeg`, `icon.webp`, `icon.svg`, `glyph.png` and `glyph.svg`; every other file belongs under `assets/`. Legal files are retained unchanged and included in the installed content hash.

An archive is at most 32 MiB, expands to at most 128 MiB and holds at most 2,000 files. Absolute paths, `.` or `..` segments and symbolic links are refused. The archive's SHA-256 must match the checksum supplied by its registry entry or by the administrator. At staging Allstarr computes a hash of the extracted contents and checks it again before every activation, so a package changed on disk does not run.

The manifest and archive are data from an untrusted publisher. Installation validates registry origin, archive and content hashes, archive bounds, paths, compatibility, manifest shape, permissions, required runtime features and reserved IDs before activation. The compatibility layer also accepts SpotiFLAC registry metadata and `.sflx` packages and converts them into this contract (see [SpotiFLAC packages](#spotiflac-packages)).

`index.js` registers its hooks by calling `registerExtension({ ... })` with an object whose functions are the hooks. Every hook the manifest declares must be a function on that object, or activation fails.

## Manifest

```json
{
  "id": "example-provider",
  "displayName": "Example provider",
  "version": "1.2.3",
  "sdkVersion": "1",
  "entryPoint": "index.js",
  "capabilities": [
    { "kind": "metadata", "hooks": ["searchTracks", "getTrack"], "accountScopes": [], "accountRequired": false },
    { "kind": "streaming", "hooks": ["getStreamLease"], "accountScopes": [], "accountRequired": false }
  ],
  "permissions": [{ "kind": "network", "value": "https://api.example.test/", "required": true }],
  "settings": []
}
```

Required fields:

| Field | Rules |
| --- | --- |
| `id` | Lowercase kebab-case, at most 128 characters. Reserved IDs are refused (see [Registries and ownership](#registries-and-ownership)). |
| `sdkVersion` | `"1"`. |
| `version` | Semantic version such as `1.2.3` or `1.2.3-beta.1`, at most 100 characters. |
| `entryPoint` | `"index.js"`. |
| `capabilities` | At least one capability; each kind at most once. |
| `permissions` | An array, even when empty. |

Optional fields:

| Field | Rules |
| --- | --- |
| `displayName` | At most 100 characters; defaults to `id`. |
| `description` | At most 500 characters, shown as the service's description. |
| `author` | At most 200 characters. |
| `icon` | Package path to a `.png`, `.jpg`, `.jpeg`, `.webp` or `.svg` file. Without it, a root `icon.svg`, `icon.png`, `icon.jpg`, `icon.jpeg` or `icon.webp` is used. Icons over 2 MiB are not served. |
| `glyphIcon` | Package path to a `.png` or `.svg` file, served as the glyph variant of the icon. |
| `settings` | Up to 64 account fields; see below. |
| `qualityOptions` | Up to 32 formats the service offers, as strings or `{id,label,description,kind}` (`value` or `name` can stand in for `id`, `displayName` for `label`). Allstarr reads each option's text to pick the best format within the listener's quality limit. |
| `requiredRuntimeFeatures` | Up to 64 runtime feature names (see [Runtime and permissions](#runtime-and-permissions)). A package that requires an unsupported feature cannot be enabled. |
| `urlPatterns` | Up to 64 link patterns, each at most 1,000 characters, for links the package can open, such as pasted playlist links. A pattern is a host with an optional path (`*.example.com`, `example.com/playlist/*`), a scheme (`example:`), a full URL with `*` wildcards, or a regular expression matched from the start of the link. Requires a `handleUrl` hook function. |
| `signedSession` | See [Signed sessions](#signed-sessions). |
| `defaultUseMode` | `automatic` (shown as **On**), `manualOnly` or `off`; defaults to `automatic`. |
| `compatibility` | Set to `spotiflac-v1` by Allstarr when it converts a SpotiFLAC package. Native packages leave it out. |

A settings entry is `{key,type?,label?,description?,required?,secret?,default?,options?}`. `key` is lower camel case and unique; `label` defaults to the key and `helpText` can stand in for `description`. `type` defaults to `text`: `boolean`, `bool`, `toggle` and `checkbox` make a switch; `integer`, `int` and `number` a number; `select`, `choice` or any `options` list a choice. A setting is stored encrypted as a secret when `secret` is true, its type is `password`, `secret` or `token`, or its key ends in `password`, `secret`, `token`, `cookie`, `apiKey` or `mediaUserToken`. Settings are fields of the source's provider accounts.

A capability is `{kind,hooks,accountScopes,accountRequired?,supportsPlaylistDiscovery?}`. `kind` is one of the kinds below, in any letter case. `hooks` is a non-empty list of unique hooks allowed for that kind. `accountScopes` accepts `personal` and `shared`. `accountRequired` defaults to true and then needs at least one scope; a package with `signedSession` always requires an account. `supportsPlaylistDiscovery` defaults to true.

A permission is `{kind,value,required?}`:

| Kind | Value | Grants |
| --- | --- | --- |
| `network` | An HTTPS origin without path or credentials, such as `https://api.example.com/`, or a subdomain wildcard such as `https://*.example.com/`. At most 32. | HTTPS requests to that origin. Requests anywhere else are refused. |
| `cache` | A lower camel case key, or `*` for any key. At most 64. | `storage.get`, `storage.set` and `storage.remove` for that key. Values are at most 256 KiB, with at most 256 keys and 4 MiB per package, kept between runs. |
| `secret` | A lower camel case key. At most 64. | An encrypted account field for the key (unless a setting already declares it). `secrets.get(key)` returns a placeholder that Allstarr replaces with the value in outgoing request headers and bodies; the script never sees the value. |

A package cannot be enabled until the administrator approves every permission marked `required: true`. Denied optional permissions are simply not granted.

## Capability contracts

SDK v1 recognizes typed metadata, streaming, download, playlist, lyrics, intelligence, and health hooks. Declare only hooks the package implements.

| Kind | Required hooks | Optional hooks |
| --- | --- | --- |
| `metadata` | At least one of the optional hooks | `searchTracks`, `getTrack`, `lookupByIsrc`, `searchAlbums`, `getAlbum`, `searchArtists`, `getArtist`, `getArtistAlbums`, `getArtistTracks` |
| `streaming` | `getStreamLease` | `probeStream` |
| `download` | `checkAvailability`, `download` | none |
| `playlist` | `getPlaylistTracks`, and `getUserPlaylists` unless `supportsPlaylistDiscovery` is false | `searchPlaylists`, `resolveArtwork` |
| `lyrics` | `fetchLyrics` | none |
| `intelligence` | `recommend` | `startAnalysis`, `getAnalysisProgress`, `getClusters`, `search`, `findPath`, `blend`, `getMap`, `disconnect` |
| `health` | At least one of the optional hooks | `probeMetadata`, `probePlaylist`, `probeStreaming`, `probeDownload`, `probeIntelligence` |

`mutatePlaylist` is reserved for built-in providers. A package that declares `download` without `streaming` plays songs through its download hook; Allstarr adds the streaming capability itself.

Search invokes only the requested resource kinds. Track results should include
their artist and album IDs; an audio-only search does not fetch album or artist
search results to fill missing relationships. Hooks remain serialized within a
package, while waiting host calls allow other providers to make progress.

- Metadata returns normalized song, album, artist, identifiers, and artwork facts.
- Streaming returns an expiring playable response for the requested external track.
- Download writes through the managed download lifecycle and reports media facts.
- Playlist lists user-visible playlists and ordered tracks for the selected account. The existing `getUserPlaylists` hook receives `limit` and `cursor`, plus optional `sourceUserId` when a caller explicitly requests a source user. Requests without that field keep their original shape.
- Lyrics returns normalized timed or plain lyrics.
- Intelligence starts and observes analysis jobs, returns clusters and recommendations, searches
  text or lyrics, finds ordered song paths, blends positive/negative song seeds, pages map points,
  and disconnects a remote service.
- Health tests one account and capability without changing unrelated state.

`getArtistAlbums` and `getArtistTracks` receive the provider artist `id`, a `{ limit, cursor }` page, and an optional `expectedSnapshotVersion`, and
return the same typed page shape as `searchAlbums` or `searchTracks`. This lets Jellyfin clients
open an extension-backed artist and browse its complete virtual discography.

Allstarr selects the initiating user, Personal or Shared provider account, capability, deadline, and effective policy before invocation. Credential access is bound to that account's current owner, revision, and purpose. Extension code cannot select a different account or impersonate another user. Backend library permissions are checked separately when resolving local items.

### Source use and matching

Every built-in and extension source has a use setting of **On**, **Manual only** or **Off**, set in the source's panel in Sources → Services. Household defaults and personal overrides use the same policy owner. The manifest's `defaultUseMode` sets the starting value; an administrator can change it.

A manual-only source still appears in listener searches, and a song a listener picks from it plays from that source. Exact selections remain user-scoped manual routes through refreshes and rematches. Automatic matching, playlist matching, identity enrichment, and playback fallback exclude it. Source-specific internal recording IDs remain available for playlists, playback and history without requiring an ISRC or MusicBrainz identifier. Turning a source off prevents its use without deleting the saved pin; only previously verified normal routes remain eligible as alternatives.

### Intelligence

Extensions may expose the intelligence capability. The features that call it are
included only in the development release profile. AudioMuse has its own built-in
connection in Settings → Mixes and discovery; see [AudioMuse](../operations/audiomuse.md).
External models, workers and indexes remain outside Allstarr.
External intelligence service implementations remain outside the Allstarr package.

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

## Accounts and settings

Extension credentials live in provider accounts under Sources → Accounts. Account fields declared as secrets are encrypted before persistence. Source status and capability readiness are shown in Sources → Services, and source order in Settings → Music sources; neither is a second credential store.

Capability `accountScopes` accepts `personal` and `shared`. Legacy `user` and `global` declarations are translated to those audiences at manifest validation. Library-scoped accounts are unsupported and rejected during validation. Built-in and extension capabilities use the same owner checks, account ordering and encrypted credential leases.

## Runtime and permissions

JavaScript packages run in the constrained Jint compatibility runtime with bounded bridges. A package receives only the APIs granted by its reviewed permissions. Network access is allowlisted and request-bounded. File access is package-scoped. Host process, Docker socket, arbitrary filesystem, and raw secret-store access are not capabilities.

The runtime supports these `requiredRuntimeFeatures`: `fetch@1`, `file@1`, `storage@1`, `signedSession@1`, `signedSession@2`, `signedSession@3`, `sessionGrant@1`, `preparedContext@1`, `patternedFileTransform@1`, `downloadSegments@1` and `deferredPreview@1`.

Permission changes require administrator review before an update can activate. Disable, uninstall, and registry removal are control-plane operations with durable state and audit events.

Activating an update uninstalls the previous active or disabled version and removes
its package files. Historical records, permission reviews and Activity remain
available for later update comparisons. Extension sign-ins and provider accounts
are retained. Startup also removes historical rollback-version files when the
same extension has a newer active or disabled version. Interrupted file cleanup
is retried at startup and reported in Activity; it never removes runtime sign-in state.

### Download compatibility

Download hooks have a five-minute cancellation budget; other hooks have twelve seconds.
`utils.getResolutionRemainingMs()` reports the remaining invocation budget.

`file.transformPatternedBlocks` supports Blowfish or AES with CBC or CTR, no padding, and
hex, base64 or UTF-8 keys and IVs. Each selected segment starts with the supplied IV.
`file.downloadSegments` downloads approved HTTPS URLs with up to eight parallel requests,
preserves segment order, and reports progress on the script thread. Common headers may be
overridden per segment. Downloads and transforms use invocation-owned temporary files,
enforce the managed artifact size limit, and publish completed output atomically.
Persistent segment checkpoints are accepted but are not retained between invocations.
Direct FLAC downloads may stream from a private host-owned capture when the script
can only download, read or delete files. The capture must contain a valid FLAC header;
the normal playback verifier checks its first audio chunk. Deleting the selected file
after playback starts fails the transfer. Only an identical, complete final artifact
can enter the cache. Scripts with writable or processing capabilities still finish first.

`ffmpeg.convert` supports audio remuxing with `codec: "copy"` using the FFmpeg included in
the container. Local installations need FFmpeg on their executable path. Input and output
remain inside the invocation workspace; raw FFmpeg commands are disabled.
MP4 and M4A output places the movie metadata before the audio, so clients can decode
the first chunk without fetching the file tail. Audio packets are copied unchanged.
The `matching` helpers expose string similarity, duration comparison and string normalization.
The `gobackend` global is `null` for native packages; converted SpotiFLAC packages get `utils` under that name.

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

For approved `api.zarz.moe` challenges, an optional Chromium verifier can complete
fresh verification when a sign-in expires or its renewal reaches the service's
lifetime cap. Enable it with `./allstarr.sh enable sessions`; see
[the session verifier](../../sidecars/session-verifier/README.md) for deployment,
limits, and recovery. Verification uses durable work scoped to the exact package
and connection revision. Sign-out, removal, or a changed connection discards its
result. Initial sign-in remains manual, and **Reconnect** is available if
automatic verification fails. Other services use the existing manual flow.

A provider-unavailable response (HTTP 503 with a retryable `PROVIDER_UNAVAILABLE`
error from the provider) is retried at most twice. Each wait follows the
response's `Retry-After` or the error's retry delay, defaults to one second, is
capped at 120 seconds and is skipped when it would outlast the invocation's
remaining time. Provider authentication errors retain the session; gateway
session-invalid or verification-required responses start verification.
Error-contract fields are returned to the script with the response.

A sign-in belongs to the extension ID and the registry the package came from; packages installed from a direct link without a catalog share one sign-in. Updates from the same registry keep it; a package with the same ID from another source starts signed out. Uninstalling the last package of that source deletes its session, and uninstalling the last package of the extension deletes all of its runtime state.

Administrators sign in from the extension's panel in Sources → Services. An active package with a signed session shows a **Sign-in** section: choose **Start verification**, open the verification page, paste the **Sign-in link or code** it shows, and choose **Complete sign-in**. The same section offers **Reconnect** after expiry and **Sign out**. Each action applies to the package version on screen; if the package changes meanwhile, the action is refused and sign-in starts again. Extension activity records each step without codes or tokens.

### SpotiFLAC packages

A SpotiFLAC manifest (a `name` and a `type` list, without `sdkVersion`) is converted when the package is staged:

- The ID becomes `spotiflac-<name>`, and `compatibility` is set to `spotiflac-v1`. The original manifest is kept under `spotiflacManifest`.
- `metadata_provider` becomes `metadata`, with the hooks `index.js` defines; `lyrics_provider` becomes `lyrics`; `download_provider` becomes `download` plus download-backed `streaming`.
- Network hosts become HTTPS origin permissions (plain HTTP and `localhost` are dropped), `storage: true` becomes a `cache` permission for `*`, and settings that look secret become `secret` permissions.
- Settings declared as `button` are source-app actions, so they are not imported as account fields or permissions. Allstarr does not run these actions; ordinary configuration fields and their defaults remain available.

For the SpotiFLAC Apple Music package, catalog metadata can work without a subscription token. Subscription lyrics require the package's `mediaUserToken`. This is separate from Allstarr's built-in Apple MusicKit playlist account and the optional Apple download gateway.

## Registries and ownership

Allstarr ships the SDK and control plane, not a bundled registry or third-party extension packages. Administrators add registries explicitly. A registry cannot be removed while installed packages still depend on it; remove those packages first.

A registry (shown in the WebUI as a catalog) is a public HTTPS JSON document of at most 4 MiB: an array of entries, or an object holding that array under `extensions`, `items`, `plugins` or `packages`.

```json
{
  "extensions": [
    {
      "id": "example-provider",
      "displayName": "Example provider",
      "version": "1.2.3",
      "description": "Searches and streams the example catalog.",
      "downloadUrl": "https://example.org/example-provider-1.2.3.zip",
      "sha256": "<64 hexadecimal characters>",
      "iconUrl": "https://example.org/icon.png",
      "homepage": "https://example.org/"
    }
  ]
}
```

Each entry needs `id` (lowercase kebab-case; `name` or `slug` is read when `id` is absent), an HTTPS `downloadUrl` (also read from `download_url`, `zipUrl`, `archiveUrl`, `packageUrl` or `url`) and a 64-character hexadecimal `sha256` (also read from `checksum` or `packageSha256`). `displayName`, `description`, `version` (default `1.0.0`), an HTTPS `iconUrl`, `homepage` and `types` are optional. Entries that fail these checks are skipped, and when enabled registries list the same ID, only the first listing is offered. Entries whose link ends in `.sflx` or `.spotiflac-ext` are SpotiFLAC packages; their IDs gain the `spotiflac-` prefix, and when such an entry has no checksum Allstarr computes one from a download of the package.

Reserved built-in provider IDs cannot be replaced by a registry package. The scrobbling service IDs `lastfm`, `listenbrainz`, `multi-scrobbler` and `koito` are reserved for built-in delivery. Package icons and descriptions come from reviewed registry/package metadata and are cached through the shared media/cache layer.

The IDs `backend` and `kept` belong to host-owned lyrics sources. They cannot be registered as providers or compatibility aliases.

## Lifecycle

1. In Sources → Services, choose **Add extension**. Pick a package from **Catalog**, or use **From a link** with a direct HTTPS download link and the publisher's SHA-256 checksum. **Manage catalogs** adds, turns off and removes registries.
2. **Download and review** downloads and verifies the package and stages it; nothing runs yet.
3. Review package identity, publisher, version, capabilities, permissions, and settings, and allow or deny each permission.
4. Enable the package.
5. Connect an account in Sources → Accounts if required.
6. Check the source's status in Sources → Services.
7. Review permission changes before an update.
8. Disable or uninstall without deleting unrelated provider account records.

By default, installation is accepted only from the server itself or the administrator's trusted networks. The administrator API is `POST /api/admin/extensions/install` with either `{id}` for a catalog entry or `{downloadUrl,sha256,registryId?}`; there is no archive upload.

Failed packages offer **Retry**. The administrator-only `POST /api/admin/extensions/packages/{id}/retry`
action requires `expectedRevision` and records the retry in extension activity.
It validates the package and required runtime features again. Missing files are
retrieved from the original enabled registry only when that version is still
available, using the original archive and content checksums. A package installed
from a direct link without a catalog cannot be restored this way; install it
again from its link. Retry retains existing permission decisions;
pending or denied required permissions return to review, and activation remains
explicit. It also retains the extension's sign-in for that source.

A disabled package with permissions offers **Review and enable**: revoke its old
grants, review access again, then enable it. A package without permissions can be
enabled directly.

## Owning code

- `allstarr/Providers/Extensions/ExtensionSdkV1.cs`: manifest and archive validation
- `allstarr/Providers/Extensions/ExtensionManager.cs`: registries, downloads and the script runtime
- `allstarr/Providers/Extensions/ExtensionControlPlaneService.cs`
- `allstarr/Providers/Extensions/ExtensionRuntimeCoordinator.cs`
- `allstarr/Providers/Extensions/ExtensionSignedSessionClient.cs`
- `allstarr/Providers/Extensions/SpotiFlacExtensionCompatibility.cs`
- `allstarr/Providers/Contracts/ProviderRegistry.cs`: allowed and required hooks
- `allstarr/Controllers/ExtensionController.cs`
