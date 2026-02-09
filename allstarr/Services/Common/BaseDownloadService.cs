using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Local;
using allstarr.Services.Subsonic;
using System.Collections.Concurrent;
using TagLib;
using IOFile = System.IO.File;

namespace allstarr.Services.Common;

/// <summary>
/// Abstract base class for download services.
/// Implements common download logic, tracking, and metadata writing.
/// Subclasses implement provider-specific download and authentication logic.
/// </summary>
public abstract class BaseDownloadService : IDownloadService
{
    protected readonly IConfiguration Configuration;
    protected readonly ILocalLibraryService LocalLibraryService;
    protected readonly IMusicMetadataService MetadataService;
    protected readonly SubsonicSettings SubsonicSettings;
    protected readonly ILogger Logger;
    private readonly IServiceProvider _serviceProvider;
    
    protected readonly string DownloadPath;
    protected readonly string CachePath;
    
    protected readonly ConcurrentDictionary<string, DownloadInfo> ActiveDownloads = new();
    protected readonly SemaphoreSlim DownloadLock = new(1, 1);
    
    // Rate limiting fields
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly int _minRequestIntervalMs = 200;
    
    /// <summary>
    /// Lazy-loaded PlaylistSyncService to avoid circular dependency
    /// </summary>
    private PlaylistSyncService? _playlistSyncService;
    protected PlaylistSyncService? PlaylistSyncService
    {
        get
        {
            if (_playlistSyncService == null)
            {
                _playlistSyncService = _serviceProvider.GetService<PlaylistSyncService>();
            }
            return _playlistSyncService;
        }
    }
    
    /// <summary>
    /// Provider name (e.g., "deezer", "qobuz")
    /// </summary>
    protected abstract string ProviderName { get; }
    
    protected BaseDownloadService(
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        SubsonicSettings subsonicSettings,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        Configuration = configuration;
        LocalLibraryService = localLibraryService;
        MetadataService = metadataService;
        SubsonicSettings = subsonicSettings;
        _serviceProvider = serviceProvider;
        Logger = logger;
        
        DownloadPath = configuration["Library:DownloadPath"] ?? "./downloads";
        CachePath = PathHelper.GetCachePath();
        
        if (!Directory.Exists(DownloadPath))
        {
            Directory.CreateDirectory(DownloadPath);
        }
        
        if (!Directory.Exists(CachePath))
        {
            Directory.CreateDirectory(CachePath);
        }
    }
    
    #region IDownloadService Implementation
    
    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
    }
    
    public async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        
        // Check if already downloaded locally
        var localPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (localPath != null && IOFile.Exists(localPath))
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogInformation("Streaming from local cache ({ElapsedMs}ms): {Path}", elapsed, localPath);
            
            // Update access time for cache cleanup
            if (SubsonicSettings.StorageMode == StorageMode.Cache)
            {
                IOFile.SetLastAccessTime(localPath, DateTime.UtcNow);
            }
            
            // Start background Odesli conversion for lyrics (if not already cached)
            StartBackgroundOdesliConversion(externalProvider, externalId);
            
            return IOFile.OpenRead(localPath);
        }

        // Download to disk first to ensure complete file with metadata
        // This is necessary because:
        // 1. Clients may seek to arbitrary positions (requires full file)
        // 2. Metadata embedding requires complete file
        // 3. Caching for future plays
        Logger.LogInformation("Downloading song for streaming: {Provider}:{ExternalId}", externalProvider, externalId);
        
        try
        {
            localPath = await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogInformation("Download completed, starting stream ({ElapsedMs}ms total): {Path}", elapsed, localPath);
            
            // Start background Odesli conversion for lyrics (after stream starts)
            StartBackgroundOdesliConversion(externalProvider, externalId);
            
            return IOFile.OpenRead(localPath);
        }
        catch (OperationCanceledException)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogWarning("Download cancelled by client after {ElapsedMs}ms for {Provider}:{ExternalId}", elapsed, externalProvider, externalId);
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogError(ex, "Download failed after {ElapsedMs}ms for {Provider}:{ExternalId}", elapsed, externalProvider, externalId);
            throw;
        }
    }
    
    /// <summary>
    /// Starts background Odesli conversion for lyrics support.
    /// This is called AFTER streaming starts so it doesn't block the client.
    /// </summary>
    private void StartBackgroundOdesliConversion(string externalProvider, string externalId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Provider-specific conversion (override in subclasses if needed)
                await ConvertToSpotifyIdAsync(externalProvider, externalId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Background Spotify ID conversion failed for {Provider}:{ExternalId}", externalProvider, externalId);
            }
        });
    }
    
    /// <summary>
    /// Converts external track ID to Spotify ID for lyrics support.
    /// Override in provider-specific services if needed.
    /// </summary>
    protected virtual Task ConvertToSpotifyIdAsync(string externalProvider, string externalId)
    {
        // Default implementation does nothing
        // Provider-specific services can override this
        return Task.CompletedTask;
    }
    
    public DownloadInfo? GetDownloadStatus(string songId)
    {
        ActiveDownloads.TryGetValue(songId, out var info);
        return info;
    }
    
    public async Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName)
        {
            return null;
        }
        
        // Check local library (works for both cache and permanent storage)
        var localPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (localPath != null && IOFile.Exists(localPath))
        {
            return localPath;
        }
        
        return null;
    }
    
    public abstract Task<bool> IsAvailableAsync();
    
    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId)
    {
        if (externalProvider != ProviderName)
        {
            Logger.LogWarning("Provider '{Provider}' is not supported for album download", externalProvider);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadRemainingAlbumTracksAsync(albumExternalId, excludeTrackExternalId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to download remaining album tracks for album {AlbumId}", albumExternalId);
            }
        });
    }
    
    #endregion
    
    #region Template Methods (to be implemented by subclasses)
    
    /// <summary>
    /// Downloads a track and saves it to disk.
    /// Subclasses implement provider-specific logic (encryption, authentication, etc.)
    /// </summary>
    /// <param name="trackId">External track ID</param>
    /// <param name="song">Song metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Local file path where the track was saved</returns>
    protected abstract Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken);
    
    /// <summary>
    /// Extracts the external album ID from the internal album ID format.
    /// Example: "ext-deezer-album-123456" -> "123456"
    /// </summary>
    protected abstract string? ExtractExternalIdFromAlbumId(string albumId);
    
    #endregion
    
    #region Common Download Logic
    
    /// <summary>
    /// Internal method for downloading a song with control over album download triggering
    /// </summary>
    protected async Task<string> DownloadSongInternalAsync(string externalProvider, string externalId, bool triggerAlbumDownload, CancellationToken cancellationToken = default)
    {
        if (externalProvider != ProviderName)
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = $"ext-{externalProvider}-{externalId}";
        var isCache = SubsonicSettings.StorageMode == StorageMode.Cache;
        
        // Acquire lock BEFORE checking existence to prevent race conditions with concurrent requests
        await DownloadLock.WaitAsync(cancellationToken);
        var lockHeld = true;
        
        try
        {
            // Check if already downloaded (works for both cache and permanent modes)
            var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
            if (existingPath != null && IOFile.Exists(existingPath))
            {
                Logger.LogInformation("Song already downloaded: {Path}", existingPath);
                
                // For cache mode, update file access time for cache cleanup logic
                if (isCache)
                {
                    IOFile.SetLastAccessTime(existingPath, DateTime.UtcNow);
                }
                
                return existingPath;
            }

            // Check if download in progress
            if (ActiveDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
            {
                Logger.LogDebug("Download already in progress for {SongId}, waiting for completion...", songId);
                // Release lock while waiting
                DownloadLock.Release();
                lockHeld = false;
                
                // Wait for download to complete, checking every 100ms (faster than 500ms)
                // Also respect cancellation token so client timeouts are handled immediately
                while (ActiveDownloads.TryGetValue(songId, out activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(100, cancellationToken);
                }
                
                if (activeDownload?.Status == DownloadStatus.Completed && activeDownload.LocalPath != null)
                {
                    Logger.LogDebug("Download completed while waiting, returning path: {Path}", activeDownload.LocalPath);
                    return activeDownload.LocalPath;
                }
                
                // Download failed or was cancelled
                throw new Exception(activeDownload?.ErrorMessage ?? "Download failed while waiting");
            }

            // Get metadata
            // In Album mode, fetch the full album first to ensure AlbumArtist is correctly set
            Song? song = null;
            
            if (SubsonicSettings.DownloadMode == DownloadMode.Album)
            {
                // First try to get the song to extract album ID
                var tempSong = await MetadataService.GetSongAsync(externalProvider, externalId);
                if (tempSong != null && !string.IsNullOrEmpty(tempSong.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(tempSong.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        // Get full album with correct AlbumArtist
                        var album = await MetadataService.GetAlbumAsync(externalProvider, albumExternalId);
                        if (album != null)
                        {
                            // Find the track in the album
                            song = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);
                        }
                    }
                }
            }
            
            // Fallback to individual song fetch if not in Album mode or album fetch failed
            if (song == null)
            {
                song = await MetadataService.GetSongAsync(externalProvider, externalId);
            }
            
            if (song == null)
            {
                throw new Exception("Song not found");
            }

            var downloadInfo = new DownloadInfo
            {
                SongId = songId,
                ExternalId = externalId,
                ExternalProvider = externalProvider,
                Status = DownloadStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };
            ActiveDownloads[songId] = downloadInfo;

            var localPath = await DownloadTrackAsync(externalId, song, cancellationToken);
            
            downloadInfo.Status = DownloadStatus.Completed;
            downloadInfo.LocalPath = localPath;
            downloadInfo.CompletedAt = DateTime.UtcNow;
            
            song.LocalPath = localPath;
            
            // Clean up completed download from tracking after a short delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5)); // Keep for 5 minutes for status checks
                ActiveDownloads.TryRemove(songId, out _);
                Logger.LogDebug("Cleaned up completed download tracking for {SongId}", songId);
            });
            
            // Register BEFORE releasing lock to prevent race conditions (both cache and download modes)
            await LocalLibraryService.RegisterDownloadedSongAsync(song, localPath);
            
            // Check if this track belongs to a playlist and update M3U
            if (PlaylistSyncService != null)
            {
                try
                {
                    var playlistId = PlaylistSyncService.GetPlaylistIdForTrack(songId);
                    if (playlistId != null)
                    {
                        Logger.LogInformation("Track {SongId} belongs to playlist {PlaylistId}, adding to M3U", songId, playlistId);
                        await PlaylistSyncService.AddTrackToM3UAsync(playlistId, song, localPath, isFullPlaylistDownload: false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to update playlist M3U for track {SongId}", songId);
                }
            }
            
            // Trigger library scan and album download AFTER releasing lock (download mode only)
            if (!isCache)
            {
                // Trigger a Subsonic library rescan (with debounce)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LocalLibraryService.TriggerLibraryScanAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to trigger library scan after download");
                    }
                });
                
                // If download mode is Album and triggering is enabled, start background download of remaining tracks
                if (triggerAlbumDownload && SubsonicSettings.DownloadMode == DownloadMode.Album && !string.IsNullOrEmpty(song.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        Logger.LogInformation("Download mode is Album, triggering background download for album {AlbumId}", albumExternalId);
                        DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, externalId);
                    }
                }
            }
            else
            {
                Logger.LogInformation("Cache mode: skipping library registration and scan");
            }
            
            Logger.LogInformation("Download completed: {Path}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            if (ActiveDownloads.TryGetValue(songId, out var downloadInfo))
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;
                
                // Clean up failed download from tracking after a short delay
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(2)); // Keep for 2 minutes for error reporting
                    ActiveDownloads.TryRemove(songId, out _);
                    Logger.LogDebug("Cleaned up failed download tracking for {SongId}", songId);
                });
            }
            Logger.LogError(ex, "Download failed for {SongId}", songId);
            throw;
        }
        finally
        {
            if (lockHeld)
            {
                DownloadLock.Release();
            }
        }
    }
    
    protected async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId)
    {
        Logger.LogInformation("Starting background download for album {AlbumId} (excluding track {TrackId})", 
            albumExternalId, excludeTrackExternalId);

        var album = await MetadataService.GetAlbumAsync(ProviderName, albumExternalId);
        if (album == null)
        {
            Logger.LogWarning("Album {AlbumId} not found, cannot download remaining tracks", albumExternalId);
            return;
        }

        var tracksToDownload = album.Songs
            .Where(s => s.ExternalId != excludeTrackExternalId && !string.IsNullOrEmpty(s.ExternalId))
            .ToList();

        Logger.LogInformation("Found {Count} additional tracks to download for album '{AlbumTitle}'", 
            tracksToDownload.Count, album.Title);

        foreach (var track in tracksToDownload)
        {
            try
            {
                var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(ProviderName, track.ExternalId!);
                if (existingPath != null && IOFile.Exists(existingPath))
                {
                    Logger.LogDebug("Track {TrackId} already downloaded, skipping", track.ExternalId);
                    continue;
                }

                // Check if download is already in progress or recently completed
                var songId = $"ext-{ProviderName}-{track.ExternalId}";
                if (ActiveDownloads.TryGetValue(songId, out var activeDownload))
                {
                    if (activeDownload.Status == DownloadStatus.InProgress)
                    {
                        Logger.LogDebug("Track {TrackId} download already in progress, skipping", track.ExternalId);
                        continue;
                    }
                    
                    if (activeDownload.Status == DownloadStatus.Completed)
                    {
                        Logger.LogDebug("Track {TrackId} already downloaded in this session, skipping", track.ExternalId);
                        continue;
                    }
                }

                Logger.LogInformation("Downloading track '{Title}' from album '{Album}'", track.Title, album.Title);
                await DownloadSongInternalAsync(ProviderName, track.ExternalId!, triggerAlbumDownload: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to download track {TrackId} '{Title}'", track.ExternalId, track.Title);
            }
        }

        Logger.LogInformation("Completed background download for album '{AlbumTitle}'", album.Title);
    }
    
    #endregion
    
    #region Common Metadata Writing
    
    /// <summary>
    /// Writes ID3/Vorbis metadata and cover art to the audio file
    /// </summary>
    protected async Task WriteMetadataAsync(string filePath, Song song, CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Writing metadata to: {Path}", filePath);
            
            using var tagFile = TagLib.File.Create(filePath);
            
            // Basic metadata
            tagFile.Tag.Title = song.Title;
            tagFile.Tag.Performers = new[] { song.Artist };
            tagFile.Tag.Album = song.Album;
            tagFile.Tag.AlbumArtists = new[] { !string.IsNullOrEmpty(song.AlbumArtist) ? song.AlbumArtist : song.Artist };
            
            if (song.Track.HasValue)
                tagFile.Tag.Track = (uint)song.Track.Value;
            
            if (song.TotalTracks.HasValue)
                tagFile.Tag.TrackCount = (uint)song.TotalTracks.Value;
            
            if (song.DiscNumber.HasValue)
                tagFile.Tag.Disc = (uint)song.DiscNumber.Value;
            
            if (song.Year.HasValue)
                tagFile.Tag.Year = (uint)song.Year.Value;
            
            if (!string.IsNullOrEmpty(song.Genre))
                tagFile.Tag.Genres = new[] { song.Genre };
            
            if (song.Bpm.HasValue)
                tagFile.Tag.BeatsPerMinute = (uint)song.Bpm.Value;
            
            if (song.Contributors.Count > 0)
                tagFile.Tag.Composers = song.Contributors.ToArray();
            
            if (!string.IsNullOrEmpty(song.Copyright))
                tagFile.Tag.Copyright = song.Copyright;
            
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(song.Isrc))
                comments.Add($"ISRC: {song.Isrc}");
            
            if (comments.Count > 0)
                tagFile.Tag.Comment = string.Join(" | ", comments);
            
            // Download and embed cover art
            var coverUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
            if (!string.IsNullOrEmpty(coverUrl))
            {
                try
                {
                    var coverData = await DownloadCoverArtAsync(coverUrl, cancellationToken);
                    if (coverData != null && coverData.Length > 0)
                    {
                        var mimeType = coverUrl.Contains(".png") ? "image/png" : "image/jpeg";
                        var picture = new TagLib.Picture
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = mimeType,
                            Description = "Cover",
                            Data = new TagLib.ByteVector(coverData)
                        };
                        tagFile.Tag.Pictures = new TagLib.IPicture[] { picture };
                        Logger.LogInformation("Cover art embedded: {Size} bytes", coverData.Length);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to download cover art from {Url}", coverUrl);
                }
            }
            
            tagFile.Save();
            Logger.LogInformation("Metadata written successfully to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to write metadata to: {Path}", filePath);
        }
    }
    
    /// <summary>
    /// Downloads cover art from a URL
    /// </summary>
    protected async Task<byte[]?> DownloadCoverArtAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to download cover art from {Url}", url);
            return null;
        }
    }
    
    #endregion
    
    #region Utility Methods
    
    /// <summary>
    /// Ensures a directory exists, creating it and all parent directories if necessary
    /// </summary>
    protected void EnsureDirectoryExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                Logger.LogDebug("Created directory: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create directory: {Path}", path);
            throw;
        }
    }
    
    
    #endregion
    
    #region Rate Limiting
    
    /// <summary>
    /// Queues a request with rate limiting to prevent overwhelming the API.
    /// Ensures minimum interval between requests.
    /// </summary>
    protected async Task<T> QueueRequestAsync<T>(Func<Task<T>> action)
    {
        await _requestLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var timeSinceLastRequest = (now - _lastRequestTime).TotalMilliseconds;
            
            if (timeSinceLastRequest < _minRequestIntervalMs)
            {
                await Task.Delay((int)(_minRequestIntervalMs - timeSinceLastRequest));
            }

            _lastRequestTime = DateTime.UtcNow;
            return await action();
        }
        finally
        {
            _requestLock.Release();
        }
    }
    
    #endregion
}
