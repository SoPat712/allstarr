using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Local;
using allstarr.Services.Subsonic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TagLib;
using IOFile = System.IO.File;

namespace allstarr.Services.Common;

public abstract class BaseDownloadService : IConcreteDownloadService
{
    private const int MaximumTrackedDownloads = 256;
    private static readonly TimeSpan CompletedDownloadRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedDownloadRetention = TimeSpan.FromMinutes(2);

    protected readonly IConfiguration Configuration;
    protected readonly ILocalLibraryService LocalLibraryService;
    protected readonly IMusicMetadataService MetadataService;
    protected readonly SubsonicSettings SubsonicSettings;
    protected readonly ILogger Logger;
    private readonly IServiceProvider _serviceProvider;

    protected readonly string DownloadPath;
    protected readonly string CachePath;

    protected readonly ConcurrentDictionary<string, DownloadInfo> ActiveDownloads = new();

    protected readonly SemaphoreSlim _stateSemaphore = new(1, 1);
    protected readonly SemaphoreSlim _concurrencySemaphore;

    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    protected int _minRequestIntervalMs = 200;

    protected StorageMode CurrentStorageMode => BackendSetting("StorageMode", StorageMode.Permanent);
    protected DownloadMode CurrentDownloadMode => BackendSetting("DownloadMode", DownloadMode.Track);

    protected abstract string ProviderName { get; }
    public string ProviderId => ProviderName;

    // Download and metadata capabilities can have different runtime IDs.
    protected virtual string MetadataProviderName => ProviderName;

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

        Directory.CreateDirectory(DownloadPath);
        Directory.CreateDirectory(CachePath);

        var maxDownloadsStr = configuration["MAX_CONCURRENT_DOWNLOADS"];
        if (!int.TryParse(maxDownloadsStr, out var maxDownloads) || maxDownloads <= 0)
        {
            maxDownloads = 3;
        }
        _concurrencySemaphore = new SemaphoreSlim(maxDownloads, maxDownloads);
    }

    public Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        DownloadSongInternalAsync(
            externalProvider,
            externalId,
            triggerAlbumDownload: true,
            requestedForStreaming: false,
            cancellationToken);


    public virtual async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, StreamQuality? qualityOverride = null, CancellationToken cancellationToken = default)
    {
        if (qualityOverride.HasValue && qualityOverride.Value != StreamQuality.Original)
        {
            return await DownloadAndStreamWithQualityOverrideAsync(externalProvider, externalId, qualityOverride.Value, cancellationToken);
        }

        var startTime = DateTime.UtcNow;

        var localPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (localPath != null && IOFile.Exists(localPath))
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogInformation("Streaming from local cache ({ElapsedMs}ms): {Path}", elapsed, localPath);

            if (CurrentStorageMode == StorageMode.Cache)
            {
                IOFile.SetLastWriteTime(localPath, DateTime.UtcNow);
            }

            StartBackgroundOdesliConversion(externalProvider, externalId);

            return IOFile.OpenRead(localPath);
        }

        // Seeking, metadata embedding, and reuse require a complete local artifact.
        Logger.LogInformation("Downloading song for streaming: {Provider}:{ExternalId}", externalProvider, externalId);

        try
        {
            // A disconnected client must not abandon the shared server-side artifact.
            localPath = await DownloadSongInternalAsync(
                externalProvider,
                externalId,
                triggerAlbumDownload: true,
                requestedForStreaming: true,
                CancellationToken.None);
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogInformation("Download completed, starting stream ({ElapsedMs}ms total): {Path}", elapsed, localPath);

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

    // Quality overrides use the short-lived transcoded cache without replacing the canonical copy.
    private async Task<Stream> DownloadAndStreamWithQualityOverrideAsync(
        string externalProvider, string externalId, StreamQuality quality, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        Logger.LogInformation(
            "Streaming with quality override {Quality} for {Provider}:{ExternalId}",
            quality, externalProvider, externalId);

        try
        {
            var song = await MetadataService.GetSongAsync(MetadataProviderName, externalId);
            if (song == null)
            {
                throw new Exception("Song not found");
            }

            var tempPath = await DownloadTrackWithQualityAsync(externalId, song, quality, CancellationToken.None);
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogInformation(
                "Quality-override download completed ({Quality}, {ElapsedMs}ms): {Path}",
                quality, elapsed, tempPath);
            IOFile.SetLastWriteTime(tempPath, DateTime.UtcNow);

            StartBackgroundOdesliConversion(externalProvider, externalId);

            return IOFile.OpenRead(tempPath);
        }
        catch (OperationCanceledException)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogWarning(
                "Quality-override download cancelled after {ElapsedMs}ms for {Provider}:{ExternalId}",
                elapsed, externalProvider, externalId);
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogError(ex,
                "Quality-override download failed after {ElapsedMs}ms for {Provider}:{ExternalId}",
                elapsed, externalProvider, externalId);
            throw;
        }
    }


    private void StartBackgroundOdesliConversion(string externalProvider, string externalId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ConvertToSpotifyIdAsync(externalProvider, externalId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Background Spotify ID conversion failed for {Provider}:{ExternalId}", externalProvider, externalId);
            }
        });
    }

    protected virtual Task ConvertToSpotifyIdAsync(string externalProvider, string externalId) =>
        Task.CompletedTask;

    public DownloadInfo? GetDownloadStatus(string songId)
    {
        PruneDownloadHistory();
        ActiveDownloads.TryGetValue(songId, out var info);
        return info;
    }

    public IReadOnlyList<DownloadInfo> GetActiveDownloads()
    {
        PruneDownloadHistory();
        return ActiveDownloads.Values.ToList().AsReadOnly();
    }

    private void PruneDownloadHistory()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ActiveDownloads)
        {
            var download = entry.Value;
            if (download.Status == DownloadStatus.InProgress)
            {
                continue;
            }

            var terminalAt = download.CompletedAt ?? download.StartedAt;
            var retention = download.Status == DownloadStatus.Completed
                ? CompletedDownloadRetention
                : FailedDownloadRetention;
            if (now - terminalAt >= retention)
            {
                ActiveDownloads.TryRemove(entry.Key, out _);
            }
        }

        var excess = ActiveDownloads.Count - MaximumTrackedDownloads;
        if (excess <= 0)
        {
            return;
        }

        foreach (var entry in ActiveDownloads
                     .Where(item => item.Value.Status != DownloadStatus.InProgress)
                     .OrderBy(item => item.Value.CompletedAt ?? item.Value.StartedAt)
                     .Take(excess))
        {
            ActiveDownloads.TryRemove(entry.Key, out _);
        }
    }

    public async Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName)
        {
            return null;
        }

        var localPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        return localPath != null && IOFile.Exists(localPath) ? localPath : null;
    }

    public abstract Task<bool> IsAvailableAsync();

    protected string BuildTrackedSongId(string externalId) => BuildTrackedSongId(ProviderName, externalId);

    protected static string BuildTrackedSongId(string externalProvider, string externalId)
    {
        return $"ext-{externalProvider}-song-{externalId}";
    }

    protected void SetDownloadProgress(string songId, double progress)
    {
        if (ActiveDownloads.TryGetValue(songId, out var info))
        {
            info.Progress = Math.Clamp(progress, 0d, 1d);
        }
    }

    private void StartRemainingAlbumDownload(string albumExternalId, string excludeTrackExternalId)
    {
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

    protected abstract Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken);

    protected virtual Task<string> DownloadTrackWithQualityAsync(string trackId, Song song, StreamQuality quality, CancellationToken cancellationToken)
    {
        return DownloadTrackAsync(trackId, song, cancellationToken);
    }

    protected virtual string? ExtractExternalIdFromAlbumId(string albumId)
    {
        var prefix = $"ext-{ProviderName}-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }

    protected async Task<string> DownloadSongInternalAsync(
        string externalProvider,
        string externalId,
        bool triggerAlbumDownload,
        bool requestedForStreaming = false,
        CancellationToken cancellationToken = default)
    {
        if (externalProvider != ProviderName)
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = BuildTrackedSongId(externalProvider, externalId);
        var isCache = CurrentStorageMode == StorageMode.Cache;

        bool isInitiator = false;
        PruneDownloadHistory();

        await _stateSemaphore.WaitAsync(cancellationToken);
        try
        {
            var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
            if (existingPath != null && IOFile.Exists(existingPath))
            {
                Logger.LogInformation("Song already downloaded: {Path}", existingPath);

                if (isCache)
                {
                    IOFile.SetLastWriteTime(existingPath, DateTime.UtcNow);
                }

                return existingPath;
            }

            if (ActiveDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
            {
                if (requestedForStreaming)
                {
                    activeDownload.RequestedForStreaming = true;
                }

                Logger.LogDebug("Download already in progress for {SongId}, waiting for completion...", songId);
            }
            else
            {
                isInitiator = true;
                ActiveDownloads[songId] = new DownloadInfo
                {
                    SongId = songId,
                    ExternalId = externalId,
                    ExternalProvider = externalProvider,
                    Title = "Unknown Title",
                    Artist = "Unknown Artist",
                    Status = DownloadStatus.InProgress,
                    Progress = 0,
                    RequestedForStreaming = requestedForStreaming,
                    StartedAt = DateTime.UtcNow
                };
            }
        }
        finally
        {
            _stateSemaphore.Release();
        }

        if (!isInitiator)
        {
            DownloadInfo? activeDownload;
            while (ActiveDownloads.TryGetValue(songId, out activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogInformation("Client cancelled while waiting for download {SongId}, but download continues server-side", songId);
                    throw new OperationCanceledException("Client cancelled request, but download continues server-side");
                }
                await Task.Delay(100, CancellationToken.None);
            }

            if (activeDownload?.Status == DownloadStatus.Completed && activeDownload.LocalPath != null)
            {
                Logger.LogDebug("Download completed while waiting, returning path: {Path}", activeDownload.LocalPath);
                return activeDownload.LocalPath;
            }

            throw new Exception(activeDownload?.ErrorMessage ?? "Download failed while waiting");
        }

        await _concurrencySemaphore.WaitAsync(cancellationToken);
        try
        {
            // Album metadata supplies the canonical AlbumArtist for every track.
            Song? song = null;

            if (CurrentDownloadMode == DownloadMode.Album)
            {
                var tempSong = await MetadataService.GetSongAsync(MetadataProviderName, externalId);
                if (tempSong != null && !string.IsNullOrEmpty(tempSong.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(tempSong.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        var album = await MetadataService.GetAlbumAsync(MetadataProviderName, albumExternalId);
                        if (album != null)
                        {
                            song = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);
                        }
                    }
                }
            }

            if (song == null)
            {
                song = await MetadataService.GetSongAsync(MetadataProviderName, externalId);
            }

            if (song == null)
            {
                throw new Exception("Song not found");
            }

            if (ActiveDownloads.TryGetValue(songId, out var info))
            {
                info.Title = song.Title ?? "Unknown Title";
                info.Artist = song.Artist ?? "Unknown Artist";
                info.DurationSeconds = song.Duration;
                info.CoverArtUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
            }

            var localPath = await DownloadTrackAsync(externalId, song, cancellationToken);

            if (ActiveDownloads.TryGetValue(songId, out var successInfo))
            {
                successInfo.Status = DownloadStatus.Completed;
                successInfo.Progress = 1.0;
                successInfo.LocalPath = localPath;
                successInfo.CompletedAt = DateTime.UtcNow;
            }

            song.LocalPath = localPath;
            PruneDownloadHistory();

            await LocalLibraryService.RegisterDownloadedSongAsync(song, localPath);

            if (!isCache)
            {
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

                if (triggerAlbumDownload && CurrentDownloadMode == DownloadMode.Album && !string.IsNullOrEmpty(song.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        Logger.LogInformation("Download mode is Album, triggering background download for album {AlbumId}", albumExternalId);
                        StartRemainingAlbumDownload(albumExternalId, externalId);
                    }
                }
            }
            else
            {
                Logger.LogInformation("Cache mode: skipping backend library scan");
            }

            Logger.LogInformation("Download completed: {Path}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            if (ActiveDownloads.TryGetValue(songId, out var downloadInfo))
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = SafeDownloadError(ex);
                downloadInfo.CompletedAt = DateTime.UtcNow;
            }
            PruneDownloadHistory();

            if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
            {
                Logger.LogError("Download failed for {SongId}: {StatusCode}: {ReasonPhrase}",
                    songId, (int)httpRequestException.StatusCode.Value, httpRequestException.StatusCode.Value);
                Logger.LogDebug(ex, "Detailed download failure for {SongId}", songId);
            }
            else
            {
                Logger.LogError(ex, "Download failed for {SongId}", songId);
            }
            throw;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    protected async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId)
    {
        Logger.LogInformation("Starting background download for album {AlbumId} (excluding track {TrackId})",
            albumExternalId, excludeTrackExternalId);

        var album = await MetadataService.GetAlbumAsync(MetadataProviderName, albumExternalId);
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

                var songId = BuildTrackedSongId(track.ExternalId!);
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
                await DownloadSongInternalAsync(
                    ProviderName,
                    track.ExternalId!,
                    triggerAlbumDownload: false,
                    requestedForStreaming: false,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to download track {TrackId} '{Title}'", track.ExternalId, track.Title);
            }
        }

        Logger.LogInformation("Completed background download for album '{AlbumTitle}'", album.Title);
    }

    protected async Task WriteMetadataAsync(string filePath, Song song, CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Writing metadata to: {Path}", filePath);

            using var tagFile = TagLib.File.Create(filePath);

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

    protected async Task<byte[]?> DownloadCoverArtAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var artworkCache = Path.Combine(CachePath, "artwork");
            Directory.CreateDirectory(artworkCache);
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            var cacheFile = Path.Combine(artworkCache, $"{cacheKey}.image");
            if (IOFile.Exists(cacheFile))
            {
                return await IOFile.ReadAllBytesAsync(cacheFile, cancellationToken);
            }

            using var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (data.Length > 0)
            {
                var temporary = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
                await IOFile.WriteAllBytesAsync(temporary, data, cancellationToken);
                IOFile.Move(temporary, cacheFile, overwrite: true);
            }
            return data;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to download cover art from {Url}", url);
            return null;
        }
    }

    protected static void EnsureDirectoryExists(string path) => Directory.CreateDirectory(path);

    protected async Task<T> QueueRequestAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var timeSinceLastRequest = (now - _lastRequestTime).TotalMilliseconds;

            if (timeSinceLastRequest < _minRequestIntervalMs)
            {
                await Task.Delay((int)(_minRequestIntervalMs - timeSinceLastRequest), cancellationToken);
            }

            _lastRequestTime = DateTime.UtcNow;
            return await action();
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private T BackendSetting<T>(string key, T fallback) where T : struct, Enum
    {
        var backend = Configuration["Backend:Type"]?.Equals(
            "Jellyfin", StringComparison.OrdinalIgnoreCase) == true
                ? "Jellyfin"
                : "Subsonic";
        var configured = Configuration[$"{backend}:{key}"] ??
                         Configuration[$"Subsonic:{key}"];
        return Enum.TryParse<T>(configured, ignoreCase: true, out var value) ? value : fallback;
    }

    private static string SafeDownloadError(Exception exception) => exception switch
    {
        OperationCanceledException => "Download canceled.",
        HttpRequestException { StatusCode: { } status } =>
            $"Provider download request failed with HTTP {(int)status}.",
        HttpRequestException => "The provider could not be reached.",
        IOException => "The downloaded file could not be written.",
        InvalidDataException => "The provider returned an invalid download.",
        _ => "Download failed."
    };
}
