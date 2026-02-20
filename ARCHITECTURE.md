# Architecture

This document describes the technical architecture of Allstarr.

## System Architecture

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
| `GET /api/admin/health` | Health check endpoint |
| `GET /api/admin/config` | Get current configuration |
| `POST /api/admin/config` | Update configuration |
| `POST /api/admin/cache/clear` | Clear cache |
| `GET /api/admin/status` | Get system status |
| `GET /api/admin/memory-stats` | Get memory usage statistics |
| `POST /api/admin/force-gc` | Force garbage collection |
| `GET /api/admin/sessions` | Get active sessions |
| `GET /api/admin/debug/endpoint-usage` | Get endpoint usage statistics |
| `DELETE /api/admin/debug/endpoint-usage` | Clear endpoint usage log |
| `GET /api/admin/squidwtf-base-url` | Get SquidWTF base URL |
| `GET /api/admin/playlists` | List all playlists with status |
| `GET /api/admin/playlists/{name}/tracks` | Get tracks for playlist |
| `POST /api/admin/playlists/refresh` | Refresh all playlists |
| `POST /api/admin/playlists/{name}/match` | Match tracks for playlist |
| `POST /api/admin/playlists/{name}/clear-cache` | Clear playlist cache |
| `POST /api/admin/playlists/match-all` | Match all playlists |
| `POST /api/admin/playlists` | Add new playlist |
| `DELETE /api/admin/playlists/{name}` | Remove playlist |
| `POST /api/admin/playlists/{name}/map` | Save manual track mapping |
| `GET /api/admin/jellyfin/search` | Search Jellyfin library |
| `GET /api/admin/jellyfin/track/{id}` | Get Jellyfin track details |
| `GET /api/admin/jellyfin/users` | List Jellyfin users |
| `GET /api/admin/jellyfin/libraries` | List Jellyfin libraries |
| `GET /api/admin/jellyfin/playlists` | List Jellyfin playlists |
| `POST /api/admin/jellyfin/playlists/{id}/link` | Link Jellyfin playlist to Spotify |
| `DELETE /api/admin/jellyfin/playlists/{name}/unlink` | Unlink playlist |
| `PUT /api/admin/playlists/{name}/schedule` | Update playlist sync schedule |
| `GET /api/admin/spotify/user-playlists` | Get Spotify user playlists |
| `GET /api/admin/spotify/sync` | Trigger Spotify sync |
| `GET /api/admin/spotify/match` | Trigger Spotify track matching |
| `POST /api/admin/spotify/clear-cache` | Clear Spotify cache |
| `GET /api/admin/spotify/mappings` | Get Spotify track mappings (paginated) |
| `GET /api/admin/spotify/mappings/{spotifyId}` | Get specific Spotify mapping |
| `POST /api/admin/spotify/mappings` | Save Spotify track mapping |
| `DELETE /api/admin/spotify/mappings/{spotifyId}` | Delete Spotify mapping |
| `GET /api/admin/spotify/mappings/stats` | Get Spotify mapping statistics |
| `GET /api/admin/downloads` | List kept downloads |
| `DELETE /api/admin/downloads` | Delete kept file |
| `GET /api/admin/downloads/file` | Download specific file |
| `GET /api/admin/downloads/all` | Download all files as zip |
| `GET /api/admin/scrobbling/status` | Get scrobbling status |
| `POST /api/admin/scrobbling/lastfm/authenticate` | Authenticate Last.fm |
| `GET /api/admin/scrobbling/lastfm/auth-url` | Get Last.fm auth URL |
| `POST /api/admin/scrobbling/lastfm/get-session` | Get Last.fm session key |
| `POST /api/admin/scrobbling/lastfm/test` | Test Last.fm connection |
| `POST /api/admin/scrobbling/lastfm/debug-auth` | Debug Last.fm auth |
| `POST /api/admin/scrobbling/listenbrainz/validate` | Validate ListenBrainz token |
| `POST /api/admin/scrobbling/listenbrainz/test` | Test ListenBrainz connection |

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