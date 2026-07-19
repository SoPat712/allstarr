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

namespace allstarr.Services.AppleMusic;

public class AppleMusicDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly IAppleDownloadEndpointDiscovery _endpointDiscovery;

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
        // Settings define the provider ceiling. Jellyfin's request is translated to
        // the closest Apple tier at or below that ceiling.
        var qualityStr = quality switch
        {
            StreamQuality.High => AppleDownloadCapabilityAdapter.Quality(
                ProviderAudioQuality.Lossy,
                _appleMusicSettings.Quality),
            StreamQuality.Low => AppleDownloadCapabilityAdapter.ClampQuality(
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
