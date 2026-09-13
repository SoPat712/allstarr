using allstarr.Models.Domain;
using allstarr.Models.Download;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

public sealed class MultiProviderDownloadService : IDownloadService
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
        cancellationToken.ThrowIfCancellationRequested();
        var savedProvider = ConcreteProviderId.Normalize(externalProvider);
        if (!_statusManager.GetEnabledPlaybackProviders().Contains(
                savedProvider,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The saved provider cannot stream this track and no exact fallback route is available.");
        }

        var service = GetDownloadServiceByName(savedProvider);
        if (service == null)
        {
            throw new NotSupportedException(
                "The saved provider cannot stream this track and no exact fallback route is available.");
        }

        _logger.LogInformation(
            "Opening stream with chosen provider {ChosenProvider} and serving provider {ServingProvider}",
            savedProvider,
            savedProvider);
        return await service.DownloadAndStreamAsync(
            savedProvider,
            externalId,
            qualityOverride,
            cancellationToken);
    }

    private static bool ProviderIdsEquivalent(string left, string right) =>
        ConcreteProviderId.Normalize(left).Equals(
            ConcreteProviderId.Normalize(right), StringComparison.Ordinal);

    public DownloadInfo? GetDownloadStatus(string songId)
    {
        foreach (var service in _allServices)
        {
            var status = service.GetDownloadStatus(songId);
            if (status != null) return status;
        }
        return null;
    }

    public IReadOnlyList<DownloadInfo> GetActiveDownloads() =>
        _allServices.SelectMany(service => service.GetActiveDownloads()).ToList();

    public async Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId)
    {
        var service = GetDownloadServiceByName(externalProvider);
        return service == null
            ? null
            : await service.GetLocalPathIfExistsAsync(service.ProviderId, externalId);
    }

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(GetPrioritizedDownloadProviders().Count > 0);

    private IReadOnlyList<string> GetPrioritizedDownloadProviders() =>
        _statusManager.GetEnabledDownloadProviders();

    private IConcreteDownloadService? GetDownloadServiceByName(string name) =>
        _allServices.FirstOrDefault(service => service.ProviderId.Equals(
            ConcreteProviderId.Normalize(name), StringComparison.Ordinal));

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

    private IConcreteMetadataService? GetConcreteMetadataServiceByName(string name) =>
        _allMetadataServices.FirstOrDefault(service => service.ProviderId.Equals(
            ConcreteProviderId.Normalize(name), StringComparison.Ordinal));

    private async Task<string?> TranslateIdAsync(string sourceProvider, string sourceId, string targetProvider, CancellationToken cancellationToken)
    {
        var sourceSong = await _metadataService.GetSongAsync(sourceProvider, sourceId, cancellationToken);
        if (sourceSong == null) return null;

        var sourceUrl = OdesliService.BuildTrackUrl(sourceProvider, sourceId);
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

}
