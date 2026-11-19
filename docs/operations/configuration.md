# Configuration

Allstarr keeps its settings in its database and you change them in the dashboard. A short list of deployment values is read when the container starts. Passwords, tokens and keys are stored encrypted; see [storage operations](storage.md) for the key ring and backups.

## Deployment values

`.env` is optional. `docker-compose.yml` reads it and passes these values to the container; it does not mount `.env` inside the container. `.env.example` lists the common ones.

| Variable | Default | Purpose |
| --- | --- | --- |
| `ALLSTARR_IMAGE` | the release named in `.env.example` | Application image. Set a tag or digest to choose a release. |
| `ALLSTARR_RELEASE_PROFILE` | `core` | `core` or `development`. Any other value stops startup. See [release profiles](#release-profiles). |
| `ALLSTARR_DATA_PATH` | `./data` | Host folder or named volume mounted at `/app/data`. |
| `PROXY_BIND_ADDRESS`, `PROXY_PORT` | `0.0.0.0`, `5274` | Host address and port for music apps. |
| `ADMIN_BIND_ADDRESS`, `ADMIN_PORT` | `127.0.0.1`, `5275` | Host address and port for the dashboard. |
| `ADMIN_BIND_ANY_IP` | `false` | Accept dashboard connections from other machines. Use it with `ADMIN_TRUSTED_SUBNETS`. |
| `ADMIN_TRUSTED_SUBNETS` | empty | Comma-separated networks allowed to open the dashboard, for example `192.168.1.0/24`. |
| `ADMIN_BASE_PATH` | empty | Path prefix when the dashboard sits behind a reverse proxy, for example `/allstarr`. |
| `BACKEND_TYPE`, `JELLYFIN_URL`, `SUBSONIC_URL` | `Jellyfin`, empty, empty | Optional start-up defaults for the media server. A connection saved in first-run setup takes precedence. |
| `SESSION_VERIFIER_URL` | empty | Address of the optional [session verifier](../../sidecars/session-verifier/README.md). |

`SESSION_VERIFIER_URL` is not in `.env.example`. `./allstarr.sh enable sessions` sets it to `http://session-verifier:8000` for you whenever the `sessions` profile is saved. If you run Compose by hand, put `SESSION_VERIFIER_URL=http://session-verifier:8000` in `.env` and pass `--profile sessions`. Compose hands the value to Allstarr as `Extensions__SessionVerification__BaseUrl`.

After you change a deployment value, recreate the container with `./allstarr.sh up` or `docker compose up -d`. A few of these values also appear in the dashboard as read-only fields, so you can check what is in effect.

For HTTPS, a reverse-proxy path or single sign-on, see [HTTPS, private dashboard access and SSO](webui-access.md). For install, update and rollback, see [deployment](deployment-profiles.md).

## Container overrides

Besides the values above, Allstarr reads a few settings from the container environment; all other settings live in the dashboard. Add these under `environment:` in a Compose override file, then recreate the container. The Compose file also sets a few internal values, such as `Storage__DataDirectory=/app/data`; leave those as they are.

| Variable | Default | Purpose |
| --- | --- | --- |
| `ForwardedHeaders__KnownProxies`, `ForwardedHeaders__KnownNetworks` | none | Trust forwarded client addresses and schemes from your reverse proxy. See [HTTPS and proxies](webui-access.md#keep-administration-private). |
| `Promotion__AllowedBase` | none | Optional limit on where **Add to library** may write. See [adding songs to the library](storage.md#adding-songs-to-the-media-server-library). |
| `Secrets__KeyRingPath` | `/app/data/keyring.json` | Key ring location, for an existing secret mount. It must be writable to apply a restore. |
| `Library__DownloadPath`, `Library__KeptPath` | `/app/data/downloads`, `/app/data/kept` | Put downloads and kept songs in another mounted folder. |
| `Storage__DatabaseFileName` | `allstarr.db` | Database file name inside the data folder. |

Other product settings from a version 2 `.env` are not read at startup. They come over once through the [legacy import](legacy-env-import.md).

## Where settings live

Use **Find a setting** at the top of Settings to search by name.

| Settings | Where |
| --- | --- |
| Audio quality, playback start, preferring your own copy | Settings → Sound quality |
| Source order for Search, Downloads, Streaming, Playlists and Lyrics; lyric translation; whether listeners can connect their own accounts | Settings → Music sources |
| Libraries to use, where added songs go, downloads, imported playlist matching, folders | Settings → Library |
| Client API for apps, music-client address, browser origins | Settings → Apps |
| AudioMuse, mixes in apps, similar-song fallbacks | Settings → Mixes and discovery |
| Scrobbling songs from your own library, activity history retention | Settings → History and scrobbling |
| Listeners, explicit songs for everyone, media-server access, single sign-on, dashboard network | Settings → People and sign-in |
| Email and ntfy alerts | Settings → Alerts |
| Song cache, streams per listener, conversions, search wait | Settings → Storage and speed |
| Backups, health checks, request logging, setup guide | Settings → Backups and health |
| Song links, extension installs, challenge solver | Settings → Advanced |
| Each service's own settings, extensions | Sources → Services |
| Service accounts and scrobbling accounts | Sources → Accounts |
| A listener's own choices | Settings → Listening |

Most settings apply as soon as you save them. A setting marked **Restarts Allstarr** restarts it automatically after the save. The dashboard waits and reconnects when Allstarr is back; playing music pauses briefly. Fields marked as deployment values are read-only in the dashboard; change them in `.env` or the Compose file and recreate the container.

## Music-client address

Set **Settings → Apps → Music-client address** to the address listeners enter in their music apps, for example `https://music.example.com`. Include the port and any path prefix your proxy uses. Use an absolute HTTP or HTTPS URL without credentials, a query or a fragment. Do not enter the media server's private address.

The address shows on **Connect apps** with step-by-step setup for each app. It changes the instructions only; it does not change ports, proxy routing or the media-server connection. A version 2 `PUBLIC_LISTENER_URL` comes over through the legacy import.

**Client API for apps** on the same page is off by default. Turn it on to let apps such as Songbird use Allstarr's own features (song details, synced lyrics, mixes). See the [client API](../api/README.md).

## Media server

First-run setup asks for the server type and address, then verifies a media-server administrator. The connection is saved in the database and the password is discarded. Allstarr restarts to load it. Use an address the container can reach: `localhost` inside the container is Allstarr itself, so use `http://host.docker.internal:8096` for a server on the Docker host.

Each user is created at their first verified sign-in. Their administrator role comes from the media server.

### Media-server access

For Jellyfin, playlist sync and library scans need saved access. Turn on **Allow playlist sync and library scans** in the **Media server** card in Settings → People and sign-in. It verifies a Jellyfin administrator sign-in, stores the access token encrypted for that server, and restarts Allstarr. Each listener's own library permissions still apply. Music apps keep working without it.

If an old `JELLYFIN_API_KEY` is still in the mounted `.env` or the container environment, the card and the setup guide offer **Verify and restore access**. With Subsonic, each listener grants playlist access themselves.

### Libraries to use

Allstarr indexes every music library the media server returns. Choose a subset in **Settings → Library → Libraries to use**; an empty choice means all. The change applies without a restart. Every request still checks the listener's own permissions on the media server. Those checks are cached for up to 30 seconds and refreshed at sign-in.

## Provider accounts

Service credentials are saved encrypted as **Personal** or **Shared** accounts in **Sources → Accounts**. A Personal account has one owner. A Shared account has no owner, is managed by administrators, and is available to the whole household for the capabilities its service offers.

**Listeners can connect their own accounts** (Settings → Music sources) is on by default. Turning it off stops listeners adding new Personal accounts; existing accounts keep working and can still be removed. Administrators can manage every account, but playback never uses another listener's Personal account.

When a song needs a service, Allstarr uses the listener's own enabled Personal account first, then an enabled Shared account. Accounts do not grant access to media-server libraries or to another listener's playlists. Shared use counts against that service's limits.

An administrator's **Test** on an account checks each capability's connection. For a streaming account it then plays a short real sample (at most 64 KiB within 30 seconds, or five minutes for extensions that download before they stream) and reports whether the service supports byte ranges, its own seeking, or only sequential playback. The sample needs a known song from saved metadata; until there is one, refresh playlist metadata first.

Connection status is kept in memory per account. After a restart it starts as Unknown and normal requests rebuild it. By default three failures start a 60-second cooldown; a rejected credential starts it at once, and a rate limit can ask for longer.

Extensions add services. Install, update and remove them in **Sources → Services → Add extension**. Their services and accounts then work like the built-in ones.

## Look up songs on your other services

**Look up songs on your other services** is in Settings → Advanced → Song links. It is off by default.

When it is on and your enabled services can't find a sure copy of a song by ISRC or search, Allstarr asks link services such as song.link where else the song lives. Each answer must pass the same match rules as any other match, so playback and lyrics can fall back to your other enabled services. Existing manual matches are never changed. Turning it off keeps links already found.

Two optional keys sit beside it. Both are stored encrypted.

- **MusicLink API key**: a free key from musiclink.one. It finds songs by ISRC on Apple Music, Spotify, YouTube Music and more, and is asked first.
- **song.link API key**: with a key, the official song.link API is tried first.

**Wait between song.link lookups** (under the Advanced fold) defaults to 6000 ms and accepts 1000 to 60000 ms. Link services' rate limits and `Retry-After` replies are honoured. These settings restart Allstarr when saved.

## Metadata service

MusicBrainz settings are in **Sources → Services → MusicBrainz enrichment**: the metadata source identifier, service URL, request interval (default 1000 ms, 1000 to 60000) and an optional authorized user-agent override. The public MusicBrainz service needs no account. Saving restarts Allstarr. Recording lookups run as low-priority background jobs.

You can point Allstarr at another service that speaks the read-only MusicBrainz `/ws/2` API. Give it its own source identifier so cached answers are kept apart, and ask that service's operator before you use it. For example, BrainzMash uses `brainzmash` and `https://api.brainzmash.cc/ws/2` once it has approved Allstarr's user agent. Do not use `https://lidarrapi.brainzmash.cc`; that is a different API. A 401 or 403 means access is not set up, not that a recording is missing.

Leave the user-agent override blank. Allstarr identifies itself with its own version. Use an override only with the metadata service operator's permission, and never copy another application's identity.

## Spotify compatibility

Public Spotify playlists are read with an anonymous web session. Private playlists and Liked Songs need a connected Spotify account. Playlist imports accept a playlist ID, a `spotify:playlist:` URI or an `https://open.spotify.com/playlist/…` link.

Spotify's private web API changes from time to time. **Sources → Services → Spotify** has advanced fields for the Pathfinder endpoint, the library, playlist and Liked Songs query hashes, the token endpoint and the public TOTP secrets URL. The shipped values are the defaults. A persisted-query failure points to a change on Spotify's side; check new values against a trusted current source before you change them. A failed response never publishes a partial playlist.

## Explicit songs

**Explicit songs for everyone** is in Settings → People and sign-in.

- **Show everything** (default).
- **Hide clean/edited versions** hides only clean or edited versions. Naturally clean, explicit and unrated songs stay.
- **Clean only** needs proof that a song is clean, including songs from your own library. Missing or unknown ratings are hidden. In playlists, a verified clean version replaces an explicit entry when title, artist, album, version and duration agree; otherwise the entry is skipped. Lyrics come only from your media server or kept copies.

Listeners can choose **Clean only** for themselves in Settings → Listening when the household shows everything, but they can't loosen a household **Clean only**. The same page has **Show [A] for external tracks** and **Show [E] for explicit tracks**, both on by default.

## Release profiles

`core` is the normal release. It includes For you, mixes, new releases, taste, Recap, listening-history imports and the [AudioMuse](audiomuse.md) features listed on its page.

`development` adds the older Intelligence workspace and its recommendation jobs, plus AudioMuse's sonic Instant Mix and the OpenSubsonic `getSonicSimilarTracks` and `findSonicPath` methods.

## Optional services

Turn optional containers on and off with `allstarr.sh` instead of editing Compose files:

```bash
./allstarr.sh enable apple
./allstarr.sh enable sessions
```

See [Apple downloads](apple-download-provider.md), the [session verifier](../../sidecars/session-verifier/README.md) and [deployment](deployment-profiles.md#optional-apple-downloads).
