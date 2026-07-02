# Configuration Guide

This document provides detailed configuration options for Allstarr.

## Advanced Configuration

### Admin Dashboard Network Access

The media-client proxy and the admin dashboard use different ports:

| Port | Purpose |
|------|---------|
| `8080` (host port `5274` in the provided Compose file) | Jellyfin/Subsonic proxy traffic |
| `5275` | Allstarr admin dashboard and admin API |

Opening port `8080` in a browser does not open the Allstarr dashboard. For
Docker, LAN, or reverse-proxy access to the dashboard, configure:

```dotenv
ADMIN_BIND_ANY_IP=true
ADMIN_TRUSTED_SUBNETS=192.168.1.0/24
```

`ADMIN_TRUSTED_SUBNETS` is a comma-separated CIDR allowlist. If a reverse proxy
connects over a Docker network, allow that Docker network's CIDR and route the
dashboard service to container port `5275`, not `8080`.

Do not publish the dashboard without an additional private-network or
authenticated-access boundary.

### Backend Selection

| Setting | Description |
|---------|-------------|
| `Backend:Type` | Backend type: `Subsonic` or `Jellyfin` (default: `Subsonic`) |

### Jellyfin Settings

| Setting | Description |
|---------|-------------|
| `Jellyfin:Url` | URL of your Jellyfin server |
| `Jellyfin:ApiKey` | API key (get from Jellyfin Dashboard > API Keys) |
| `Jellyfin:UserId` | User ID for library access |
| `Jellyfin:LibraryId` | Music library ID (optional, auto-detected) |
| `Jellyfin:MusicService` | Music provider: `SquidWTF`, `Deezer`, or `Qobuz` |

### Subsonic Settings

| Setting | Description |
|---------|-------------|
| `Subsonic:Url` | URL of your Navidrome/Subsonic server |
| `Subsonic:MusicService` | Music provider: `SquidWTF`, `Deezer`, or `Qobuz` (default: `SquidWTF`) |

### Shared Settings

| Setting | Description |
|---------|-------------|
| `Library:DownloadPath` | Directory where downloaded songs are stored |
| `*:ExplicitFilter` | Content filter: `All`, `ExplicitOnly`, or `CleanOnly` |
| `*:DownloadMode` | Download mode: `Track` or `Album` |
| `*:StorageMode` | Storage mode: `Permanent` or `Cache` |
| `*:CacheDurationHours` | Cache expiration time in hours |
| `*:EnableExternalPlaylists` | Enable external playlist support |

### SquidWTF Settings

| Setting | Description |
|---------|-------------|
| `SquidWTF:Quality` | Preferred audio quality: `FLAC`, `MP3_320`, `MP3_128`. If not specified, the highest available quality for your account will be used |

**Load Balancing & Reliability:**

SquidWTF uses a round-robin load balancing strategy across multiple backup API endpoints to distribute requests evenly and prevent overwhelming any single provider. Each request automatically rotates to the next endpoint in the pool, with automatic fallback to other endpoints if one fails. This ensures high availability and prevents rate limiting by distributing load across multiple providers.

### Deezer Settings

| Setting | Description |
|---------|-------------|
| `Deezer:Arl` | Your Deezer ARL token (required if using Deezer) |
| `Deezer:ArlFallback` | Backup ARL token if primary fails |
| `Deezer:Quality` | Preferred audio quality: `FLAC`, `MP3_320`, `MP3_128`. If not specified, the highest available quality for your account will be used |

### Qobuz Settings

| Setting | Description |
|---------|-------------|
| `Qobuz:UserAuthToken` | Your Qobuz User Auth Token (required if using Qobuz) - [How to get it](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)) |
| `Qobuz:UserId` | Your Qobuz User ID (required if using Qobuz) |
| `Qobuz:Quality` | Preferred audio quality: `FLAC`, `FLAC_24_HIGH`, `FLAC_24_LOW`, `FLAC_16`, `MP3_320`. If not specified, the highest available quality will be used |

### External Playlists

Allstarr supports discovering and downloading playlists from your streaming providers (SquidWTF, Deezer, and Qobuz).

| Setting | Description |
|---------|-------------|
| `Subsonic:EnableExternalPlaylists` | Enable/disable external playlist support (default: `true`) |
| `Subsonic:PlaylistsDirectory` | Directory name where M3U playlist files are created (default: `playlists`) |

**How it works:**
1. Search for playlists from an external provider using the global search in your Subsonic client
2. When you "star" (favorite) a playlist, Allstarr automatically downloads all tracks
3. An M3U playlist file is created in `{DownloadPath}/playlists/` with relative paths to downloaded tracks
4. Individual tracks are added to the M3U as they are played or downloaded

**Environment variable:**
```bash
# To disable playlists
Subsonic__EnableExternalPlaylists=false
```

> **Note**: Due to client-side filtering, playlists from streaming providers may not appear in the "Playlists" tab of some clients, but will show up in global search results.

### Spotify Playlist Injection (Jellyfin Only)

Allstarr automatically fills your Spotify playlists (like Release Radar and Discover Weekly) with tracks from your configured streaming provider (SquidWTF, Deezer, or Qobuz). This works by intercepting playlists created by the Jellyfin Spotify Import plugin and matching missing tracks with your streaming service.

<img width="1649" height="3764" alt="image" src="https://github.com/user-attachments/assets/a4d3d79c-7741-427f-8c01-ffc90f3a579b" />


#### Prerequisites

1. **Install the Jellyfin Spotify Import Plugin**
   - Navigate to Jellyfin Dashboard → Plugins → Catalog
   - Search for "Spotify Import" by Viperinius
   - Install and restart Jellyfin
   - Plugin repository: [Viperinius/jellyfin-plugin-spotify-import](https://github.com/Viperinius/jellyfin-plugin-spotify-import)

2. **Configure the Spotify Import Plugin**
   - Go to Jellyfin Dashboard → Plugins → Spotify Import
   - Connect your Spotify account
   - Select which playlists to sync (e.g., Release Radar, Discover Weekly)
   - Set a sync schedule (the plugin will create playlists in Jellyfin)

3. **Configure Allstarr**
   - Enable Spotify Import in Allstarr (see configuration below)
   - Link your Jellyfin playlists to Spotify playlists via the Web UI
   - Uses your existing `JELLYFIN_URL` and `JELLYFIN_API_KEY` settings

#### Configuration

| Setting | Description |
|---------|-------------|
| `SpotifyImport:Enabled` | Enable Spotify playlist injection (default: `false`) |
| `SpotifyImport:MatchingIntervalHours` | How often to run track matching in hours (default: 24, set to 0 for startup only) |
| `SpotifyImport:Playlists` | JSON array of playlists (managed via Web UI) |

**Environment variables example:**
```bash
# Enable the feature
SPOTIFY_IMPORT_ENABLED=true

# Matching interval (24 hours = once per day)
SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS=24

# Playlists (use Web UI to manage instead of editing manually)
SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","37i9dQZEVXcV6s7Dm7RXsU","first"],["Release Radar","37i9dQZEVXbng2vDHnfQlC","first"]]
```

#### How It Works

1. **Spotify Import Plugin Runs**
   - Plugin fetches your Spotify playlists
   - Creates/updates playlists in Jellyfin with tracks already in your library
   - Generates "missing tracks" JSON files for songs not found locally

2. **Allstarr Matches Tracks** (on startup + every 24 hours by default)
   - Reads missing tracks files from the Jellyfin plugin
   - For each missing track, searches your streaming provider (SquidWTF, Deezer, or Qobuz)
   - Uses fuzzy matching to find the best match (title + artist similarity)
   - Rate-limited to avoid overwhelming the service (150ms delay between searches)
   - Pre-builds playlist cache for instant loading

3. **You Open the Playlist in Jellyfin**
   - Allstarr intercepts the request
   - Returns a merged list: local tracks + matched streaming tracks
   - Loads instantly from cache!

4. **You Play a Track**
   - Local tracks stream from Jellyfin normally
   - Matched tracks download from streaming provider on-demand
   - Downloaded tracks are saved to your library for future use

#### Manual API Triggers

You can manually trigger operations via the admin API:

```bash
# Get API key from your .env file
API_KEY="your-api-key-here"

# Fetch missing tracks from Jellyfin plugin
curl "http://localhost:5274/spotify/sync?api_key=$API_KEY"

# Trigger track matching (searches streaming provider)
curl "http://localhost:5274/spotify/match?api_key=$API_KEY"

# Match all playlists (refresh all matches)
curl "http://localhost:5274/spotify/match-all?api_key=$API_KEY"

# Clear cache and rebuild
curl "http://localhost:5274/spotify/clear-cache?api_key=$API_KEY"

# Refresh specific playlist
curl "http://localhost:5274/spotify/refresh-playlist?playlistId=PLAYLIST_ID&api_key=$API_KEY"
```

#### Web UI Management

The easiest way to manage Spotify playlists is through the Web UI at `http://localhost:5275`:

1. **Link Playlists Tab**: Link Jellyfin playlists to Spotify playlists
2. **Active Playlists Tab**: View status, trigger matching, and manage playlists
3. **Configuration Tab**: Enable/disable Spotify Import and adjust settings

#### Troubleshooting

**Playlists are empty:**
- Check that the Spotify Import plugin is running and creating playlists
- Verify playlists are linked in the Web UI
- Check logs: `docker-compose logs -f allstarr | grep -i spotify`

**Tracks aren't matching:**
- Ensure your streaming provider is configured (`MUSIC_SERVICE`, credentials)
- Manually trigger matching via Web UI or API
- Check that the Jellyfin plugin generated missing tracks files

**Performance:**
- Matching runs in background with rate limiting (150ms between searches)
- First match may take a few minutes for large playlists
- Subsequent loads are instant (served from cache)

#### Notes

- Uses your existing `JELLYFIN_URL` and `JELLYFIN_API_KEY` settings
- Matched tracks cached for fast loading
- Missing tracks cache persists across restarts (Redis + file cache)
- Rate limiting prevents overwhelming your streaming provider
- Only works with Jellyfin backend (not Subsonic/Navidrome)

### Scrobbling (Last.fm & ListenBrainz)

Track your listening history to Last.fm and/or ListenBrainz. Allstarr automatically scrobbles tracks when you listen to at least half the track or 4 minutes (whichever comes first).

#### Configuration

| Setting | Description |
|---------|-------------|
| `Scrobbling:Enabled` | Enable scrobbling globally (default: `false`) |
| `Scrobbling:LocalTracksEnabled` | Enable scrobbling for local library tracks (default: `false`) - See note below |
| `Scrobbling:LastFm:Enabled` | Enable Last.fm scrobbling (default: `false`) |
| `Scrobbling:LastFm:ApiKey` | Your Last.fm API key (required when enabled) — [create an app](https://www.last.fm/api/account/create) |
| `Scrobbling:LastFm:SharedSecret` | Your Last.fm shared secret (required when enabled) |
| `Scrobbling:LastFm:Username` | Your Last.fm username |
| `Scrobbling:LastFm:Password` | Your Last.fm password (only used for authentication) |
| `Scrobbling:LastFm:SessionKey` | Last.fm session key (auto-generated via Web UI) |
| `Scrobbling:ListenBrainz:Enabled` | Enable ListenBrainz scrobbling (default: `false`) |
| `Scrobbling:ListenBrainz:UserToken` | Your ListenBrainz user token |

**Environment variables example:**
```bash
# Enable scrobbling globally
SCROBBLING_ENABLED=true

# Local track scrobbling (RECOMMENDED: keep disabled)
# Use native Jellyfin plugins instead:
# - Last.fm: https://github.com/danielfariati/jellyfin-plugin-lastfm
# - ListenBrainz: https://github.com/lyarenei/jellyfin-plugin-listenbrainz
SCROBBLING_LOCAL_TRACKS_ENABLED=false

# Last.fm configuration (API key + secret required — shared Jellyfin plugin key is suspended)
SCROBBLING_LASTFM_ENABLED=true
SCROBBLING_LASTFM_API_KEY=your-api-key
SCROBBLING_LASTFM_SHARED_SECRET=your-shared-secret
SCROBBLING_LASTFM_USERNAME=your-username
SCROBBLING_LASTFM_PASSWORD=your-password
# Session key is auto-generated via Web UI after you set API credentials

# ListenBrainz configuration
SCROBBLING_LISTENBRAINZ_ENABLED=true
SCROBBLING_LISTENBRAINZ_USER_TOKEN=your-token-here
```

#### Setup via Web UI (Recommended)

The easiest way to configure scrobbling is through the Web UI at `http://localhost:5275`:

**Last.fm Setup:**
1. Create a Last.fm API account at https://www.last.fm/api/account/create and note the API key and shared secret
2. Set `SCROBBLING_LASTFM_API_KEY` and `SCROBBLING_LASTFM_SHARED_SECRET` in your `.env` file
3. Navigate to the **Scrobbling** tab
4. Toggle "Last.fm Enabled" to enable
5. Click "Edit" next to Username and enter your Last.fm username
6. Click "Edit" next to Password and enter your Last.fm password
7. Click "Authenticate & Save" to generate a session key (must be done again if you change API key)
8. Restart the container for changes to take effect

**ListenBrainz Setup:**
1. Get your user token from [ListenBrainz Settings](https://listenbrainz.org/settings/)
2. Navigate to the **Scrobbling** tab in Allstarr Web UI
3. Toggle "ListenBrainz Enabled" to enable
4. Click "Validate & Save Token" and enter your token
5. Restart the container for changes to take effect

#### Important Notes

- **Local Track Scrobbling**: By default, Allstarr does NOT scrobble local library tracks. It's recommended to use native Jellyfin plugins for local track scrobbling:
  - [Last.fm Plugin](https://github.com/danielfariati/jellyfin-plugin-lastfm)
  - [ListenBrainz Plugin](https://github.com/lyarenei/jellyfin-plugin-listenbrainz)
  
  This ensures Allstarr only scrobbles external tracks (Spotify, Deezer, Qobuz) that aren't in your local library.

- **Last.fm**: Scrobbles both local library tracks (if enabled) and external tracks
- **ListenBrainz**: Only scrobbles external tracks (not local library tracks) to maintain data quality
- Tracks shorter than 30 seconds are not scrobbled (per Last.fm rules)
- "Now Playing" status is updated when you start playing a track
- Scrobbles are submitted when you reach the scrobble threshold (50% or 4 minutes)

#### Troubleshooting

**Last.fm "API Key Suspended" or "not allowed to make requests":**
- Last.fm suspended the old shared Jellyfin plugin API key that Allstarr used by default
- Create your own application at https://www.last.fm/api/account/create
- Set `SCROBBLING_LASTFM_API_KEY` and `SCROBBLING_LASTFM_SHARED_SECRET` in `.env`, restart, then authenticate again in Admin → Scrobbling

**Last.fm authentication fails:**
- Verify your username and password are correct
- Ensure API key and shared secret are set (not empty)
- Check that there are no extra spaces in your credentials
- Try re-authenticating via the Web UI

**ListenBrainz token invalid:**
- Make sure you copied the entire token from ListenBrainz settings
- Check that there are no extra spaces or newlines
- Try generating a new token by clicking "Reset token" on ListenBrainz

**Tracks not scrobbling:**
- Verify scrobbling is enabled globally (`SCROBBLING_ENABLED=true`)
- Check that the specific service is enabled (Last.fm or ListenBrainz)
- Ensure you're listening to at least 50% of the track or 4 minutes
- Check container logs: `docker-compose logs -f allstarr | grep -i scrobbl`

### Getting Credentials

#### Deezer ARL Token

See the [Wiki guide](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Deezer-Credentials-(ARL-Token)) for detailed instructions on obtaining your Deezer ARL token.

#### Qobuz Credentials

See the [Wiki guide](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)) for detailed instructions on obtaining your Qobuz User ID and User Auth Token.
