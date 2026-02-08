# Allstarr

[![Build Status - Main](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml/badge.svg?branch=main)](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml)
[![Build Status - Beta](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml/badge.svg?branch=beta)](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

A media server proxy that integrates music streaming providers with your local library. Works with **Jellyfin** and **Subsonic-compatible** servers (Navidrome). When a song isn't in your local library, it gets fetched from your configured provider, downloaded, and served to your client. The downloaded song then lives in your library for next time.

**THIS IS UNDER ACTIVE DEVELOPMENT**

Please report all bugs as soon as possible, as the Jellyfin addition is entirely a test at this point

## Quick Start

Using Docker (recommended):

```bash
# 1. Download the docker-compose.yml file and the .env.example file to a folder on the machine you have Docker

curl -O https://raw.githubusercontent.com/SoPat712/allstarr/refs/heads/main/docker-compose.yml \
     -O https://raw.githubusercontent.com/SoPat712/allstarr/refs/heads/main/.env.example

# 2. Configure environment
cp .env.example .env
vi .env  # Edit with your settings

# 3. Pull the latest image
docker-compose pull

# 3. Start services
docker-compose up -d

# 4. Check status
docker-compose ps
docker-compose logs -f
```

The proxy will be available at `http://localhost:5274`.

## Web Dashboard

Allstarr includes a web-based dashboard for easy configuration and playlist management, accessible at `http://localhost:5275` (internal port, not exposed through reverse proxy).

### Features

- **Real-time Status**: Monitor Spotify authentication, cookie age, and playlist sync status
- **Playlist Management**: Link Jellyfin playlists to Spotify playlists with a few clicks
- **Configuration Editor**: Update settings without manually editing .env files
- **Track Viewer**: Browse tracks in your configured playlists
- **Cache Management**: Clear cached data and restart the container

### Quick Setup with Web UI

1. **Access the dashboard** at `http://localhost:5275`
2. **Configure Spotify** (Configuration tab):
   - Enable Spotify API
   - Add your `sp_dc` cookie from Spotify (see instructions in UI)
   - The cookie age is automatically tracked
3. **Link playlists** (Link Playlists tab):
   - View all your Jellyfin playlists
   - Click "Link to Spotify" on any playlist
   - Paste the Spotify playlist ID, URL, or `spotify:playlist:` URI
   - Accepts formats like:
     - `37i9dQZF1DXcBWIGoYBM5M` (just the ID)
     - `spotify:playlist:37i9dQZF1DXcBWIGoYBM5M` (Spotify URI)
     - `https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M` (full URL)
4. **Restart** to apply changes (button in Configuration tab)

### Why Two Playlist Tabs?

- **Link Playlists**: Shows all Jellyfin playlists and lets you connect them to Spotify
- **Active Playlists**: Shows which Spotify playlists are currently being monitored and filled with tracks

### Configuration Persistence

The web UI updates your `.env` file directly. Changes persist across container restarts, but require a restart to take effect. In development mode, the `.env` file is in your project root. In Docker, it's at `/app/.env`.

**Recommended workflow**: Use the `sp_dc` cookie method (simpler and more reliable than the Jellyfin Spotify Import plugin).

### Nginx Proxy Setup (Required)

This service only exposes ports internally. You can use nginx to proxy to it, however PLEASE take significant precautions before exposing this! Everyone decides their own level of risk, but this is currently untested, potentially dangerous software, with almost unfettered access to your Jellyfin server. My recommendation is use Tailscale or something similar!

```nginx
server {
    listen 443 ssl http2;
    server_name your-domain.com;
    
    ssl_certificate /etc/letsencrypt/live/your-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-domain.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    
    # Security headers
    add_header Strict-Transport-Security "max-age=31536000" always;
    add_header X-Content-Type-Options "nosniff" always;
    
    # Streaming settings
    proxy_buffering off;
    proxy_request_buffering off;
    proxy_read_timeout 600s;
    
    location / {
        proxy_pass http://allstarr:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

**Security:** Don't trust me or my code, or anyone for that matter (Zero-trust, get it?), use Tailscale or Pangolin or Cloudflare Zero-Trust or anything like it please

## Why "Allstarr"?

This project brings together all the music streaming providers into one unified library - making them all stars in your collection.

## Features

- **Dual Backend Support**: Works with Jellyfin and Subsonic-compatible servers (Navidrome, Airsonic, etc.)
- **Multi-Provider Architecture**: Pluggable system for streaming providers (Deezer, Qobuz, SquidWTF)
- **Transparent Proxy**: Sits between your music clients and media server
- **Automatic Search**: Searches streaming providers when songs aren't local
- **On-the-Fly Downloads**: Songs download and cache for future use
- **Favorite to Keep**: When you favorite an external track, it's automatically copied to a permanent `/kept` folder separate from the cache
- **External Playlist Support**: Search and download playlists from Deezer, Qobuz, and SquidWTF with M3U generation
- **Hi-Res Audio**: SquidWTF supports up to 24-bit/192kHz FLAC
- **Full Metadata**: Downloaded files include complete ID3 tags (title, artist, album, track number, year, genre, BPM, ISRC, etc.) and cover art
- **Organized Library**: Downloads save in `Artist/Album/Track` folder structure
- **Artist Deduplication**: Merges local and streaming artists to avoid duplicates
- **Album Enrichment**: Adds missing tracks to local albums from streaming providers
- **Cover Art Proxy**: Serves cover art for external content
- **Spotify Playlist Injection** (Jellyfin only): Intercepts Spotify Import plugin playlists (Release Radar, Discover Weekly) and fills them with tracks auto-matched from streaming providers

## Supported Backends

### Jellyfin
[Jellyfin](https://jellyfin.org/) is a free and open-source media server. Allstarr connects via the Jellyfin API using your Jellyfin user login. (I plan to move this to api key if possible)

**Compatible Jellyfin clients:**

- [Feishin](https://github.com/jeffvli/feishin) (Mac/Windows/Linux)
- [Musiver](https://music.aqzscn.cn/en/) (Android/IOS/Windows/Android)

_Working on getting more currently_

### Subsonic/Navidrome
[Navidrome](https://www.navidrome.org/) and other Subsonic-compatible servers are supported via the Subsonic API.

**Compatible Subsonic clients:**

#### PC
- [Aonsoku](https://github.com/victoralvesf/aonsoku)
- [Feishin](https://github.com/jeffvli/feishin)
- [Subplayer](https://github.com/peguerosdc/subplayer)
- [Aurial](https://github.com/shrimpza/aurial)

#### Android
- [Tempus](https://github.com/eddyizm/tempus)
- [Substreamer](https://substreamerapp.com/)

#### iOS
- [Narjo](https://www.reddit.com/r/NarjoApp/)
- [Arpeggi](https://www.reddit.com/r/arpeggiApp/)

> **Want to improve client compatibility?** Pull requests are welcome!

### Incompatible Clients

These clients are **not compatible** with Allstarr due to architectural limitations:

- [Symfonium](https://symfonium.app/) - Uses offline-first architecture and never queries the server for searches, making streaming provider integration impossible. [See details](https://support.symfonium.app/t/suggestions-on-search-function/1121/)

## Supported Music Providers

- **[SquidWTF](https://tidal.squid.wtf/)** - Quality: FLAC (Hi-Res 24-bit/192kHz & CD-Lossless 16-bit/44.1kHz), AAC
- **[Deezer](https://www.deezer.com/)** - Quality: FLAC, MP3_320, MP3_128
- **[Qobuz](https://www.qobuz.com/)** - Quality: FLAC, FLAC_24_HIGH (Hi-Res 24-bit/192kHz), FLAC_24_LOW, FLAC_16, MP3_320

Choose your preferred provider via the `MUSIC_SERVICE` environment variable. Additional providers may be added in future releases.

## Requirements

- A running media server:
  - **Jellyfin**: Any recent version with API access enabled
  - **Subsonic**: Navidrome or other Subsonic-compatible server
- Credentials for at least one music provider (IF NOT USING SQUIDWTF):
  - **Deezer**: ARL token from browser cookies
  - **Qobuz**: User ID + User Auth Token from browser localStorage ([see Wiki guide](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)))
- Docker and Docker Compose (recommended) **or** [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for manual installation

## Configuration

### Environment Setup

1. **Create your environment file**
   ```bash
   cp .env.example .env
   ```

2. **Edit the `.env` file** with your configuration:

   **For Jellyfin backend:**
   ```bash
   # Backend selection
   BACKEND_TYPE=Jellyfin
   
   # Jellyfin server URL
   JELLYFIN_URL=http://localhost:8096
   
   # API key (get from Jellyfin Dashboard > API Keys)
   JELLYFIN_API_KEY=your-api-key-here
   
   # User ID (from Jellyfin Dashboard > Users > click user > check URL)
   JELLYFIN_USER_ID=your-user-id-here
   
   # Music library ID (optional, auto-detected if not set)
   JELLYFIN_LIBRARY_ID=
   ```

   **For Subsonic/Navidrome backend:**
   ```bash
   # Backend selection
   BACKEND_TYPE=Subsonic
   
   # Navidrome/Subsonic server URL
   SUBSONIC_URL=http://localhost:4533
   ```

   **Common settings (both backends):**
   ```bash
   # Path where downloaded songs will be stored
   DOWNLOAD_PATH=./downloads
   
   # Music service to use: SquidWTF, Deezer, or Qobuz
   MUSIC_SERVICE=SquidWTF
   
   # Storage mode: Permanent or Cache
   STORAGE_MODE=Permanent
   ```

   See the full `.env.example` for all available options including Deezer/Qobuz credentials.

3. **Configure your client**
   
   Point your music client to `http://localhost:5274` instead of your media server directly.

> **Tip**: Make sure the `DOWNLOAD_PATH` points to a directory that your media server can scan, so downloaded songs appear in your library.

## Advanced Configuration

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

Allstarr can automatically fill your Spotify playlists (like Release Radar and Discover Weekly) with tracks from your configured streaming provider (SquidWTF, Deezer, or Qobuz). This feature works by intercepting playlists created by the Jellyfin Spotify Import plugin and matching missing tracks with your streaming service.

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
   - Set a daily sync schedule (e.g., 4:15 PM daily)
   - The plugin will create playlists in Jellyfin and generate "missing tracks" files for songs not in your library

3. **Configure Allstarr**
   - Allstarr needs to know when the plugin runs and which playlists to intercept
   - Uses your existing `JELLYFIN_URL` and `JELLYFIN_API_KEY` settings (no additional credentials needed)

#### Configuration

| Setting | Description |
|---------|-------------|
| `SpotifyImport:Enabled` | Enable Spotify playlist injection (default: `false`) |
| `SpotifyImport:SyncStartHour` | Hour when the Spotify Import plugin runs (24-hour format, 0-23) |
| `SpotifyImport:SyncStartMinute` | Minute when the plugin runs (0-59) |
| `SpotifyImport:SyncWindowHours` | Hours to search for missing tracks files after sync time (default: 2) |
| `SpotifyImport:PlaylistIds` | Comma-separated Jellyfin playlist IDs to intercept |
| `SpotifyImport:PlaylistNames` | Comma-separated playlist names (must match order of IDs) |

**Environment variables example:**
```bash
# Enable the feature
SPOTIFY_IMPORT_ENABLED=true

# Sync window settings (optional - used to prevent fetching too frequently)
# The fetcher searches backwards from current time for the last 48 hours
SPOTIFY_IMPORT_SYNC_START_HOUR=16
SPOTIFY_IMPORT_SYNC_START_MINUTE=15
SPOTIFY_IMPORT_SYNC_WINDOW_HOURS=2

# Get playlist IDs from Jellyfin URLs: https://jellyfin.example.com/web/#/details?id=PLAYLIST_ID
SPOTIFY_IMPORT_PLAYLIST_IDS=ba50e26c867ec9d57ab2f7bf24cfd6b0,4383a46d8bcac3be2ef9385053ea18df

# Names must match exactly as they appear in Jellyfin (used to find missing tracks files)
SPOTIFY_IMPORT_PLAYLIST_NAMES=Release Radar,Discover Weekly
```

#### How It Works

1. **Spotify Import Plugin Runs** (e.g., daily at 4:15 PM)
   - Plugin fetches your Spotify playlists
   - Creates/updates playlists in Jellyfin with tracks already in your library
   - Generates "missing tracks" JSON files for songs not found locally
   - Files are named like: `Release Radar_missing_2026-02-01_16-15.json`

2. **Allstarr Fetches Missing Tracks** (within sync window)
   - Searches for missing tracks files from the Jellyfin plugin
   - Searches **+24 hours forward first** (newest files), then **-48 hours backward** if not found
   - This efficiently finds the most recent file regardless of timezone differences
   - Example: Server time 12 PM EST, file timestamped 9 PM UTC (same day) → Found in forward search
   - Caches the list of missing tracks in Redis + file cache
   - Runs automatically on startup (if needed) and every 5 minutes during the sync window

3. **Allstarr Matches Tracks** (2 minutes after startup, then configurable interval)
   - For each missing track, searches your streaming provider (SquidWTF, Deezer, or Qobuz)
   - Uses fuzzy matching to find the best match (title + artist similarity)
   - Rate-limited to avoid overwhelming the service (150ms delay between searches)
   - Caches matched results for 1 hour
   - **Pre-builds playlist items cache** for instant serving (no "on the fly" building)
   - Default interval: 24 hours (configurable via `SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS`)
   - Set to 0 to only run once on startup (manual trigger via admin UI still works)

4. **You Open the Playlist in Jellyfin**
   - Allstarr intercepts the request
   - Returns a merged list: local tracks + matched streaming tracks
   - Loads instantly from cache (no searching needed!)

5. **You Play a Track**
   - If it's a local track, streams from Jellyfin normally
   - If it's a matched track, downloads from streaming provider on-demand
   - Downloaded tracks are saved to your library for future use

#### Manual Triggers

You can manually trigger syncing and matching via API:

```bash
# Fetch missing tracks from Jellyfin plugin
curl "https://your-jellyfin-proxy.com/spotify/sync?api_key=YOUR_API_KEY"

# Trigger track matching (searches streaming provider)
curl "https://your-jellyfin-proxy.com/spotify/match?api_key=YOUR_API_KEY"

# Clear cache to force re-matching
curl "https://your-jellyfin-proxy.com/spotify/clear-cache?api_key=YOUR_API_KEY"
```

#### Startup Behavior

When Allstarr starts with Spotify Import enabled:

**Smart Cache Check:**
- Checks if today's sync window has passed (e.g., if sync is at 4 PM + 2 hour window = 6 PM)
- If before 6 PM and yesterday's cache exists → **Skips fetch** (cache is still current)
- If after 6 PM or no cache exists → **Fetches missing tracks** from Jellyfin plugin

**Track Matching:**
- **T+2min**: Matches tracks with streaming provider (with rate limiting)
- Only matches playlists that don't already have cached matches
- **Result**: Playlists load instantly when you open them!

**Example Timeline:**
- Plugin runs daily at 4:15 PM, creates files at ~4:16 PM
- You restart Allstarr at 12:00 PM (noon) the next day
- Startup check: "Today's sync window ends at 6 PM, and I have yesterday's 4:16 PM file"
- **Decision**: Skip fetch, use existing cache
- At 6:01 PM: Next scheduled check will search for new files

#### Troubleshooting

**Playlists are empty:**
- Check that the Spotify Import plugin is running and creating playlists
- Verify `SPOTIFY_IMPORT_PLAYLIST_IDS` match your Jellyfin playlist IDs
- Check logs: `docker-compose logs -f allstarr | grep -i spotify`

**Tracks aren't matching:**
- Ensure your streaming provider is configured (`MUSIC_SERVICE`, credentials)
- Check that playlist names in `SPOTIFY_IMPORT_PLAYLIST_NAMES` match exactly
- Manually trigger matching: `curl "https://your-proxy.com/spotify/match?api_key=KEY"`

**Sync timing issues:**
- Set `SPOTIFY_IMPORT_SYNC_START_HOUR/MINUTE` to match your plugin schedule
- Increase `SPOTIFY_IMPORT_SYNC_WINDOW_HOURS` if files aren't being found
- Check Jellyfin plugin logs to confirm when it runs

#### Notes

- This feature uses your existing `JELLYFIN_URL` and `JELLYFIN_API_KEY` settings
- Matched tracks are cached for 1 hour to avoid repeated searches
- Missing tracks cache persists across restarts (stored in Redis + file cache)
- Rate limiting prevents overwhelming your streaming provider (150ms between searches)
- Only works with Jellyfin backend (not Subsonic/Navidrome)

### Getting Credentials

#### Deezer ARL Token

See the [Wiki guide](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Deezer-Credentials-(ARL-Token)) for detailed instructions on obtaining your Deezer ARL token.

#### Qobuz Credentials

See the [Wiki guide](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)) for detailed instructions on obtaining your Qobuz User ID and User Auth Token.

## Limitations

- **Playlist Search**: Subsonic clients like Aonsoku filter playlists client-side from a cached `getPlaylists` call. Streaming provider playlists appear in global search (`search3`) but not in the Playlists tab filter.
- **Region Restrictions**: Some tracks may be unavailable depending on your region and provider.
- **Token Expiration**: Provider authentication tokens expire and need periodic refresh.

## Architecture

```
                                                    ┌─────────────────┐
                                               ┌───▶│    Jellyfin     │
┌─────────────────┐     ┌──────────────────┐   │    │    Server       │
│  Music Client   │────▶│     Allstarr     │───┤    └─────────────────┘
│  (Aonsoku,      │◀────│   (Proxy)        │◀──┤
│   Finamp, etc.) │     │                  │   │    ┌─────────────────┐
└─────────────────┘     └────────┬─────────┘   └───▶│   Navidrome     │
                                 │                  │   (Subsonic)    │
                                 ▼                  └─────────────────┘
                        ┌─────────────────┐
                        │ Music Providers │
                        │  - SquidWTF     │
                        │  - Deezer       │
                        │  - Qobuz        │
                        └─────────────────┘
```

The proxy intercepts requests from your music client and:
1. Forwards library requests to your configured backend (Jellyfin or Subsonic)
2. Merges results with content from your music provider
3. Downloads and caches external tracks on-demand
4. Serves audio streams transparently

**Note**: Only the controller matching your configured `BACKEND_TYPE` is registered at runtime, preventing route conflicts and ensuring clean API separation.

## Manual Installation

If you prefer to run Allstarr without Docker:

1. **Clone the repository**
   ```bash
   git clone https://github.com/SoPat712/allstarr.git
   cd allstarr
   ```

2. **Restore dependencies**
   ```bash
   dotnet restore
   ```

3. **Configure the application**
   
   Edit `allstarr/appsettings.json`:
   
   **For Jellyfin:**
   ```json
   {
     "Backend": {
       "Type": "Jellyfin"
     },
     "Jellyfin": {
       "Url": "http://localhost:8096",
       "ApiKey": "your-api-key",
       "UserId": "your-user-id",
       "MusicService": "SquidWTF"
     },
     "Library": {
       "DownloadPath": "./downloads"
     }
   }
   ```
   
   **For Subsonic/Navidrome:**
   ```json
   {
     "Backend": {
       "Type": "Subsonic"
     },
     "Subsonic": {
       "Url": "http://localhost:4533",
       "MusicService": "SquidWTF"
     },
     "Library": {
       "DownloadPath": "./downloads"
     }
   }
   ```

4. **Run the server**
   ```bash
   cd allstarr
   dotnet run
   ```
   
   The proxy will start on `http://localhost:5274` by default.

5. **Configure your client**
   
   Point your music client to `http://localhost:5274` instead of your media server directly.

## API Endpoints

### Subsonic Backend

The proxy implements the Subsonic API and adds transparent streaming provider integration:

| Endpoint | Description |
|----------|-------------|
| `GET /rest/search3` | Merged search results from Navidrome + streaming provider |
| `GET /rest/stream` | Streams audio, downloading from provider if needed |
| `GET /rest/getSong` | Returns song details (local or from provider) |
| `GET /rest/getAlbum` | Returns album with tracks from both sources |
| `GET /rest/getArtist` | Returns artist with albums from both sources |
| `GET /rest/getCoverArt` | Proxies cover art for external content |
| `GET /rest/star` | Stars items; triggers automatic playlist download for external playlists |

All other Subsonic API endpoints are passed through to Navidrome unchanged.

### Jellyfin Backend

The proxy implements a subset of the Jellyfin API:

| Endpoint | Description |
|----------|-------------|
| `GET /Items` | Search and browse library items |
| `GET /Artists` | Browse artists with streaming provider results |
| `GET /Audio/{id}/stream` | Stream audio, downloading from provider if needed |
| `GET /Items/{id}/Images/{type}` | Proxy cover art for external content |
| `POST /UserFavoriteItems/{id}` | Favorite items; triggers playlist download |

All other Jellyfin API endpoints are passed through unchanged.

## External ID Format

External (streaming provider) content uses typed IDs:

| Type | Format | Example |
|------|--------|---------|
| Song | `ext-{provider}-song-{id}` | `ext-deezer-song-123456`, `ext-qobuz-song-789012` |
| Album | `ext-{provider}-album-{id}` | `ext-deezer-album-789012`, `ext-qobuz-album-456789` |
| Artist | `ext-{provider}-artist-{id}` | `ext-deezer-artist-259`, `ext-qobuz-artist-123` |

Legacy format `ext-deezer-{id}` is also supported (assumes song type).

## Download Folder Structure

Downloaded music is organized as:
```
downloads/
├── Artist Name/
│   ├── Album Title/
│   │   ├── 01 - Track One.mp3
│   │   ├── 02 - Track Two.mp3
│   │   └── ...
│   └── Another Album/
│       └── ...
├── Another Artist/
│   └── ...
└── playlists/
    ├── My Favorite Songs.m3u
    ├── Chill Vibes.m3u
    └── ...
```

Playlists are stored as M3U files with relative paths to downloaded tracks, making them portable and compatible with most music players.

## Metadata Embedding

Downloaded files include:
- **Basic**: Title, Artist, Album, Album Artist
- **Track Info**: Track Number, Total Tracks, Disc Number
- **Dates**: Year, Release Date
- **Audio**: BPM, Duration
- **Identifiers**: ISRC (in comments)
- **Credits**: Contributors/Composers
- **Visual**: Embedded cover art (high resolution)
- **Rights**: Copyright, Label

## Development

### Build
```bash
dotnet build
```

### Run Tests
```bash
dotnet test
```

### Project Structure

```
allstarr/
├── Controllers/
│   ├── JellyfinController.cs              # Jellyfin API controller (registered when Backend:Type=Jellyfin)
│   └── SubsonicController.cs              # Subsonic API controller (registered when Backend:Type=Subsonic)
├── Middleware/
│   └── GlobalExceptionHandler.cs          # Global error handling
├── Models/
│   ├── Domain/                            # Domain entities
│   │   ├── Song.cs
│   │   ├── Album.cs
│   │   └── Artist.cs
│   ├── Settings/                          # Configuration models
│   │   ├── SubsonicSettings.cs
│   │   ├── DeezerSettings.cs
│   │   └── QobuzSettings.cs
│   ├── Download/                          # Download-related models
│   │   ├── DownloadInfo.cs
│   │   └── DownloadStatus.cs
│   ├── Search/
│   │   └── SearchResult.cs
│   └── Subsonic/
│       └── ScanStatus.cs
├── Services/
│   ├── Common/                            # Shared services
│   │   ├── BaseDownloadService.cs         # Template method base class
│   │   ├── PathHelper.cs                  # Path utilities
│   │   ├── Result.cs                      # Result<T> pattern
│   │   └── Error.cs                       # Error types
│   ├── Deezer/                            # Deezer provider
│   │   ├── DeezerDownloadService.cs
│   │   ├── DeezerMetadataService.cs
│   │   └── DeezerStartupValidator.cs
│   ├── Qobuz/                             # Qobuz provider
│   │   ├── QobuzDownloadService.cs
│   │   ├── QobuzMetadataService.cs
│   │   ├── QobuzBundleService.cs
│   │   └── QobuzStartupValidator.cs
│   ├── Local/                             # Local library
│   │   ├── ILocalLibraryService.cs
│   │   └── LocalLibraryService.cs
│   ├── Subsonic/                          # Subsonic API logic
│   │   ├── SubsonicProxyService.cs        # Request proxying
│   │   ├── SubsonicModelMapper.cs         # Model mapping
│   │   ├── SubsonicRequestParser.cs       # Request parsing
│   │   └── SubsonicResponseBuilder.cs     # Response building
│   ├── Validation/                        # Startup validation
│   │   ├── IStartupValidator.cs
│   │   ├── BaseStartupValidator.cs
│   │   ├── SubsonicStartupValidator.cs
│   │   ├── StartupValidationOrchestrator.cs
│   │   └── ValidationResult.cs
│   ├── IDownloadService.cs                # Download interface
│   ├── IMusicMetadataService.cs           # Metadata interface
│   └── StartupValidationService.cs
├── Program.cs                             # Application entry point
└── appsettings.json                       # Configuration

allstarr.Tests/
├── DeezerDownloadServiceTests.cs          # Deezer download tests
├── DeezerMetadataServiceTests.cs          # Deezer metadata tests
├── QobuzDownloadServiceTests.cs           # Qobuz download tests (127 tests)
├── LocalLibraryServiceTests.cs            # Local library tests
├── SubsonicModelMapperTests.cs            # Model mapping tests
├── SubsonicProxyServiceTests.cs           # Proxy service tests
├── SubsonicRequestParserTests.cs          # Request parser tests
└── SubsonicResponseBuilderTests.cs        # Response builder tests
```

### Dependencies

- **BouncyCastle.Cryptography** - Blowfish decryption for Deezer streams
- **TagLibSharp** - ID3 tag and cover art embedding
- **Swashbuckle.AspNetCore** - Swagger/OpenAPI documentation
- **xUnit** - Unit testing framework
- **Moq** - Mocking library for tests
- **FluentAssertions** - Fluent assertion library for tests

## Contributing

We welcome contributions! Here's how to get started:

### Development Setup

1. **Clone the repository**
   ```bash
   git clone https://github.com/SoPat712/allstarr.git
   cd allstarr
   ```

2. **Build and run locally**
   
   Using Docker (recommended for development):
   ```bash
   # Copy and configure environment
   cp .env.example .env
   vi .env
   
   # Build and start with local changes
   docker-compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
   
   # View logs
   docker-compose logs -f
   ```
   
   Or using .NET directly:
   ```bash
   # Restore dependencies
   dotnet restore
   
   # Run the application
   cd allstarr
   dotnet run
   ```

3. **Run tests**
   ```bash
   dotnet test
   ```

### Making Changes

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Run tests to ensure everything works
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to your fork (`git push origin feature/amazing-feature`)
7. Open a Pull Request

### Code Style

- Follow existing code patterns and conventions
- Add tests for new features
- Update documentation as needed
- Keep commits focused and atomic

### Testing

All changes should include appropriate tests:
```bash
# Run all tests
dotnet test

# Run specific test file
dotnet test --filter "FullyQualifiedName~SubsonicProxyServiceTests"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## License

GPL-3.0

## Acknowledgments

- [Navidrome](https://www.navidrome.org/) - The excellent self-hosted music server
- [Jellyfin](https://jellyfin.org/) - The free and open-source media server
- [Deezer](https://www.deezer.com/) - Music streaming service
- [Qobuz](https://www.qobuz.com/) - Hi-Res music streaming service
- [Subsonic API](http://www.subsonic.org/pages/api.jsp) - The API specification
