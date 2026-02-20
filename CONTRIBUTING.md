# Contributing to Allstarr

We welcome contributions! Here's how to get started:

## Development Setup

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

## Making Changes

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Run tests to ensure everything works
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to your fork (`git push origin feature/amazing-feature`)
7. Open a Pull Request

## Code Style

- Follow existing code patterns and conventions
- Add tests for new features
- Update documentation as needed
- Keep commits feature focused

## Testing

All changes should include appropriate tests:
```bash
# Run all tests
dotnet test

# Run specific test file
dotnet test --filter "FullyQualifiedName~SubsonicProxyServiceTests"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Build

```bash
dotnet build
```

## Run Tests

```bash
dotnet test
```

## Project Structure

```
allstarr/
├── Controllers/
│   ├── AdminController.cs                 # Admin health check
│   ├── ConfigController.cs                # Configuration management
│   ├── DiagnosticsController.cs           # System diagnostics & debugging
│   ├── DownloadsController.cs             # Download management
│   ├── JellyfinAdminController.cs         # Jellyfin admin operations
│   ├── JellyfinController.cs              # Jellyfin API proxy
│   ├── LyricsController.cs                # Lyrics management
│   ├── MappingController.cs               # Track mapping management
│   ├── PlaylistController.cs              # Playlist operations & CRUD
│   ├── ScrobblingAdminController.cs       # Scrobbling configuration
│   ├── SpotifyAdminController.cs          # Spotify admin operations
│   └── SubSonicController.cs              # Subsonic API proxy
├── Filters/
│   ├── AdminPortFilter.cs                 # Admin port access control
│   ├── ApiKeyAuthFilter.cs                # API key authentication
│   └── JellyfinAuthFilter.cs              # Jellyfin authentication
├── Middleware/
│   ├── AdminStaticFilesMiddleware.cs      # Admin UI static file serving
│   ├── GlobalExceptionHandler.cs          # Global error handling
│   └── WebSocketProxyMiddleware.cs        # WebSocket proxying for Jellyfin
├── Models/
│   ├── Admin/                             # Admin request/response models
│   │   └── AdminDtos.cs
│   ├── Domain/                            # Domain entities
│   │   ├── Album.cs
│   │   ├── Artist.cs
│   │   └── Song.cs
│   ├── Download/                          # Download-related models
│   │   ├── DownloadInfo.cs
│   │   └── DownloadStatus.cs
│   ├── Lyrics/
│   │   └── LyricsInfo.cs
│   ├── Scrobbling/                        # Scrobbling models
│   │   ├── PlaybackSession.cs
│   │   ├── ScrobbleResult.cs
│   │   └── ScrobbleTrack.cs
│   ├── Search/
│   │   └── SearchResult.cs
│   ├── Settings/                          # Configuration models
│   │   ├── CacheSettings.cs
│   │   ├── DeezerSettings.cs
│   │   ├── JellyfinSettings.cs
│   │   ├── MusicBrainzSettings.cs
│   │   ├── QobuzSettings.cs
│   │   ├── RedisSettings.cs
│   │   ├── ScrobblingSettings.cs
│   │   ├── SpotifyApiSettings.cs
│   │   ├── SpotifyImportSettings.cs
│   │   ├── SquidWTFSettings.cs
│   │   └── SubsonicSettings.cs
│   ├── Spotify/                           # Spotify-specific models
│   │   ├── MissingTrack.cs
│   │   ├── SpotifyPlaylistTrack.cs
│   │   └── SpotifyTrackMapping.cs
│   └── Subsonic/                          # Subsonic-specific models
│       ├── ExternalPlaylist.cs
│       └── ScanStatus.cs
├── Services/
│   ├── Admin/                             # Admin helper services
│   │   └── AdminHelperService.cs
│   ├── Common/                            # Shared utilities
│   │   ├── AuthHeaderHelper.cs            # Auth header handling
│   │   ├── BaseDownloadService.cs         # Template method base class
│   │   ├── CacheCleanupService.cs         # Cache cleanup background service
│   │   ├── CacheExtensions.cs             # Cache extension methods
│   │   ├── CacheKeyBuilder.cs             # Type-safe cache key generation
│   │   ├── CacheWarmingService.cs         # Startup cache warming
│   │   ├── EndpointBenchmarkService.cs    # Endpoint performance benchmarking
│   │   ├── EnvMigrationService.cs         # Environment migration utilities
│   │   ├── Error.cs                       # Error types
│   │   ├── ExplicitContentFilter.cs       # Explicit content filtering
│   │   ├── FuzzyMatcher.cs                # Fuzzy string matching
│   │   ├── GenreEnrichmentService.cs      # MusicBrainz genre enrichment
│   │   ├── OdesliService.cs               # Odesli/song.link conversion
│   │   ├── ParallelMetadataService.cs     # Parallel metadata fetching
│   │   ├── PathHelper.cs                  # Path utilities
│   │   ├── PlaylistIdHelper.cs            # Playlist ID helpers
│   │   ├── RedisCacheService.cs           # Redis caching
│   │   ├── RedisPersistenceService.cs     # Redis persistence monitoring
│   │   ├── Result.cs                      # Result<T> pattern
│   │   ├── RetryHelper.cs                 # Retry logic with exponential backoff
│   │   └── RoundRobinFallbackHelper.cs    # Load balancing and failover
│   ├── Deezer/                            # Deezer provider
│   │   ├── DeezerDownloadService.cs
│   │   ├── DeezerMetadataService.cs
│   │   └── DeezerStartupValidator.cs
│   ├── Jellyfin/                          # Jellyfin integration
│   │   ├── JellyfinModelMapper.cs         # Model mapping
│   │   ├── JellyfinProxyService.cs        # Request proxying
│   │   ├── JellyfinResponseBuilder.cs     # Response building
│   │   ├── JellyfinSessionManager.cs      # Session management
│   │   └── JellyfinStartupValidator.cs    # Startup validation
│   ├── Local/                             # Local library
│   │   ├── ILocalLibraryService.cs
│   │   └── LocalLibraryService.cs
│   ├── Lyrics/                            # Lyrics services
│   │   ├── LrclibService.cs               # LRCLIB lyrics
│   │   ├── LyricsOrchestrator.cs          # Lyrics orchestration
│   │   ├── LyricsPlusService.cs           # LyricsPlus multi-source
│   │   ├── LyricsPrefetchService.cs       # Background lyrics prefetching
│   │   ├── LyricsStartupValidator.cs      # Lyrics validation
│   │   └── SpotifyLyricsService.cs        # Spotify lyrics
│   ├── MusicBrainz/
│   │   └── MusicBrainzService.cs          # MusicBrainz metadata
│   ├── Qobuz/                             # Qobuz provider
│   │   ├── QobuzBundleService.cs
│   │   ├── QobuzDownloadService.cs
│   │   ├── QobuzMetadataService.cs
│   │   └── QobuzStartupValidator.cs
│   ├── Scrobbling/                        # Scrobbling services
│   │   ├── IScrobblingService.cs
│   │   ├── LastFmScrobblingService.cs
│   │   ├── ListenBrainzScrobblingService.cs
│   │   ├── ScrobblingHelper.cs
│   │   └── ScrobblingOrchestrator.cs
│   ├── Spotify/                           # Spotify integration
│   │   ├── SpotifyApiClient.cs            # Spotify API client
│   │   ├── SpotifyMappingMigrationService.cs # Mapping migration
│   │   ├── SpotifyMappingService.cs       # Mapping management
│   │   ├── SpotifyMappingValidationService.cs # Mapping validation
│   │   ├── SpotifyMissingTracksFetcher.cs # Missing tracks fetcher
│   │   ├── SpotifyPlaylistFetcher.cs      # Playlist fetcher
│   │   └── SpotifyTrackMatchingService.cs # Track matching
│   ├── SquidWTF/                          # SquidWTF provider
│   │   ├── SquidWTFDownloadService.cs
│   │   ├── SquidWTFMetadataService.cs
│   │   └── SquidWTFStartupValidator.cs
│   ├── Subsonic/                          # Subsonic API logic
│   │   ├── PlaylistSyncService.cs         # Playlist synchronization
│   │   ├── SubsonicModelMapper.cs         # Model mapping
│   │   ├── SubsonicProxyService.cs        # Request proxying
│   │   ├── SubsonicRequestParser.cs       # Request parsing
│   │   └── SubsonicResponseBuilder.cs     # Response building
│   ├── Validation/                        # Startup validation
│   │   ├── BaseStartupValidator.cs
│   │   ├── IStartupValidator.cs
│   │   ├── StartupValidationOrchestrator.cs
│   │   ├── SubsonicStartupValidator.cs
│   │   └── ValidationResult.cs
│   ├── IDownloadService.cs                # Download interface
│   ├── IMusicMetadataService.cs           # Metadata interface
│   └── StartupValidationService.cs
├── wwwroot/                               # Admin UI static files
│   ├── js/                                # JavaScript modules
│   ├── app.js                             # Main application logic
│   ├── index.html                         # Admin dashboard
│   ├── placeholder.png                    # Placeholder image
│   ├── spotify-mappings.html              # Spotify mappings UI
│   ├── spotify-mappings.js                # Spotify mappings logic
│   └── styles.css                         # Stylesheet
├── Program.cs                             # Application entry point
└── appsettings.json                       # Configuration

allstarr.Tests/
├── DeezerDownloadServiceTests.cs          # Deezer download tests
├── DeezerMetadataServiceTests.cs          # Deezer metadata tests
├── JellyfinResponseStructureTests.cs      # Jellyfin response tests
├── LocalLibraryServiceTests.cs            # Local library tests
├── QobuzDownloadServiceTests.cs           # Qobuz download tests
├── SubsonicModelMapperTests.cs            # Model mapping tests
├── SubsonicProxyServiceTests.cs           # Proxy service tests
├── SubsonicRequestParserTests.cs          # Request parser tests
└── SubsonicResponseBuilderTests.cs        # Response builder tests
```

## Dependencies

- **BouncyCastle.Cryptography** (v2.6.2) - Blowfish decryption for Deezer streams
- **Cronos** (v0.11.1) - Cron expression parsing for scheduled tasks
- **Microsoft.AspNetCore.OpenApi** (v9.0.4) - OpenAPI support
- **Otp.NET** (v1.4.1) - One-time password generation for Last.fm authentication
- **StackExchange.Redis** (v2.8.16) - Redis client for caching
- **Swashbuckle.AspNetCore** (v9.0.4) - Swagger/OpenAPI documentation
- **TagLibSharp** (v2.3.0) - ID3 tag and cover art embedding
- **xUnit** - Unit testing framework
- **Moq** - Mocking library for tests
- **FluentAssertions** - Fluent assertion library for tests
