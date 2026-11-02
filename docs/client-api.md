# Client API

The client API adds Allstarr features alongside the normal Jellyfin API.
Jellyfin-compatible apps can use both APIs with the listener's Jellyfin token.

It is off by default. An administrator can enable **Client API for apps** in
the dashboard's server settings under **Defaults for everyone**. Changes apply
immediately. While off, all `/allstarr` routes return 404, title marker headers
are ignored, and the extra CORS header support is disabled.

Use `/allstarr/v1` on the music listener, port 8080. These routes are unavailable
on the dashboard listener and on servers using a Subsonic backend.

Send the same credentials used for Jellyfin, for example an `X-Emby-Token`
header or a MediaBrowser authorization header. Tokens must belong to a user.
API keys and tokens without an Allstarr user are forbidden. Administrators
receive the same personal scope as other listeners.

`GET /allstarr/v1/capabilities` returns `serverVersion`, `apiVersion`, and a
`features` object. Check feature flags before using optional routes.
`titleMarkers` is available; the other flags remain false until supported.
The version in the path identifies the contract. Compatible additions stay
within v1; incompatible changes use a new path version.

JSON uses camelCase. Every error has this shape:

```json
{"code":"unauthorized","error":"A valid Jellyfin user token is required."}
```

Codes are `unauthorized`, `forbidden`, `not_found`, `invalid_request`, `conflict`,
and `unavailable`. Unknown `/allstarr` paths return `not_found`.

Send `X-Allstarr-Title-Markers: none` on any music request to omit both `[A]`
and `[E]` title markers. This also works on normal Jellyfin routes. It changes
only that request; saved listening preferences stay the same. Other header
values preserve the normal behavior.

Browser clients may send `X-Allstarr-*` headers. Responses expose
`X-Allstarr-Playback-Request` through CORS.

When enabled, the OpenAPI document is public at `/allstarr/v1/openapi.json`.
Its checked-in copy is `apis/specifications/allstarr/client-v1.json`.
The contract test compares normalized JSON. Set `ALLSTARR_UPDATE_CLIENT_SPEC=1`
when running that test to refresh the copy after an intentional change.
