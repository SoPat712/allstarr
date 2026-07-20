using allstarr.Models.Domain;
using allstarr.Models.Download;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

public class MultiProviderDownloadService : IDownloadService
{
    private readonly IEnumerable<IConcreteDownloadService> _allServices;
    private readonly IMusicMetadataService _metadataService;
    private readonly ProviderStatusManager _statusManager;
    private readonly OdesliService _odesliService;
    private readonly ILogger<MultiProviderDownloadService> _logger;
    private readonly IEnumerable<IConcreteMetadataService> _allMetadataServices;

    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var downloadProviders = GetPrioritizedDownloadProviders();
        if (downloadProviders.Count == 0)
        {
            throw new InvalidOperationException("No download providers are currently enabled and healthy.");
        }

        Exception? lastException = null;

        foreach (var targetProvider in downloadProviders)
        {
            try
            {
                _logger.LogInformation("Attempting download using target provider: {TargetProvider}", targetProvider);

                string targetId = externalId;
                if (!ProviderIdsEquivalent(externalProvider, targetProvider))
                {
                    var translatedId = await TranslateIdAsync(externalProvider, externalId, targetProvider, cancellationToken);
                    if (string.IsNullOrEmpty(translatedId))
                    {
                        _logger.LogWarning("Could not translate track ID from {SourceProvider}:{SourceId} to {TargetProvider}", externalProvider, externalId, targetProvider);
                        continue;
                    }
                    targetId = translatedId;
                }

                var service = GetDownloadServiceByName(targetProvider);
                if (service == null)
                {
                    _logger.LogWarning("Download service for {Provider} is not registered", targetProvider);
                    continue;
                }

                var path = await service.DownloadSongAsync(targetProvider, targetId, cancellationToken);
                if (!string.IsNullOrEmpty(path))
                {
                    _logger.LogInformation("Successfully downloaded song using provider {Provider}: {Path}", targetProvider, path);
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download failed using provider {Provider}", targetProvider);
                lastException = ex;
            }
        }

        throw new InvalidOperationException("All configured download services failed to download the song.", lastException);
    }

    public async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, Common.StreamQuality? qualityOverride = null, CancellationToken cancellationToken = default)
    {
        var streamingProviders = GetPrioritizedStreamingProviders();
        if (streamingProviders.Count == 0)
        {
            throw new InvalidOperationException("No streaming providers are currently enabled and healthy.");
        }

        Exception? lastException = null;

        foreach (var targetProvider in streamingProviders)
        {
            try
            {
                _logger.LogInformation("Attempting streaming using target provider: {TargetProvider}", targetProvider);

                string targetId = externalId;
                if (!ProviderIdsEquivalent(externalProvider, targetProvider))
                {
                    var translatedId = await TranslateIdAsync(externalProvider, externalId, targetProvider, cancellationToken);
                    if (string.IsNullOrEmpty(translatedId))
                    {
                        _logger.LogWarning("Could not translate track ID from {SourceProvider}:{SourceId} to {TargetProvider} for streaming", externalProvider, externalId, targetProvider);
                        continue;
                    }
                    targetId = translatedId;
                }

                var service = GetDownloadServiceByName(targetProvider);
                if (service == null)
                {
                    _logger.LogWarning("Download service for {Provider} is not registered", targetProvider);
                    continue;
                }

                var stream = await service.DownloadAndStreamAsync(targetProvider, targetId, qualityOverride, cancellationToken);
                if (stream != null)
                {
                    _logger.LogInformation("Successfully opened stream using provider {Provider}", targetProvider);
                    return stream;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming failed using provider {Provider}", targetProvider);
                lastException = ex;
            }
        }

        throw new InvalidOperationException("All configured download services failed to stream the song.", lastException);
    }

    private static bool ProviderIdsEquivalent(string left, string right) =>
        CanonicalProviderId(left).Equals(CanonicalProviderId(right), StringComparison.OrdinalIgnoreCase);

    private static string CanonicalProviderId(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "applemusic" => "apple-download",
        _ => provider.Trim().ToLowerInvariant()
    };

    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId)
    {
        var providers = GetPrioritizedDownloadProviders();
        if (providers.Count == 0) return;

        var service = GetDownloadServiceByName(providers[0]);
        if (service != null)
        {
            service.DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, excludeTrackExternalId);
        }
    }

    public DownloadInfo? GetDownloadStatus(string songId)
    {
        foreach (var service in _allServices)
        {
            var status = service.GetDownloadStatus(songId);
            if (status != null) return status;
        }
        return null;
    }

    public IReadOnlyList<DownloadInfo> GetActiveDownloads()
    {
        return _allServices.SelectMany(s => s.GetActiveDownloads()).ToList();
    }

    public async Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId)
    {
        var service = GetDownloadServiceByName(externalProvider);
        if (service != null)
        {
            var path = await service.GetLocalPathIfExistsAsync(externalProvider, externalId);
            if (path != null) return path;
        }

        foreach (var s in _allServices)
        {
            var path = await s.GetLocalPathIfExistsAsync(externalProvider, externalId);
            if (path != null) return path;
        }

        return null;
    }

    public async Task<bool> IsAvailableAsync()
    {
        return GetPrioritizedDownloadProviders().Count > 0;
    }

    private IReadOnlyList<string> GetPrioritizedDownloadProviders()
    {
        return _statusManager.GetEnabledDownloadProviders();
    }

    private IReadOnlyList<string> GetPrioritizedStreamingProviders()
    {
        return _statusManager.GetEnabledStreamingProviders();
    }

    private IDownloadService? GetDownloadServiceByName(string name)
    {
        var normalizedName = name.ToLowerInvariant();
        return _allServices.FirstOrDefault(s =>
            s.GetType().Name.StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase) ||
            (normalizedName == "squidwtf" && s.GetType().Name.StartsWith("SquidWTF", StringComparison.OrdinalIgnoreCase)) ||
            (normalizedName is "apple-download" or "applemusic" && s.GetType().Name.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase))
        );
    }

    private IMusicMetadataService? GetMetadataServiceByName(string name)
    {
        // Obtain metadata services from DI indirectly or filter from metadata service if needed,
        // but since metadataService resolves to MultiProviderMetadataService, we can just inject
        // IEnumerable<IMusicMetadataService> to find the concrete one!
        // To be safe and clean, let's resolve this from the MultiProviderMetadataService itself,
        // or we can pass IEnumerable<IMusicMetadataService> to MultiProviderDownloadService as well!
        // Let's check: can we inject both? Yes! Let's do that!
        return null; // Will be mapped dynamically in constructor if we inject IEnumerable<IMusicMetadataService>
    }


    public MultiProviderDownloadService(
        IEnumerable<IConcreteDownloadService> services,
        IEnumerable<IConcreteMetadataService> metadataServices,
        IMusicMetadataService metadataService,
        ProviderStatusManager statusManager,
        OdesliService odesliService,
        ILogger<MultiProviderDownloadService> logger)
    {
        _allServices = services.ToList();
        _allMetadataServices = metadataServices.ToList();
        _metadataService = metadataService;
        _statusManager = statusManager;
        _odesliService = odesliService;
        _logger = logger;
    }

    private IMusicMetadataService? GetConcreteMetadataServiceByName(string name)
    {
        var normalizedName = name.ToLowerInvariant();
        return _allMetadataServices.FirstOrDefault(s =>
            s.GetType().Name.StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase) ||
            (normalizedName == "squidwtf" && s.GetType().Name.StartsWith("SquidWTF", StringComparison.OrdinalIgnoreCase)) ||
            (normalizedName is "apple-download" or "applemusic" && s.GetType().Name.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase))
        );
    }

    private async Task<string?> TranslateIdAsync(string sourceProvider, string sourceId, string targetProvider, CancellationToken cancellationToken)
    {
        var sourceSong = await _metadataService.GetSongAsync(sourceProvider, sourceId, cancellationToken);
        if (sourceSong == null) return null;

        var sourceUrl = GetTrackUrl(sourceProvider, sourceId, sourceSong);
        if (!string.IsNullOrEmpty(sourceUrl))
        {
            var odesliId = await _odesliService.TranslateTrackUrlAsync(sourceUrl, targetProvider, cancellationToken);
            if (!string.IsNullOrEmpty(odesliId))
            {
                _logger.LogInformation("Translated track using Odesli: {SourceProvider}:{SourceId} -> {TargetProvider}:{TargetId}", sourceProvider, sourceId, targetProvider, odesliId);
                return odesliId;
            }
        }

        if (!string.IsNullOrEmpty(sourceSong.Isrc))
        {
            var targetMetadataService = GetConcreteMetadataServiceByName(targetProvider);
            if (targetMetadataService != null)
            {
                var match = await targetMetadataService.FindSongByIsrcAsync(sourceSong.Isrc, cancellationToken);
                if (match != null && !string.IsNullOrEmpty(match.ExternalId))
                {
                    _logger.LogInformation("Translated track using ISRC {Isrc}: {SourceProvider}:{SourceId} -> {TargetProvider}:{TargetId}", sourceSong.Isrc, sourceProvider, sourceId, targetProvider, match.ExternalId);
                    return match.ExternalId;
                }
            }
        }

        var targetMetadataServiceForSearch = GetConcreteMetadataServiceByName(targetProvider);
        if (targetMetadataServiceForSearch != null)
        {
            var query = $"{sourceSong.Title} {sourceSong.Artist}";
            var results = await targetMetadataServiceForSearch.SearchSongsAsync(query, 5, cancellationToken);
            var match = results.FirstOrDefault(r =>
                r.Title.Contains(sourceSong.Title, StringComparison.OrdinalIgnoreCase) ||
                sourceSong.Title.Contains(r.Title, StringComparison.OrdinalIgnoreCase));

            if (match != null && !string.IsNullOrEmpty(match.ExternalId))
            {
                _logger.LogInformation("Translated track using text search mapping: {SourceProvider}:{SourceId} -> {TargetProvider}:{TargetId}", sourceProvider, sourceId, targetProvider, match.ExternalId);
                return match.ExternalId;
            }
        }

        return null;
    }

    private string? GetTrackUrl(string provider, string id, Song song)
    {
        return provider.ToLowerInvariant() switch
        {
            "spotify" => $"https://open.spotify.com/track/{id}",
            "deezer" => $"https://www.deezer.com/track/{id}",
            "applemusic" or "apple-download" => $"https://music.apple.com/us/song/{id}",
            "qobuz" => $"https://open.qobuz.com/track/{id}",
            "squidwtf" => $"https://tidal.com/browse/track/{id}",
            "tidal" => $"https://tidal.com/browse/track/{id}",
            _ => null
        };
    }
}
