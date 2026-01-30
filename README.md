# Allstarr

A media server proxy that integrates music streaming providers with your local library. Works with **Jellyfin** and **Subsonic-compatible** servers (Navidrome). When a song isn't in your local library, it gets fetched from your configured provider, downloaded, and served to your client. The downloaded song then lives in your library for next time.

**THIS IS UNDER ACTIVE DEVELOPMENT**

Please report all bugs as soon as possible, as the Jellyfin addition is entirely a test at this point

## Quick Start

```bash
# 1. Configure environment
cp .env.example .env
vi .env  # Edit with your settings

# 2. Start services
docker-compose up -d --build

# 3. Check status
docker-compose ps
docker-compose logs -f
```

### Nginx Proxy Setup (Required)

This service only exposes ports internally. You **must** use nginx to proxy to it:

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
- **External Playlist Support**: Search and download playlists from Deezer, Qobuz, and SquidWTF with M3U generation
- **Hi-Res Audio**: SquidWTF supports up to 24-bit/192kHz FLAC
- **Full Metadata**: Downloaded files include complete ID3 tags (title, artist, album, track number, year, genre, BPM, ISRC, etc.) and cover art
- **Organized Library**: Downloads save in `Artist/Album/Track` folder structure
- **Artist Deduplication**: Merges local and streaming artists to avoid duplicates
- **Album Enrichment**: Adds missing tracks to local albums from streaming providers
- **Cover Art Proxy**: Serves cover art for external content

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
  - **Qobuz**: User ID + User Auth Token from browser localStorage ([see Wiki guide](https://github.com/V1ck3s/allstarr/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)))
- Docker and Docker Compose (recommended) **or** [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) for manual installation

## Quick Start (Docker)

The easiest way to run Allstarr is with Docker Compose.

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

3. **Start the container**
   ```bash
   docker-compose up -d
   ```
   
   The proxy will be available at `http://localhost:5274`.

4. **Configure your client**
   
   Point your music client to `http://localhost:5274` instead of your media server directly.

> **Tip**: Make sure the `DOWNLOAD_PATH` points to a directory that your media server can scan, so downloaded songs appear in your library.

## Configuration

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

### Deezer Settings

| Setting | Description |
|---------|-------------|
| `Deezer:Arl` | Your Deezer ARL token (required if using Deezer) |
| `Deezer:ArlFallback` | Backup ARL token if primary fails |
| `Deezer:Quality` | Preferred audio quality: `FLAC`, `MP3_320`, `MP3_128`. If not specified, the highest available quality for your account will be used |

### Qobuz Settings

| Setting | Description |
|---------|-------------|
| `Qobuz:UserAuthToken` | Your Qobuz User Auth Token (required if using Qobuz) - [How to get it](https://github.com/V1ck3s/allstarr/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)) |
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

### Getting Credentials

#### Deezer ARL Token

See the [Wiki guide](https://github.com/V1ck3s/allstarr/wiki/Getting-Deezer-Credentials-(ARL-Token)) for detailed instructions on obtaining your Deezer ARL token.

#### Qobuz Credentials

See the [Wiki guide](https://github.com/V1ck3s/allstarr/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)) for detailed instructions on obtaining your Qobuz User ID and User Auth Token.

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
   git clone https://github.com/your-username/allstarr.git
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

## License

GPL-3.0

## Acknowledgments

- [Navidrome](https://www.navidrome.org/) - The excellent self-hosted music server
- [Deezer](https://www.deezer.com/) - Music streaming service
- [Qobuz](https://www.qobuz.com/) - Hi-Res music streaming service
- [Subsonic API](http://www.subsonic.org/pages/api.jsp) - The API specification
