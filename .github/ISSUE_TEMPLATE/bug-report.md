---
name: Bug Report
about: Report a reproducible problem with Allstarr
title: "[BUG] Issue with ..."
labels: bug
assignees: SoPat712

---

## Describe the bug

A clear and concise description of what the bug is.

## To Reproduce

Steps to reproduce the behavior:

1. Go to '...'
2. Click on '...'
3. Scroll down to '...'
4. See error

## Deployment

- Install type: [Standard Compose / AIO Compose / local development / other]
- Allstarr image tag or commit:
- Host architecture: [amd64 / arm64]
- Backend: [Jellyfin / Navidrome / other Subsonic server]
- Fresh install: [Yes / No]

## Expected behavior

A clear and concise description of what you expected to happen.

## Additional context

Add any other context, screenshots, or surrounding details here.

## Safe diagnostics from Allstarr

- Sensitive values stay redacted in this block.
- Allstarr Version: [image tag or commit]
- Backend Type: [e.g. Jellyfin]
- Music Service: [e.g. SquidWTF]
- Storage Mode: [e.g. Cache]
- Download Mode: [e.g. Track]
- Storage: [Postgres version and readiness state]
- Valkey Enabled: [e.g. Yes]
- Spotify Import Enabled: [e.g. Yes]
- Scrobbling Enabled: [e.g. Disabled]
- Spotify Status: [e.g. Spotify Ready]
- Jellyfin URL: [Configured (redacted) or Not configured]
- Client: [e.g. Firefox 149 on macOS]
- Generated At (UTC): [e.g. 2026-04-19T02:18:52.483Z]
- Browser Time Zone: [e.g. America/New_York]

## Rendered Compose configuration (optional)

Run `docker compose config` and remove secrets before pasting it.

```yaml

```

## .env (redacted, optional)

```env

```
