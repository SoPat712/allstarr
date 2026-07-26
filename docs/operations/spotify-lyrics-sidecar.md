# Spotify lyrics service

Spotify lyrics is an optional upstream service used only for lyric lookup. It is not Spotify playlist authentication, account discovery, metadata ownership, or playback routing.

## Enable

Place the required Spotify session cookie in the protected deployment environment, then enable the native profile:

```bash
./allstarr.sh enable spotify-lyrics
./allstarr.sh up
```

The checked-in Compose file points at a pinned image from `akashrchandran/spotify-lyrics-api`. Allstarr does not vendor that project's source or maintain a forked image.

## Verify

```bash
./allstarr.sh status
./allstarr.sh logs spotify-lyrics
```

Provider readiness and lyrics routing are visible in the Sources and Settings surfaces. An unhealthy lyrics service degrades only the capability that depends on it; it must not make playlist discovery or the core proxy unavailable.

## Update or disable

```bash
./allstarr.sh update
./allstarr.sh disable spotify-lyrics
./allstarr.sh up
```

Disabling the profile removes the running optional container while preserving Allstarr's durable settings. Re-enable it explicitly when a valid session is available.

## Security

- Keep the session cookie out of Compose YAML, logs, cache keys, and support exports.
- Do not expose the lyrics service port to the host; Allstarr reaches it on the private Compose network.
- Do not mount the Docker socket or unrelated state into the service.
- Treat upstream image updates as dependency changes: review and pin them before release.
