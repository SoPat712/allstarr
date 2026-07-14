# Authentication Architecture

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Two Authentication Planes

Allstarr has two separate auth stories:

1. Proxy auth for Jellyfin or Subsonic clients on port `8080`
2. Admin Web UI auth for `/api/admin/*` on port `5275`

Do not merge these models. They serve different audiences and code paths.

## Proxy Authentication: Transparent To The Backend

For ordinary relayed client traffic, Allstarr preserves the backend's authentication shape. Routes that synthesize local or provider-backed work verify the credential with the selected backend first.

- Clients authenticate against Jellyfin through `POST /Users/AuthenticateByName`.
- The proxy forwards auth headers and tokens to Jellyfin without interpreting the secret locally.
- `JellyfinAuthFilter` verifies every non-public Jellyfin controller request against backend `Users/Me`, records the stable backend principal, and resolves an existing canonical Allstarr identity when one is linked.
- `JellyfinProxyService` and `AuthHeaderHelper` forward the auth shape the client used.

Supported incoming auth shapes include:

- `X-Emby-Authorization`
- `X-Emby-Token`
- `Authorization: Bearer ...`
- `Authorization: MediaBrowser ...`
- `api_key` query parameters

### Proxy Guardrails

- Keep backend verification before any synthesized Jellyfin or Subsonic work. Do not substitute local password or token interpretation for backend authority.
- Do not rewrite client tokens unless a backend compatibility fix requires it.
- Preserve auth failures from Jellyfin so clients can re-authenticate correctly.
- `JELLYFIN_API_KEY` is for server-side admin or helper requests, not client auth.

## Backend Identity And Provider-Account Context

The proxy continues to let the selected backend authenticate its own clients. Allstarr then resolves identity when a request needs user-scoped state or a side effect.

`BackendIdentityResolver` must:

1. Accept a verified backend principal together with protocol and backend-instance IDs.
2. Map that stable backend identity to a canonical Allstarr user and applicable roles/policies.
3. Produce a `ProtocolExecutionContext` with actor, library scope, correlation ID, cancellation/deadline, and no raw client/provider secret.
4. Refuse to infer authorization from a display name, a caller-supplied route/query `userId`, or an opaque bearer/API token value.

For a request whose backend identity cannot be resolved, Allstarr may continue transparent proxying but must not access a user-owned provider account, user library mapping, favorite action, placement root, or playlist link. This prevents a compatibility path from becoming an account-boundary bypass.

Provider account selection occurs after identity and policy resolution:

- An explicit per-user account is usable only by its owner (or an authorized administrator).
- A per-library or global account is usable only when the saved policy permits that capability and scope.
- Fallbacks must be explicit, visible in the WebUI/audit event, and never select another user's account.
- Every favorite, download, placement, playlist sync, and provider call records the selected account scope and correlation ID, but never the secret value.

The admin session is a separate plane. An admin UI session may authorize management of mappings or shared accounts; it must not be substituted for a Jellyfin/Subsonic client credential or silently impersonate a proxy user.

## Jellyfin Session Bootstrap

Jellyfin clients expect session capability reporting around auth and playback.

- `JellyfinController.Authentication.cs` forwards login to Jellyfin.
- On successful auth, it posts `Sessions/Capabilities/Full` in the background.
- `JellyfinSessionManager` keeps device sessions alive and maintains proxy-owned websocket connections for active playback reporting.

Do not remove this behavior casually; it fixes real client compatibility issues.

## Admin Web UI Authentication

The admin UI is separate and local by design.

- Login route: `POST /api/admin/auth/login`
- Session check: `GET /api/admin/auth/me`
- Logout route: `POST /api/admin/auth/logout`
- Cookie name: `allstarr_admin_session`
- Session storage: encrypted file-backed `AdminAuthSessionService`, with an in-memory index and atomic persistence
- Session lifetime: 12 hours

The login flow is Jellyfin-backed:

1. User submits Jellyfin username and password to `AdminAuthController`
2. Controller forwards to Jellyfin `/Users/AuthenticateByName`
3. On success, Allstarr stores an encrypted admin session containing user ID, name, admin flag, and Jellyfin access token
4. Allstarr returns an HTTP-only session cookie

Passwords are not persisted by Allstarr.

## Admin Authorization Model

`AdminAuthenticationMiddleware` protects `/api/admin/*` on port `5275` except `/api/admin/auth/*`.

- Full admin sessions can access the whole admin API.
- Non-admin Jellyfin users are intentionally limited to a narrow playlist-linking surface.

Current non-admin allowed routes are:

- `GET /api/admin/jellyfin/playlists`
- `POST /api/admin/jellyfin/playlists/{id}/link`
- `DELETE /api/admin/jellyfin/playlists/{id}/unlink`
- `GET /api/admin/spotify/user-playlists`
- `GET /api/admin/ui/schema`
- Self-service list/create/delete/secret rotation under `/api/admin/provider-accounts`
- Read/update of the caller's favorite-action policy
- User-scoped intelligence and job routes, with controller-level ownership checks

User-scoped Spotify cookie operations must also respect the authenticated user ID. Non-admin users cannot read or write another user's Spotify cookie scope.

The same rule applies to new user-scoped account, favorite, placement, and playlist APIs: a request path may name a user for routing, but the authenticated session or resolved backend identity is the authority. Add an explicit administrator audit event for cross-user operations.

## Admin Surface Guardrails

- Keep admin endpoints under `/api/admin` so the middleware still protects them.
- Do not treat the admin session cookie as a Jellyfin client token replacement.
- Do not persist Jellyfin passwords.
- If a feature is user-scoped, use the authenticated admin session user ID when the caller is not a Jellyfin administrator.
