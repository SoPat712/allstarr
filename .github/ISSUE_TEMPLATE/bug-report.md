---
name: Bug Report
about: Report a reproducible problem with Allstarr
title: "[BUG] Issue with ..."
labels: bug
assignees: SoPat712

---

Remove passwords, tokens, cookies, signed links and private addresses before posting.

## Describe the bug

A clear and concise description of what the bug is.

## To Reproduce

Steps to reproduce the behavior:

1. Go to '...'
2. Click on '...'
3. Scroll down to '...'
4. See error

## Expected behavior

A clear and concise description of what you expected to happen.

## Deployment

- Install type: [Compose release / Compose source / local development / other]
- Allstarr version: [shown at the bottom of the Settings list, or the image tag]
- Host architecture: [amd64 / arm64]
- Media server and version: [e.g. Jellyfin 10.11 / Navidrome / other Subsonic server]
- Install history: [fresh version 3 install / upgraded from version 2 / updated from an earlier version 3 image]
- Optional extras enabled: [none / apple / sessions] (`./allstarr.sh status` lists them)

## Where it happens

- Music app and version, if relevant: [e.g. Feishin 1.0 on macOS]
- Browser, if it's a dashboard problem: [e.g. Firefox 149 on macOS]
- Service involved, if any: [e.g. Deezer, Spotify, an extension]
- Account scope, if a service is involved: [Personal / Shared / not applicable]
- Activity details, if any: [the job or entry from Activity, with its time]

For a playback problem, open **Activity → Plays**, choose the play and use **Copy diagnostics**. Paste the result below after checking it.

```text

```

## Additional context

Add any other context, screenshots, or surrounding details here.

## Rendered Compose configuration (optional)

Run `docker compose config` and remove secrets before pasting it.

```yaml

```

## .env (redacted, optional)

```env

```
