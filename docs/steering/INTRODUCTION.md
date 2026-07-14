# Allstarr - Introduction

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## What Allstarr Is

Allstarr is an ASP.NET Core `net10.0` music proxy. It sits between clients and a media backend, then expands the library with external providers and admin tooling.

- Proxy port `8080`: Jellyfin-compatible or Subsonic-compatible client traffic.
- Admin port `5275`: local Web UI and admin API.
- Backends: Jellyfin or Subsonic/Navidrome, selected at startup.
- Providers: SquidWTF, Deezer, and Qobuz.
- Cross-cutting features: Spotify playlist injection, lyrics, scrobbling, caching, downloads, admin diagnostics.

## What Matters Most In This Repo

- Client compatibility comes first. The proxy must behave like the backend the client expects.
- The admin surface is intentionally separate from the proxy surface.
- External content is represented with stable typed IDs and then mapped back into backend-specific response shapes.
- Postgres or SQLite owns durable control-plane state. Valkey, `/app/cache`, and `.env` remain important runtime
  and compatibility inputs, but they are not the authoritative account, job, outbox, health, or backup store.
- The codebase already has a large regression suite. New behavior should land with tests.

## Repository Shape

- `allstarr/Controllers`: Jellyfin proxy, Subsonic proxy, and admin APIs.
- `allstarr/Services`: backend integrations, provider clients, downloads, Spotify, lyrics, scrobbling, validation, shared helpers.
- `allstarr/Middleware` and `allstarr/Filters`: proxy and admin request boundaries.
- `allstarr/Models`: shared domain models, settings, Spotify models, scrobbling models, admin DTOs.
- `allstarr/wwwroot`: static admin UI.
- `allstarr.Tests`: xUnit regression suite.
- `docs/steering`: subsystem-specific editing guidance for future work.

## Start Here For Changes

- Runtime wiring or config changes: [STARTUP-AND-CONFIG.md](STARTUP-AND-CONFIG.md)
- Proxy behavior or backend compatibility: [BACKENDS.md](BACKENDS.md)
- Admin auth, port isolation, allowlists, or admin-only routes: [ADMIN-SECURITY.md](ADMIN-SECURITY.md) and [AUTHENTICATION.md](AUTHENTICATION.md)
- Provider or download behavior: [PROVIDERS.md](PROVIDERS.md) and [DOWNLOADS.md](DOWNLOADS.md)
- Spotify syncing and mapping: [SPOTIFY.md](SPOTIFY.md)
- Lyrics: [LYRICS.md](LYRICS.md)
- Scrobbling: [SCROBBLING.md](SCROBBLING.md)
- Cache or persistence behavior: [CACHING.md](CACHING.md)
- Shared helpers and conventions: [UTILITIES.md](UTILITIES.md)
- Admin frontend changes: [ARCHITECTURE.md](ARCHITECTURE.md) and [TESTING.md](TESTING.md)
- Test expectations: [TESTING.md](TESTING.md)

## Editing Guardrails

- Keep proxy authentication transparent for client traffic.
- Keep admin endpoints under `/api/admin` so the security middleware stack still applies.
- Use shared helper classes before inventing new one-off logic.
- Update steering when the code makes a steering doc stale.
