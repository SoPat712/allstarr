# Admin Security

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Admin Surface Model

The admin surface is intentionally separate from the proxy surface.

- Proxy traffic lives on port `8080`
- Admin UI and admin API live on port `5275`
- Admin routes should stay under `/api/admin`

If an admin feature leaves the `/api/admin` namespace or bypasses port `5275`, it will skip important security layers.

## Middleware And Filter Boundaries

Admin protection is split across middleware and filters:

- `AdminNetworkAllowlistMiddleware`: blocks non-local and non-trusted-subnet access to port `5275`
- `AdminStaticFilesMiddleware`: serves static assets only from port `5275` and enforces a web-root boundary
- `AdminAuthenticationMiddleware`: requires an authenticated admin session for `/api/admin/*` except `/api/admin/auth/*`
- `AdminPortFilter`: returns `404` when an admin controller is hit on the wrong port

Some admin endpoints also rely only on the middleware path prefix. `DownloadActivityController` is one example. That means the `/api/admin` route prefix itself is part of the security model.

## Network Isolation Rules

Default behavior is conservative:

- Native admin listeners bind to localhost unless `Admin:BindAnyIp=true`
- Containerized listeners bind to the container interface, but default Compose publishes only to host loopback and permits only its resolved gateway
- Loopback is always allowed
- Extra trusted CIDRs come from `Admin:TrustedSubnets`

Do not weaken these defaults casually. If you change bind or allowlist behavior, update tests and docs together.

## Auth And Authorization Rules

`AdminAuthenticationMiddleware` enforces:

- Valid admin UI session cookie
- Session expiration
- Per-user versus admin authorization checks

Current non-admin capabilities are intentionally narrow:

- View Jellyfin playlists
- Link or unlink playlist ownership for their own scope
- View their own Spotify user playlists

Do not add new non-admin routes without being explicit about why the capability should be delegated to regular Jellyfin users.

## Path, File, And Request Hardening

Security-sensitive helpers already exist:

- `PathHelper` for file and folder names
- `OutboundRequestGuard` for user-derived outbound URLs
- `AdminHelperService` for `.env` key and value validation
- `BotProbeDetector` and `BotProbeBlockMiddleware` for common scanner paths
- `RequestLoggingMiddleware` for optional request logging with redaction support

Prefer these helpers over ad hoc inline sanitization.

## Editing Guardrails

- Keep admin endpoints under `/api/admin`.
- Preserve the admin middleware order from `Program.cs`.
- Do not serve admin static assets on the proxy port.
- Reuse the existing validation and sanitization helpers for paths, `.env` values, and outbound URLs.
- If you add a security boundary, add a regression test for it.
