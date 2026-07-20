using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Services.Local;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleDownload;
using IOFile = System.IO.File;
using System.Collections.Concurrent;

namespace allstarr.Services.AppleMusic;

public class AppleMusicDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly IAppleDownloadEndpointDiscovery _endpointDiscovery;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _streamingDownloads =
        new(StringComparer.Ordinal);

    protected override string ProviderName => "apple-download";
    protected override string MetadataProviderName => "applemusic";

    public AppleMusicDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IAppleDownloadEndpointDiscovery endpointDiscovery,
        IServiceProvider serviceProvider,
        ILogger<AppleMusicDownloadService> logger)
        : base(configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient("AppleMusic");
        _appleMusicSettings = appleMusicSettings.Value;
        _endpointDiscovery = endpointDiscovery;

        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _minRequestIntervalMs = 200;
    }

    public override async Task<bool> IsAvailableAsync()
    {
        var snapshot = await _endpointDiscovery.DiscoverAsync();
        return snapshot.State == AppleDownloadEndpointState.Available &&
               snapshot.Capability(ProviderCapabilities.Download).State ==
               AppleDownloadCapabilityState.Available;
    }

    public override async Task<Stream> DownloadAndStreamAsync(
        string externalProvider,
        string externalId,
        StreamQuality? qualityOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var existing = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (!string.IsNullOrWhiteSpace(existing) && IOFile.Exists(existing)) return IOFile.OpenRead(existing);

        var songId = BuildTrackedSongId(externalProvider, externalId);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_streamingDownloads.TryAdd(songId, completion))
        {
            var completedPath = await _streamingDownloads[songId].Task.WaitAsync(cancellationToken);
            return IOFile.OpenRead(completedPath);
        }

        string? temporaryPath = null;
        try
        {
            // Metadata is useful when the completed cache artifact is published, but
            // it must not sit on the cold playback path. Start it alongside the media
            // request and begin relaying the sidecar's FLAC bytes immediately.
            var metadataTask = MetadataService.GetSongAsync(MetadataProviderName, externalId, cancellationToken);
            _ = metadataTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var quality = qualityOverride switch
            {
                StreamQuality.High => AppleDownloadCapabilityAdapter.Quality(
                    ProviderAudioQuality.Lossy, _appleMusicSettings.Quality),
                StreamQuality.Low => AppleDownloadCapabilityAdapter.ApplyClientQuality(
                    "aac-96", _appleMusicSettings.Quality),
                _ => AppleDownloadCapabilityAdapter.Quality(
                    ProviderAudioQuality.Any, _appleMusicSettings.Quality)
            };

            if (!Uri.TryCreate(_appleMusicSettings.BaseUrl, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException("Apple download provider URL is not configured.");
            }

            var basePath = CurrentStorageMode == StorageMode.Cache
                ? Path.Combine(DownloadPath, "cache")
                : Path.Combine(DownloadPath, "permanent");
            var incomingPath = Path.Combine(basePath, ".incoming");
            EnsureDirectoryExists(incomingPath);
            temporaryPath = Path.Combine(incomingPath, $"apple-{Guid.NewGuid():N}.partial");

            var streamUrl = new Uri(
                baseUri,
                $"api/stream/{Uri.EscapeDataString(externalId)}?quality={Uri.EscapeDataString(quality)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "audio/flac";
            var upstream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var cache = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            var partialPath = temporaryPath;
            Logger.LogInformation(
                "Progressively streaming Apple Music track {TrackId} as {ContentType}",
                externalId,
                mediaType);

            return new ProgressiveCachingStream(
                upstream,
                cache,
                mediaType,
                async () =>
                {
                    try
                    {
                        response.Dispose();
                        var song = await ResolveStreamingMetadataAsync(metadataTask, externalId);
                        var finalPath = PathHelper.BuildTrackPath(
                            basePath,
                            song.AlbumArtist ?? song.Artist,
                            song.Album,
                            song.Title,
                            song.Track,
                            ".flac",
                            "applemusic",
                            externalId);
                        EnsureDirectoryExists(Path.GetDirectoryName(finalPath)!);
                        finalPath = PathHelper.ResolveUniquePath(finalPath);
                        IOFile.Move(partialPath, finalPath);
                        song.LocalPath = finalPath;
                        await LocalLibraryService.RegisterDownloadedSongAsync(song, finalPath);
                        SetDownloadProgress(songId, 1.0);
                        completion.TrySetResult(finalPath);
                        Logger.LogInformation(
                            "Apple Music progressive cache completed: {TrackId} -> {Path}",
                            externalId,
                            finalPath);
                    }
                    catch (Exception exception)
                    {
                        TryDeletePartial(partialPath);
                        completion.TrySetException(exception);
                        Logger.LogWarning(
                            exception,
                            "Apple Music played successfully but its cache artifact could not be published for {TrackId}",
                            externalId);
                    }
                    finally
                    {
                        _streamingDownloads.TryRemove(songId, out _);
                    }
                },
                () =>
                {
                    response.Dispose();
                    TryDeletePartial(partialPath);
                    completion.TrySetException(new IOException("Apple Music playback ended before the cache completed."));
                    _streamingDownloads.TryRemove(songId, out _);
                });
        }
        catch (Exception exception)
        {
            if (temporaryPath != null) TryDeletePartial(temporaryPath);
            completion.TrySetException(exception);
            _streamingDownloads.TryRemove(songId, out _);
            throw;
        }
    }

    private static async Task<Song> ResolveStreamingMetadataAsync(Task<Song?> metadataTask, string externalId)
    {
        try
        {
            var song = await metadataTask;
            if (song != null) return song;
        }
        catch
        {
            // Playback has already succeeded. Preserve a usable cache mapping even
            // when the optional metadata refresh failed independently.
        }

        return new Song
        {
            Id = $"ext-apple-download-song-{externalId}",
            Title = externalId,
            Artist = "Apple Music",
            Album = "Stream cache",
            ExternalProvider = "apple-download",
            ExternalId = externalId,
            IsLocal = false
        };
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (IOFile.Exists(path)) IOFile.Delete(path);
        }
        catch
        {
            // Cache cleanup will remove an abandoned partial artifact.
        }
    }

    protected override async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var quality = AppleDownloadCapabilityAdapter.Quality(
            ProviderAudioQuality.Any,
            _appleMusicSettings.Quality);
        return await DownloadTrackWithQualityInternalAsync(trackId, song, quality, cancellationToken);
    }

    protected override async Task<string> DownloadTrackWithQualityAsync(
        string trackId, Song song, StreamQuality quality, CancellationToken cancellationToken)
    {
        // Original playback uses the configured quality. Jellyfin bandwidth requests
        // are translated to an appropriate lower Apple tier when needed.
        var qualityStr = quality switch
        {
            StreamQuality.High => AppleDownloadCapabilityAdapter.Quality(
                ProviderAudioQuality.Lossy,
                _appleMusicSettings.Quality),
            StreamQuality.Low => AppleDownloadCapabilityAdapter.ApplyClientQuality(
                "aac-96",
                _appleMusicSettings.Quality),
            _ => AppleDownloadCapabilityAdapter.Quality(
                ProviderAudioQuality.Any,
                _appleMusicSettings.Quality)
        };

        return await DownloadTrackWithQualityInternalAsync(trackId, song, qualityStr, cancellationToken);
    }

    private async Task<string> DownloadTrackWithQualityInternalAsync(
        string trackId, Song song, string quality, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_appleMusicSettings.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Apple download provider URL is not configured.");
        }

        var songId = BuildTrackedSongId(trackId);
        var basePath = CurrentStorageMode == StorageMode.Cache
            ? Path.Combine(DownloadPath, "cache") : Path.Combine(DownloadPath, "permanent");

        var artistForPath = song.AlbumArtist ?? song.Artist;

        // The managed track-download route returns a FLAC artifact stream.
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, ".flac", "applemusic", trackId);
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);

        outputPath = PathHelper.ResolveUniquePath(outputPath);

        var streamUrl = new Uri(baseUri, $"api/download/{Uri.EscapeDataString(trackId)}?quality={Uri.EscapeDataString(quality)}");
        Logger.LogInformation("Downloading Apple Music track {TrackId} at quality {Quality} from sidecar...", trackId, quality);

        using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
        var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();

        await using var responseStream = await res.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        var totalBytes = res.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long totalBytesRead = 0;

        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0) break;

            await outputFile.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                SetDownloadProgress(songId, (double)totalBytesRead / totalBytes.Value);
            }
        }

        await outputFile.DisposeAsync();
        SetDownloadProgress(songId, 1.0);

        // Write tags and cover art
        await WriteMetadataAsync(outputPath, song, cancellationToken);
        Logger.LogInformation("Successfully saved and tagged Apple Music track {TrackId} -> {Path}", trackId, outputPath);

        return outputPath;
    }

    protected override Task ConvertToSpotifyIdAsync(string externalProvider, string externalId)
    {
        // No conversion needed for Spotify mapping in base downloader
        return Task.CompletedTask;
    }
}
