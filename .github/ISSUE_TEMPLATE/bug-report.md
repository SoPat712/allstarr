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
- Install history: [fresh version 3 install / imported reviewed 2.x settings / upgraded from version 3 tag]

## Expected behavior

A clear and concise description of what you expected to happen.

## Additional context

Add any other context, screenshots, or surrounding details here.

## Safe diagnostics from Allstarr

- Sensitive values stay redacted in this block.
- Allstarr Version: [image tag or commit]
- Backend Type: [e.g. Jellyfin]
- Capability and provider involved: [e.g. playlist / Spotify]
- Provider account scope: [global / user / library / not applicable]
- Provider readiness: [Ready / Needs Config / Degraded / Unknown]
- Storage Mode: [e.g. Cache]
- Download Mode: [e.g. Track]
- Storage: [Postgres version and readiness state]
- Valkey Enabled: [e.g. Yes]
- Relevant job or correlation ID:
- Playlist mode, if relevant: [virtual / materialized / hybrid]
- Scrobbling or intelligence enabled: [e.g. Disabled]
- Backend URL: [Configured (redacted) or Not configured]
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
