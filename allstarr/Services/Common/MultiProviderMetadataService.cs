using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

public class MultiProviderMetadataService : IMusicMetadataService
{
    private static readonly TimeSpan ProviderSearchTimeout = TimeSpan.FromSeconds(5);
    private readonly IEnumerable<IConcreteMetadataService> _allServices;
    private readonly ProviderStatusManager _statusManager;
    private readonly ExtensionManager _extensionManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MultiProviderMetadataService> _logger;
    private readonly SemaphoreSlim _fanOutGate;

    public MultiProviderMetadataService(
        IEnumerable<IConcreteMetadataService> services,
        ProviderStatusManager statusManager,
        ExtensionManager extensionManager,
        IConfiguration configuration,
        ILogger<MultiProviderMetadataService> logger)
    {
        _allServices = services.ToList();
        _statusManager = statusManager;
        _extensionManager = extensionManager;
        _configuration = configuration;
        _logger = logger;
        _fanOutGate = new SemaphoreSlim(Math.Clamp(
            configuration.GetValue("Providers:MetadataFanoutConcurrency", 4), 1, 16));
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        return await SearchSongsFromProvidersAsync(
            providers, query, limit, includeExtensions: true, requirePlayableExtensions: false, cancellationToken);
    }

    /// <summary>
    /// Searches only providers that can currently supply audio. Catalog-only providers are
    /// intentionally excluded so playlist matching cannot select an unplayable result.
    /// </summary>
    public async Task<List<Song>> SearchPlayableSongsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledPlaybackProviders();

        return await SearchSongsFromProvidersAsync(
            providers, query, limit, includeExtensions: true, requirePlayableExtensions: true, cancellationToken);
    }

    private async Task<List<Song>> SearchSongsFromProvidersAsync(
        IEnumerable<string> providers,
        string query,
        int limit,
        bool includeExtensions,
        bool requirePlayableExtensions,
        CancellationToken cancellationToken)
    {

        var tasks = providers.Select(p => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Song>();
            try
            {
                return await RunTimedAsync(
                    token => service.SearchSongsAsync(query, limit, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchSongsAsync timed out for provider: {Provider}", p);
                return new List<Song>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchSongsAsync failed for provider: {Provider}", p);
                return new List<Song>();
            }
        }, cancellationToken)).ToList();

        var extensions = includeExtensions
            ? _extensionManager.GetActiveExtensions()
                .Where(extension => !requirePlayableExtensions || extension.Types.Any(IsPlaybackCapability))
                .ToList()
            : [];
        var extensionTasks = extensions.Select(ext => RunFanOutAsync(async () =>
        {
            try
            {
                var res = await RunTimedAsync(
                    _ => Task.Run(() => ext.Search(query, limit), cancellationToken),
                    ProviderSearchTimeout,
                    cancellationToken);
                return res.Songs;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchSongsAsync timed out for extension: {ExtensionId}", ext.Id);
                return new List<Song>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchSongsAsync failed for extension: {ExtensionId}", ext.Id);
                return new List<Song>();
            }
        }, cancellationToken));

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var providerEntries = providers.Zip(providerResults, (id, results) => (Id: id, Results: results));
        var extensionEntries = extensions.Zip(extensionResults, (extension, results) => (Id: extension.Id, Results: results));
        var resultEntries = providerEntries.Concat(extensionEntries).ToList();
        var configuredOrder = ConfiguredSearchOrder(requirePlayableExtensions);
        var allResultsList = resultEntries
            .OrderBy(entry => ProviderRank(configuredOrder, entry.Id))
            .Select(entry => entry.Results)
            .ToList();
        return InterleaveLists(allResultsList).Take(Math.Max(0, limit)).ToList();
    }

    public async Task<Song?> FindPlayableSongByIsrcAsync(
        string isrc,
        CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledPlaybackProviders().ToList();
        var tasks = providers.Select(provider => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(provider);
            if (service == null) return null;
            try
            {
                return await RunTimedAsync(
                    token => service.FindSongByIsrcAsync(isrc, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ISRC lookup failed for playback provider: {Provider}", provider);
                return null;
            }
        }, cancellationToken)).ToList();

        var extensions = _extensionManager.GetActiveExtensions()
            .Where(extension => extension.Types.Any(IsPlaybackCapability))
            .ToList();
        var extensionTasks = extensions.Select(extension => RunFanOutAsync(async () =>
        {
            try
            {
                var result = await RunTimedAsync(
                    _ => Task.Run(() => extension.Search($"isrc:{isrc}", 1), cancellationToken),
                    ProviderSearchTimeout,
                    cancellationToken);
                return result.Songs.FirstOrDefault();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ISRC lookup failed for playback extension: {ExtensionId}", extension.Id);
                return null;
            }
        }, cancellationToken)).ToList();

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);
        var configuredOrder = ConfiguredSearchOrder(playbackOnly: true);
        return providers.Zip(providerResults, (id, song) => (Id: id, Song: song))
            .Concat(extensions.Zip(extensionResults, (extension, song) => (Id: extension.Id, Song: song)))
            .OrderBy(result => ProviderRank(configuredOrder, result.Id))
            .Select(result => result.Song)
            .FirstOrDefault(song => song is not null);
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(p => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Album>();
            try
            {
                return await RunTimedAsync(
                    token => service.SearchAlbumsAsync(query, limit, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchAlbumsAsync timed out for provider: {Provider}", p);
                return new List<Album>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAlbumsAsync failed for provider: {Provider}", p);
                return new List<Album>();
            }
        }, cancellationToken)).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(ext => RunFanOutAsync(async () =>
        {
            try
            {
                var res = await RunTimedAsync(
                    _ => Task.Run(() => ext.Search(query, limit), cancellationToken),
                    ProviderSearchTimeout,
                    cancellationToken);
                return res.Albums;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchAlbumsAsync timed out for extension: {ExtensionId}", ext.Id);
                return new List<Album>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAlbumsAsync failed for extension: {ExtensionId}", ext.Id);
                return new List<Album>();
            }
        }, cancellationToken));

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var allResultsList = providerResults.Concat(extensionResults).ToList();
        return InterleaveLists(allResultsList).Take(Math.Max(0, limit)).ToList();
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(p => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Artist>();
            try
            {
                return await RunTimedAsync(
                    token => service.SearchArtistsAsync(query, limit, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchArtistsAsync timed out for provider: {Provider}", p);
                return new List<Artist>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchArtistsAsync failed for provider: {Provider}", p);
                return new List<Artist>();
            }
        }, cancellationToken)).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(ext => RunFanOutAsync(async () =>
        {
            try
            {
                var res = await RunTimedAsync(
                    _ => Task.Run(() => ext.Search(query, limit), cancellationToken),
                    ProviderSearchTimeout,
                    cancellationToken);
                return res.Artists;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchArtistsAsync timed out for extension: {ExtensionId}", ext.Id);
                return new List<Artist>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchArtistsAsync failed for extension: {ExtensionId}", ext.Id);
                return new List<Artist>();
            }
        }, cancellationToken));

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var allResultsList = providerResults.Concat(extensionResults).ToList();
        return InterleaveLists(allResultsList).Take(Math.Max(0, limit)).ToList();
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(p => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return null;
            try
            {
                return await RunTimedAsync(
                    token => service.SearchAllAsync(
                        query, songLimit, albumLimit, artistLimit, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchAllAsync timed out for provider: {Provider}", p);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAllAsync failed for provider: {Provider}", p);
                return null;
            }
        }, cancellationToken)).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(ext => RunFanOutAsync(async () =>
        {
            try
            {
                return await RunTimedAsync(
                    _ => Task.Run(() => ext.Search(query, songLimit), cancellationToken),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("SearchAllAsync timed out for extension: {ExtensionId}", ext.Id);
                return new SearchResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAllAsync failed for extension: {ExtensionId}", ext.Id);
                return new SearchResult();
            }
        }, cancellationToken));

        var providerResults = await Task.WhenAll(tasks);
        var extensionResults = await Task.WhenAll(extensionTasks);

        var validProviderResults = providerResults.Where(r => r != null).ToList();
        var validExtensionResults = extensionResults.Where(r => r != null).ToList();

        var allSongsLists = validProviderResults.Select(r => r!.Songs)
            .Concat(validExtensionResults.Select(r => r.Songs)).ToList();

        var allAlbumsLists = validProviderResults.Select(r => r!.Albums)
            .Concat(validExtensionResults.Select(r => r.Albums)).ToList();

        var allArtistsLists = validProviderResults.Select(r => r!.Artists)
            .Concat(validExtensionResults.Select(r => r.Artists)).ToList();

        return new SearchResult
        {
            Songs = InterleaveLists(allSongsLists).Take(Math.Max(0, songLimit)).ToList(),
            Albums = InterleaveLists(allAlbumsLists).Take(Math.Max(0, albumLimit)).ToList(),
            Artists = InterleaveLists(allArtistsLists).Take(Math.Max(0, artistLimit)).ToList()
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null)
        {
            return await Task.Run(() => ext.GetSong(externalId), cancellationToken);
        }

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetSongAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();

        var tasks = providers.Select(p => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return null;
            try
            {
                return await RunTimedAsync(
                    token => service.FindSongByIsrcAsync(isrc, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }, cancellationToken)).ToList();

        var extensions = _extensionManager.GetActiveExtensions();
        var extensionTasks = extensions.Select(ext => RunFanOutAsync(async () =>
        {
            try
            {
                var res = await RunTimedAsync(
                    _ => Task.Run(() => ext.Search($"isrc:{isrc}", 1), cancellationToken),
                    ProviderSearchTimeout,
                    cancellationToken);
                return res.Songs.FirstOrDefault();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var extResults = await Task.WhenAll(extensionTasks);

        return results.FirstOrDefault(s => s != null) ?? extResults.FirstOrDefault(s => s != null);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null)
        {
            return await Task.Run(() => ext.GetAlbum(externalId), cancellationToken);
        }

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetAlbumAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null)
        {
            return await Task.Run(() => ext.GetArtist(externalId), cancellationToken);
        }

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetArtistAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var ext = _extensionManager.GetExtension(externalProvider);
        if (ext != null) return new List<Album>(); // Extensions don't have separate artist-albums endpoint usually

        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return new List<Album>();
        return await service.GetArtistAlbumsAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return new List<Song>();
        return await service.GetArtistTracksAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledPlaylistProviders();
        if (providers.Count == 0) return new List<ExternalPlaylist>();

        var tasks = providers.Select(p => RunFanOutAsync(async () =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<ExternalPlaylist>();
            try
            {
                return await RunTimedAsync(
                    token => service.SearchPlaylistsAsync(query, limit, token),
                    ProviderSearchTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchPlaylistsAsync failed for provider: {Provider}", p);
                return new List<ExternalPlaylist>();
            }
        }, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return InterleaveLists(results.ToList()).Take(Math.Max(0, limit)).ToList();
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetPlaylistAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return new List<Song>();
        return await service.GetPlaylistTracksAsync(externalProvider, externalId, cancellationToken);
    }

    private IMusicMetadataService? GetMetadataServiceByName(string name)
    {
        var normalizedName = name.ToLowerInvariant();
        return _allServices.FirstOrDefault(s =>
            s.GetType().Name.StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase) ||
            (normalizedName is "apple-download" or "applemusic" && s.GetType().Name.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase))
        );
    }

    private List<T> InterleaveLists<T>(List<List<T>> lists)
    {
        var result = new List<T>();
        var nonNullLists = lists.Where(l => l != null && l.Count > 0).ToList();
        if (nonNullLists.Count == 0) return result;

        int maxCount = nonNullLists.Max(l => l.Count);
        for (int i = 0; i < maxCount; i++)
        {
            foreach (var list in nonNullLists)
            {
                if (i < list.Count)
                {
                    result.Add(list[i]);
                }
            }
        }
        return result;
    }

    private static bool IsPlaybackCapability(string capability)
    {
        var normalized = capability.Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "stream" or "streaming" or "download" or "downloads";
    }

    private IReadOnlyList<string> ConfiguredSearchOrder(bool playbackOnly)
    {
        IEnumerable<string> values = playbackOnly
            ? [
                _configuration["Providers:StreamingOrder"] ?? _configuration["MULTI_PROVIDER_STREAMING_ORDER"] ?? "apple-download,deezer,qobuz",
                _configuration["Providers:DownloadOrder"] ?? _configuration["MULTI_PROVIDER_DOWNLOAD_ORDER"] ?? "apple-download,deezer,qobuz"
              ]
            : [
                _configuration["Providers:MetadataOrder"] ?? _configuration["MULTI_PROVIDER_METADATA_ORDER"] ?? "apple-download,deezer,qobuz"
              ];

        var configuredOrder = values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!playbackOnly)
        {
            return configuredOrder;
        }

        var extensionPlaybackProviders = _extensionManager
            .GetActiveExtensions()
            .Where(ext => ext.Types.Any(IsPlaybackCapability))
            .Select(ext => ext.Id.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToList();

        return configuredOrder
            .Concat(extensionPlaybackProviders.Where(id => !configuredOrder.Contains(id, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    private static int ProviderRank(IReadOnlyList<string> configuredOrder, string providerId)
    {
        for (var index = 0; index < configuredOrder.Count; index++)
        {
            if (configuredOrder[index].Equals(providerId, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return configuredOrder.Count;
    }

    private async Task<T> RunFanOutAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _fanOutGate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _fanOutGate.Release();
        }
    }

    internal static async Task<T> RunTimedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var task = operation(deadline.Token);
        try
        {
            return await task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await task;
            }
            catch
            {
                // The timed-out operation must drain before its concurrency permit is reused.
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The provider search deadline expired.");
        }
    }
}
