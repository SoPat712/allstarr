using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

public class MultiProviderMetadataService : IMusicMetadataService
{
    private readonly IEnumerable<IMusicMetadataService> _allServices;
    private readonly ProviderStatusManager _statusManager;
    private readonly ILogger<MultiProviderMetadataService> _logger;

    public MultiProviderMetadataService(
        IEnumerable<IMusicMetadataService> services,
        ProviderStatusManager statusManager,
        ILogger<MultiProviderMetadataService> logger)
    {
        _allServices = services.Where(s => s.GetType() != typeof(MultiProviderMetadataService)).ToList();
        _statusManager = statusManager;
        _logger = logger;
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();
        if (providers.Count == 0) return new List<Song>();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Song>();
            try
            {
                return await service.SearchSongsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchSongsAsync failed for provider: {Provider}", p);
                return new List<Song>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return InterleaveLists(results.ToList());
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();
        if (providers.Count == 0) return new List<Album>();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Album>();
            try
            {
                return await service.SearchAlbumsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAlbumsAsync failed for provider: {Provider}", p);
                return new List<Album>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return InterleaveLists(results.ToList());
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();
        if (providers.Count == 0) return new List<Artist>();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<Artist>();
            try
            {
                return await service.SearchArtistsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchArtistsAsync failed for provider: {Provider}", p);
                return new List<Artist>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return InterleaveLists(results.ToList());
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        var providers = _statusManager.GetEnabledSearchProviders();
        if (providers.Count == 0) return new SearchResult();

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return null;
            try
            {
                return await service.SearchAllAsync(query, songLimit, albumLimit, artistLimit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchAllAsync failed for provider: {Provider}", p);
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        var validResults = results.Where(r => r != null).ToList();

        return new SearchResult
        {
            Songs = InterleaveLists(validResults.Select(r => r!.Songs).ToList()),
            Albums = InterleaveLists(validResults.Select(r => r!.Albums).ToList()),
            Artists = InterleaveLists(validResults.Select(r => r!.Artists).ToList())
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetSongAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        // Try searching in parallel across all enabled search providers to find the first exact match
        var providers = _statusManager.GetEnabledSearchProviders();
        if (providers.Count == 0) return null;

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return null;
            try
            {
                return await service.FindSongByIsrcAsync(isrc, cancellationToken);
            }
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(s => s != null);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetAlbumAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var service = GetMetadataServiceByName(externalProvider);
        if (service == null) return null;
        return await service.GetArtistAsync(externalProvider, externalId, cancellationToken);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
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

        var tasks = providers.Select(async p =>
        {
            var service = GetMetadataServiceByName(p);
            if (service == null) return new List<ExternalPlaylist>();
            try
            {
                return await service.SearchPlaylistsAsync(query, limit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchPlaylistsAsync failed for provider: {Provider}", p);
                return new List<ExternalPlaylist>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return InterleaveLists(results.ToList());
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
            (normalizedName == "squidwtf" && s.GetType().Name.StartsWith("SquidWTF", StringComparison.OrdinalIgnoreCase)) ||
            (normalizedName == "applemusic" && s.GetType().Name.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase))
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
}
