# Spotify lyrics sidecar

Spotify lyrics are optional. The normal and AIO installs stay healthy without this service. Add it only when you
have a Spotify web session cookie and want Spotify to participate in the lyrics route.

The sidecar is pinned by digest, stays on Allstarr's private Docker network, and does not publish a host port. Its
upstream image currently runs on `linux/amd64`; ARM hosts need Docker's emulation support.

## Add it

Keep the cookie in the stack's host `.env`:

```dotenv
SPOTIFY_API_SESSION_COOKIE=replace-with-your-sp-dc-cookie
SPOTIFY_LYRICS_API_URL=http://spotify-lyrics:8080
```

Then save and start the optional profile with the normal deployment helper:

```bash
./allstarr.sh enable spotify-lyrics
./allstarr.sh up
./allstarr.sh status
```

The helper remembers the profile, validates the merged Compose configuration, and reuses it during later updates.

The `spotify-lyrics` service includes a lightweight local HTTP health check. An unhealthy sidecar removes only
that lyrics source; it does not prevent Allstarr, Postgres, Valkey, or other providers from starting.

This does not replace Postgres, Valkey, application state, downloads, or kept media. Compose may recreate the
Allstarr container to attach the endpoint setting, but it reuses the same volumes.

If a legacy `.env` import contains `SPOTIFY_LYRICS_API_URL`, Allstarr imports that URL as a durable runtime setting.
The WebUI never starts containers or copies the Spotify cookie into a sidecar. The administrator must still add the
overlay so Docker creates the service and supplies its cookie.

## Update or remove it

Normal updates use the saved profile:

```bash
./allstarr.sh update
```

To remove only the optional service:

```bash
./allstarr.sh disable spotify-lyrics
./allstarr.sh up
```

Remove or clear the Spotify lyrics URL in Sources if you do not want Allstarr to probe the absent endpoint. Removing
the sidecar never removes music or durable Allstarr data.
