---
name: allstarr-steering
version: 1.0.0
description: |
  Steering index for the Allstarr codebase. Use it when changing proxy routing,
  admin security, provider integrations, Spotify syncing, lyrics, caching,
  downloads, or background services.
allowed-tools:
  - Read
  - Write
  - Edit
  - Grep
  - Glob
---

# Allstarr Steering

Read [INTRODUCTION.md](INTRODUCTION.md) and [ARCHITECTURE.md](ARCHITECTURE.md) first. After that, load only the subsystem docs that match the files you are touching.

## Core Rules

- Do not add proxy-side authentication to Jellyfin or Subsonic client routes. Those routes stay transparent to the backend.
- Keep the admin surface isolated to port `5275` and preserve the admin allowlist and authentication middleware stack.
- Preserve client compatibility first. When proxying, prefer forwarding the exact path, query string, headers, and status code unless there is an explicit compatibility or security fix.
- Reuse shared helpers for cache keys, auth headers, paths, retries, explicit filtering, outbound URL validation, and playlist IDs instead of re-implementing logic inline.
- Treat `.env`, `/app/cache`, Valkey, and playlist or mapping files as current compatibility state. Use the
  existing services and controllers that own those flows, but do not treat cache files or Valkey as the durable
  owner of accounts, jobs, outbox work, health, or backup records.
- Add or update tests for behavior changes, especially in proxy routing, admin security, path safety, mappings, caching, JavaScript module boundaries, and playback or scrobbling flows.

## Steering Map

- `Program.cs`, DI, middleware order, env parsing, hosted services: [STARTUP-AND-CONFIG.md](STARTUP-AND-CONFIG.md)
- `Controllers/JellyfinController*.cs`, `Controllers/SubSonicController.cs`, backend proxy services: [BACKENDS.md](BACKENDS.md)
- `Controllers/AdminAuthController.cs`, admin middleware, filters, admin-only routing: [ADMIN-SECURITY.md](ADMIN-SECURITY.md) and [AUTHENTICATION.md](AUTHENTICATION.md)
- `Services/Spotify/*`, playlist admin controllers, Spotify cookie handling and matching: [SPOTIFY.md](SPOTIFY.md)
- `Services/Lyrics/*`, lyrics endpoints and fallback chain: [LYRICS.md](LYRICS.md) and [LYRICS-ARCHITECTURE.md](LYRICS-ARCHITECTURE.md)
- `Services/Scrobbling/*`, playback-to-scrobble flow, scrobbling admin routes: [SCROBBLING.md](SCROBBLING.md)
- `Services/*/{Deezer,Qobuz,SquidWTF}*`, `IMusicMetadataService`, `IDownloadService`: [PROVIDERS.md](PROVIDERS.md) and [DOWNLOADS.md](DOWNLOADS.md)
- `Services/Common/RedisCacheService.cs`, `Cache*`: [CACHING.md](CACHING.md)
- `Services/Common/*`, `Services/Admin/*`: [UTILITIES.md](UTILITIES.md)
- `wwwroot/js/*`, `wwwroot/index.html`, JS architecture tests: [ARCHITECTURE.md](ARCHITECTURE.md) and [TESTING.md](TESTING.md)
- `allstarr.Tests/*`: [TESTING.md](TESTING.md)
- Repo-wide context, directory ownership, background services, and data model conventions: [INTRODUCTION.md](INTRODUCTION.md) and [ARCHITECTURE.md](ARCHITECTURE.md)

## Release Docs

Use [GIT.md](GIT.md) and [BRANCHING.md](BRANCHING.md) only when the task is about git workflow, release flow, or branch policy.

## Conflict Resolution

- Code wins over steering.
- When code changes materially, update the relevant steering file in the same change.
- Prefer updating the narrowest relevant doc instead of repeating the same rule in every file.
