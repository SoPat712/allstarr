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

# 4. Start services
docker-compose up -d

# 5. Check status
docker-compose ps
docker-compose logs -f
```

The proxy will be available at `http://localhost:5274`.

## Web Dashboard

Allstarr includes a web UI for easy configuration and playlist management, accessible at `http://localhost:5275`
<img width="1664" height="1101" alt="image" src="https://github.com/user-attachments/assets/9159100b-7e11-449e-8530-517d336d6bd2" />


### Features

- **Playlist Management**: Link Jellyfin playlists to Spotify playlists with just a few clicks
- **Provider Matching**: It should fill in the gaps of your Jellyfin library with tracks from your selected provider
- **WebUI**: Update settings without manually editing .env files
- **Music**: Using multiple sources for music (optimized for SquidWTF right now, though)
- **Lyrics**: Using multiple sources for lyrics - Jellyfin local, Spotify Lyrics API, LyricsPlus (multi-source), and LRCLib
- **Scrobbling**: Track your listening history to Last.fm and ListenBrainz with automatic scrobbling
- **Downloads Management**: View, download, and manage your kept files through the web UI
- **Diagnostics**: Monitor system performance, memory usage, cache statistics, and endpoint usage

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

### Nginx Proxy Setup (Optional)

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
- **Lyrics Support**: Multi-source lyrics fetching from Jellyfin local files, Spotify Lyrics API (synchronized), LyricsPlus (multi-source aggregator), and LRCLib (community database)
- **Scrobbling Support**: Track your listening history to Last.fm and ListenBrainz

## Supported Backends

### Jellyfin
[Jellyfin](https://jellyfin.org/) is a free and open-source media server. Allstarr connects via the Jellyfin API using your Jellyfin user login. (I plan to move this to api key if possible)

**Compatible Jellyfin clients:**

- [Feishin](https://github.com/jeffvli/feishin) (Mac/Windows/Linux)
<img width="1691" height="1128" alt="image" src="https://github.com/user-attachments/assets/c602f71c-c4dd-49a9-b533-1558e24a9f45" />


- [Musiver](https://music.aqzscn.cn/en/) (Android/iOS/Windows/Android)
<img width="523" height="1025" alt="image" src="https://github.com/user-attachments/assets/135e2721-5fd7-482f-bb06-b0736003cfe7" />


- [Finamp](https://github.com/jmshrv/finamp) (Android/iOS)

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

See [CLIENTS.md](CLIENTS.md) for more detailed client information.

## Supported Music Providers

- **[SquidWTF](https://tidal.squid.wtf/)** - Quality: FLAC (Hi-Res 24-bit/192kHz & CD-Lossless 16-bit/44.1kHz), AAC
- **[Deezer](https://www.deezer.com/)** - Quality: FLAC, MP3_320, MP3_128
- **[Qobuz](https://www.qobuz.com/)** - Quality: FLAC, FLAC_24_HIGH (Hi-Res 24-bit/192kHz), FLAC_24_LOW, FLAC_16, MP3_320

Choose your preferred provider via the `MUSIC_SERVICE` environment variable. Additional providers may be added in future releases.

## Requirements

- A running media server:
  - **Jellyfin**: Any recent version with API access enabled
  - **Subsonic**: Navidrome or other Subsonic-compatible server
- **Docker and Docker Compose** (recommended) - includes Redis and Spotify Lyrics API sidecars
  - Redis is used for caching (search results, playlists, lyrics, etc.)
  - Spotify Lyrics API provides synchronized lyrics for Spotify tracks
- Credentials for at least one music provider (IF NOT USING SQUIDWTF):
  - **Deezer**: ARL token from browser cookies
  - **Qobuz**: User ID + User Auth Token from browser localStorage ([see Wiki guide](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)))
- **OR** [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for manual installation (requires separate Redis setup)

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

For detailed configuration options, see [CONFIGURATION.md](CONFIGURATION.md).

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

## Documentation

- **[CONFIGURATION.md](CONFIGURATION.md)** - Detailed configuration guide for all settings
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Technical architecture and API documentation
- **[CLIENTS.md](CLIENTS.md)** - Client compatibility and setup
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Development setup and contribution guidelines

## Limitations

- **Playlist Search**: Subsonic clients like Aonsoku filter playlists client-side from a cached `getPlaylists` call. Streaming provider playlists appear in global search (`search3`) but not in the Playlists tab filter.
- **Region Restrictions**: Some tracks may be unavailable depending on your region and provider.
- **Token Expiration**: Provider authentication tokens expire and need periodic refresh.

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
