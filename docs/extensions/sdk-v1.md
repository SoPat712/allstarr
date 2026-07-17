# Extension SDK v1

SDK v1 is for provider integrations. It does not grant general plugin access to Allstarr, the host filesystem, environment variables, processes, or arbitrary network destinations.

An extension package is a ZIP containing `manifest.json` and `index.js` at its root. It may also contain `README.md`, a license file, and static files below `assets/`. Install scripts, native binaries, nested code directories, and parent paths are rejected.

## Package identity

Use a stable lowercase kebab-case ID. The package ID is also its provider ID, so it must not collide with a built-in provider. Versions use semantic version text and `sdkVersion` is `1`.

```json
{
  "id": "example-catalog",
  "displayName": "Example Catalog",
  "version": "1.0.0",
  "sdkVersion": "1",
  "entryPoint": "index.js",
  "capabilities": [
    {
      "kind": "metadata",
      "hooks": ["searchTracks", "getTrack"],
      "accountScopes": ["user"]
    }
  ],
  "permissions": [
    {
      "kind": "network",
      "value": "https://api.example.com/",
      "required": true
    },
    {
      "kind": "secret",
      "value": "accountToken",
      "required": true
    },
    {
      "kind": "cache",
      "value": "metadataCache",
      "required": false
    }
  ]
}
```

Declare only hooks the package implements. SDK v1 recognizes metadata, streaming, download, playlist, lyrics, and health capabilities. Account scopes are explicit for every capability. Allstarr selects the account before invoking the extension; the extension cannot choose another user or a shared account on its own.

## Permissions

Network permissions are exact HTTPS origins with no path, credentials, query, or fragment. Redirects are checked against the same approved origins. Cache and secret permissions use lower camel-case keys.

Every permission receives an administrator decision before activation. Denying a required permission fails the staged version. Denying an optional permission leaves that bridge operation unavailable. A new package version receives its own review.

`secrets.get(key)` returns an opaque marker, not the credential. When that marker is used in an approved HTTP header or body, the host substitutes the selected account value immediately before sending the request. `utils.hmacSHA1Secret(key, data)` performs an HMAC without returning its key. Do not store markers in extension state or write credential material to logs. The host redacts common credential forms, but that is a last line of defense rather than permission to log them.

The cache is private runtime state, not package storage. Individual values, total keys, and total size are bounded. It is not a durable database and must not contain audio or other media.

## Runtime API

Register one provider object from `index.js`:

```javascript
registerExtension({
  searchTracks(query, page) {
    const token = secrets.get("accountToken");
    const response = http.get(
      `https://api.example.com/search?q=${encodeURIComponent(query)}&limit=${page.limit}`,
      { Authorization: `Bearer ${token}` }
    );

    if (response.statusCode !== 200) return { songs: [] };
    const payload = JSON.parse(response.body);
    return {
      songs: payload.items.map(item => ({
        id: item.id,
        name: item.title,
        artists: item.artists,
        album: item.album,
        duration_ms: item.durationMs,
        isrc: item.isrc
      }))
    };
  }
});
```

Available bridge objects are `http`, `storage`, `secrets`, `log`, `artifacts`, and a small `utils` helper. There is no ambient filesystem or process API. Execution has recursion, statement, memory, response-size, time, and log limits. Treat failures as normal provider failures and return a bounded structured result.

A `download` hook cannot write a host path or claim an artifact it created elsewhere. It calls
`artifacts.download(approvedHttpsUrl, artifactId, headers)` during its download invocation. The host applies secret
markers only to that approved request, streams the response into the exact job workspace, enforces the configured
size and path limits, and returns the host-derived `artifactId`, `sha256`, `sizeBytes`, and `verified` facts. Return
those exact facts with the typed media description. A second write, a foreign workspace, a traversal ID, an
oversized response, or a claim that differs from the broker result fails the operation.

The canonical hook names and result responsibilities are maintained in [providers-and-extensions.md](../steering/references/providers-and-extensions.md).

## Publishing and installing

A registry entry must include the package URL and its lowercase SHA-256 checksum. Allstarr downloads into a contained staging directory, checks archive and expanded limits, verifies the checksum, validates the layout and manifest, then records a content hash for later tamper checks. Activation recomputes that content hash.

Adding a registry does not install anything. Allstarr ships with no third-party registry configured. An administrator must add the HTTPS registry, stage a version, review every permission, and activate it. Updates follow the same staged flow. The prior version remains available for explicit rollback.

Disable an active version before uninstalling it. Uninstall removes that verified package content from routing and disk but retains provider accounts and encrypted secret records. Account removal is a separate tenant-aware action, so removing a package can never silently erase user credentials or settings. A version still serving as another package's rollback target cannot be uninstalled.

Direct URL staging is an administrator-only development path and still requires a checksum. It should not be presented as a signed or trusted registry release.

## Testing an extension

Test at least these cases before publishing:

- valid searches and individual lookups;
- missing or disabled accounts;
- denied optional and required permissions;
- unapproved origins and redirect attempts;
- provider timeouts, rate limits, malformed JSON, and oversized responses;
- successful brokered download, idempotent retry, artifact traversal, foreign workspace, and false artifact claims;
- cancellation and repeated concurrent calls;
- package traversal, unexpected files, checksum mismatch, and post-stage modification;
- update, disable, restart restore, and rollback behavior.

Automated tests must use fake providers and local fixtures. Do not put live provider credentials in a package, fixture, registry, or test log.
