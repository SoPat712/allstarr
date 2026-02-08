# Allstarr

[![Build Status - Main](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml/badge.svg?branch=main)](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml)
[![Build Status - Beta](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml/badge.svg?branch=beta)](https://github.com/SoPat712/allstarr/actions/workflows/docker.yml)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fsopat712%2Fallstarr-blue)](https://github.com/SoPat712/allstarr/pkgs/container/allstarr)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)

A media server proxy that integrates music streaming providers with your local library. Works with **Jellyfin** and **Subsonic-compatible** servers. When a song isn't in your local library, it gets fetched from your configured provider, downloaded, and served to your client. The downloaded song then lives in your library for next time.

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

Allstarr includes a web UI for easy configuration and playlist management, accessible at `http://localhost:5275`

### Features

- **Playlist Management**: Link Jellyfin playlists to Spotify playlists with just a few clicks
- **Provider Matching**: It should fill in the gaps of your Jellyfin library with tracks from your selected provider
- **WebUI**: Update settings without manually editing .env files
- **Music**: Using multiple sources for music (optimized for SquidWTF right now, though)
- **Lyrics**: Using multiple sources for lyrics, first Jellyfin Lyrics, then Spotify Lyrics, then LrcLib as a last resort

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
4. **Restart** to apply changes (should be a banner)

Then, proceeed to **Active Playlists**, which shows you which Spotify playlists are currently being monitored and filled with tracks, and lets you do a bunch of useful operations on them.

### Configuration Persistence

The web UI updates your `.env` file directly. Changes persist across container restarts, but require a restart to take effect. In development mode, the `.env` file is in your project root. In Docker, it's at `/app/.env`.

There's an environment variable to modify this.


**Recommended workflow**: Use the `sp_dc` cookie method alongside the [Spotify Import Plugin](https://github.com/Viperinius/jellyfin-plugin-spotify-import?tab=readme-ov-file).



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
- [Finamp](https://github.com/jmshrv/finamp) ()

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

Allstarr automatically fills your Spotify playlists (like Release Radar and Discover Weekly) with tracks from your configured streaming provider (SquidWTF, Deezer, or Qobuz). This works by intercepting playlists created by the Jellyfin Spotify Import plugin and matching missing tracks with your streaming service.

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

### Jellyfin Backend (Primary Focus)

The proxy provides comprehensive Jellyfin API support with streaming provider integration:

| Endpoint | Description |
|----------|-------------|
| `GET /Items` | Search and browse library items (local + streaming providers) |
| `GET /Artists` | Browse artists with merged results from local + streaming |
| `GET /Artists/AlbumArtists` | Album artists with streaming provider results |
| `GET /Users/{userId}/Items` | User library items with external content |
| `GET /Audio/{id}/stream` | Stream audio, downloading from provider on-demand |
| `GET /Audio/{id}/Lyrics` | Lyrics from Jellyfin, Spotify, or LRCLib |
| `GET /Items/{id}/Images/{type}` | Proxy cover art for external content |
| `GET /Playlists/{id}/Items` | Playlist items (Spotify Import integration) |
| `POST /UserFavoriteItems/{id}` | Favorite items; copies external tracks to kept folder |
| `DELETE /UserFavoriteItems/{id}` | Unfavorite items |
| `POST /Sessions/Playing` | Playback reporting for external tracks |
| `POST /Sessions/Playing/Progress` | Playback progress tracking |
| `POST /Sessions/Playing/Stopped` | Playback stopped reporting |
| `WebSocket /socket` | Real-time session management and remote control |

**Admin API (Port 5275):**
| Endpoint | Description |
|----------|-------------|
| `GET /api/config` | Get current configuration |
| `POST /api/config` | Update configuration |
| `GET /api/playlists` | List Spotify Import playlists |
| `POST /api/playlists/link` | Link Jellyfin playlist to Spotify |
| `DELETE /api/playlists/{id}` | Unlink playlist |
| `POST /spotify/sync` | Fetch missing tracks from Jellyfin plugin |
| `POST /spotify/match` | Trigger track matching |
| `POST /spotify/match-all` | Match all playlists |
| `POST /spotify/clear-cache` | Clear playlist cache |
| `POST /spotify/refresh-playlist` | Refresh specific playlist |

All other Jellyfin API endpoints are passed through unchanged.

### Subsonic Backend

The proxy implements the Subsonic API with streaming provider integration:

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

## External ID Format

External (streaming provider) content uses typed IDs:

| Type | Format | Example |
|------|--------|---------|
| Song | `ext-{provider}-song-{id}` | `ext-deezer-song-123456`, `ext-qobuz-song-789012` |
| Album | `ext-{provider}-album-{id}` | `ext-deezer-album-789012`, `ext-qobuz-album-456789` |
| Artist | `ext-{provider}-artist-{id}` | `ext-deezer-artist-259`, `ext-qobuz-artist-123` |

Legacy format `ext-deezer-{id}` is also supported (assumes song type).

## Download Folder Structure

All downloads are organized under a single base directory (default: `./downloads`):

```
downloads/
├── permanent/                    # Permanent downloads (STORAGE_MODE=Permanent)
│   ├── Artist Name/
│   │   ├── Album Title/
│   │   │   ├── 01 - Track One.flac
│   │   │   ├── 02 - Track Two.flac
│   │   │   └── ...
│   │   └── Another Album/
│   │       └── ...
│   └── playlists/
│       ├── My Favorite Songs.m3u
│       └── Chill Vibes.m3u
├── cache/                        # Temporary cache (STORAGE_MODE=Cache)
│   └── Artist Name/
│       └── Album Title/
│           └── Track.flac
└── kept/                         # Favorited external tracks (always permanent)
    └── Artist Name/
        └── Album Title/
            └── Track.flac
```

**Storage modes:**
- **Permanent** (`downloads/permanent/`): Files saved permanently and registered in your media server
- **Cache** (`downloads/cache/`): Temporary files, auto-cleaned after `CACHE_DURATION_HOURS`
- **Kept** (`downloads/kept/`): External tracks you've favorited - always permanent, separate from cache

Playlists are stored as M3U files with relative paths, making them portable and compatible with most music players.

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
│   ├── AdminController.cs                 # Admin dashboard API
│   ├── JellyfinController.cs              # Jellyfin API controller
│   └── SubsonicController.cs              # Subsonic API controller
├── Filters/
│   ├── AdminPortFilter.cs                 # Admin port access control
│   ├── ApiKeyAuthFilter.cs                # API key authentication
│   └── JellyfinAuthFilter.cs              # Jellyfin authentication
├── Middleware/
│   ├── AdminStaticFilesMiddleware.cs      # Admin UI static file serving
│   ├── GlobalExceptionHandler.cs          # Global error handling
│   └── WebSocketProxyMiddleware.cs        # WebSocket proxying for Jellyfin
├── Models/
│   ├── Domain/                            # Domain entities
│   │   ├── Song.cs
│   │   ├── Album.cs
│   │   └── Artist.cs
│   ├── Settings/                          # Configuration models
│   │   ├── SubsonicSettings.cs
│   │   ├── DeezerSettings.cs
│   │   ├── QobuzSettings.cs
│   │   ├── SquidWTFSettings.cs
│   │   ├── SpotifyApiSettings.cs
│   │   ├── SpotifyImportSettings.cs
│   │   ├── MusicBrainzSettings.cs
│   │   └── RedisSettings.cs
│   ├── Download/                          # Download-related models
│   │   ├── DownloadInfo.cs
│   │   └── DownloadStatus.cs
│   ├── Lyrics/
│   │   └── LyricsInfo.cs
│   ├── Search/
│   │   └── SearchResult.cs
│   ├── Spotify/
│   │   ├── MissingTrack.cs
│   │   └── SpotifyPlaylistTrack.cs
│   └── Subsonic/
│       ├── ExternalPlaylist.cs
│       └── ScanStatus.cs
├── Services/
│   ├── Common/                            # Shared services
│   │   ├── BaseDownloadService.cs         # Template method base class
│   │   ├── CacheCleanupService.cs         # Cache cleanup background service
│   │   ├── CacheWarmingService.cs         # Startup cache warming
│   │   ├── EndpointBenchmarkService.cs    # Endpoint performance benchmarking
│   │   ├── FuzzyMatcher.cs                # Fuzzy string matching
│   │   ├── GenreEnrichmentService.cs      # MusicBrainz genre enrichment
│   │   ├── OdesliService.cs               # Odesli/song.link conversion
│   │   ├── ParallelMetadataService.cs     # Parallel metadata fetching
│   │   ├── PathHelper.cs                  # Path utilities
│   │   ├── PlaylistIdHelper.cs            # Playlist ID helpers
│   │   ├── RedisCacheService.cs           # Redis caching
│   │   ├── RoundRobinFallbackHelper.cs    # Load balancing and failover
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
│   ├── SquidWTF/                          # SquidWTF provider
│   │   ├── SquidWTFDownloadService.cs
│   │   ├── SquidWTFMetadataService.cs
│   │   └── SquidWTFStartupValidator.cs
│   ├── Jellyfin/                          # Jellyfin integration
│   │   ├── JellyfinModelMapper.cs         # Model mapping
│   │   ├── JellyfinProxyService.cs        # Request proxying
│   │   ├── JellyfinResponseBuilder.cs     # Response building
│   │   ├── JellyfinSessionManager.cs      # Session management
│   │   └── JellyfinStartupValidator.cs    # Startup validation
│   ├── Lyrics/                            # Lyrics services
│   │   ├── LrclibService.cs               # LRCLIB lyrics
│   │   ├── LyricsPrefetchService.cs       # Background lyrics prefetching
│   │   ├── LyricsStartupValidator.cs      # Lyrics validation
│   │   └── SpotifyLyricsService.cs        # Spotify lyrics
│   ├── MusicBrainz/
│   │   └── MusicBrainzService.cs          # MusicBrainz metadata
│   ├── Spotify/                           # Spotify integration
│   │   ├── SpotifyApiClient.cs            # Spotify API client
│   │   ├── SpotifyMissingTracksFetcher.cs # Missing tracks fetcher
│   │   ├── SpotifyPlaylistFetcher.cs      # Playlist fetcher
│   │   └── SpotifyTrackMatchingService.cs # Track matching
│   ├── Local/                             # Local library
│   │   ├── ILocalLibraryService.cs
│   │   └── LocalLibraryService.cs
│   ├── Subsonic/                          # Subsonic API logic
│   │   ├── PlaylistSyncService.cs         # Playlist synchronization
│   │   ├── SubsonicModelMapper.cs         # Model mapping
│   │   ├── SubsonicProxyService.cs        # Request proxying
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
├── wwwroot/                               # Admin UI static files
│   ├── index.html                         # Admin dashboard
│   └── placeholder.png                    # Placeholder image
├── Program.cs                             # Application entry point
└── appsettings.json                       # Configuration

allstarr.Tests/
├── DeezerDownloadServiceTests.cs          # Deezer download tests
├── DeezerMetadataServiceTests.cs          # Deezer metadata tests
├── JellyfinResponseStructureTests.cs      # Jellyfin response tests
├── QobuzDownloadServiceTests.cs           # Qobuz download tests
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
- Keep commits feature focused

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

- [octo-fiesta](https://github.com/V1ck3s/octo-fiesta) - The original
- [octo-fiestarr](https://github.com/bransoned/octo-fiestarr) - The fork that introduced me to this idea based on the above
- [Jellyfin Spotify Import Plugin](https://github.com/Viperinius/jellyfin-plugin-spotify-import?tab=readme-ov-file) - The plugin that I **strongly** recommend using alongside this repo
- [Jellyfin](https://jellyfin.org/) - The free and open-source media server
- [Navidrome](https://www.navidrome.org/) - The excellent self-hosted music server
- [Subsonic API](http://www.subsonic.org/pages/api.jsp) - The API specification
- [Hi-Fi API](https://github.com/binimum/hifi-api) - These people do some great work, and you should thank them for this even existing!
- [Deezer](https://www.deezer.com/) - Music streaming service
- [Qobuz](https://www.qobuz.com/) - Hi-Res music streaming service
- [spotify-lyrics-api](https://github.com/akashrchandran/spotify-lyrics-api) - Thank them for the fact that we have access to Spotify's lyrics!
- [LRCLIB](https://github.com/tranxuanthang/lrclib) - The GOATS for giving us a free api for lyrics! They power LRCGET, which I'm sure some of you have heard of