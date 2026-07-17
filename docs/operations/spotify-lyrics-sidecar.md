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

Then validate and apply the optional overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml config --quiet
docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml up -d
docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml ps
```

This does not replace Postgres, Valkey, application state, downloads, or kept media. Compose may recreate the
Allstarr container to attach the endpoint setting, but it reuses the same volumes.

If a legacy `.env` import contains `SPOTIFY_LYRICS_API_URL`, Allstarr imports that URL as a durable runtime setting.
The WebUI never starts containers or copies the Spotify cookie into a sidecar. The administrator must still add the
overlay so Docker creates the service and supplies its cookie.

## Update or remove it

Normal updates use the same overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml pull
docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml up -d
```

To remove only the optional service:

```bash
docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml rm -s -f spotify-lyrics
docker compose -f docker-compose.yml up -d
```

Remove or clear the Spotify lyrics URL in Sources if you do not want Allstarr to probe the absent endpoint. Removing
the sidecar never removes music or durable Allstarr data.

